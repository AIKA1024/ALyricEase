#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Microsoft.Extensions.Logging;
using ExoPlayerType = AndroidX.Media3.ExoPlayer.IExoPlayer;

namespace ALyricEase.Services.Audio;

/// <summary>Android 音频播放(Media3/ExoPlayer),替代 android.media.MediaPlayer 黑盒。
/// 动机(2026-10-26 真机日志排查):MediaPlayer 流播对 VBR 源的 Duration 按码率估算、
/// 明显偏低,交叉淡化据此提前触发,表现为"歌的后半段直接切歌";且断流直接 Completion
/// 被当成"播完"。ExoPlayer 取容器真实元数据,自带缓冲重试,HTTP 头/重定向可控。
///
/// 线程模型:ExoPlayer 实例必须在创建线程(UI)访问,轮询改用主线程 Handler
/// (旧 MediaPlayer 后端是线程池 Timer,只读属性虽无害,这里一并收敛到 UI 线程)。
/// 流播仍保留"下载到缓存文件"兜底:ExoPlayer 的错误恢复覆盖绝大多数场景,但个别
/// CDN/明文组合仍可能起播失败,兜底保证"能响"优先(与旧后端行为一致)。</summary>
public sealed class ExoPlayerMediaPlayer : IAudioPlayer
{
  private const string Tag = "ALyricEasePlayer";
  private const int PollIntervalMs = 200;
  private const string UserAgent =
      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

  // androidx.media3.common.Player 的 STATE_* 稳定常量(API 文档承诺不变)。
  // 绑定侧是接口静态成员,不同绑定版本命名有差异,直接用数值规避。
  private const int NativeStateIdle = 1;
  private const int NativeStateBuffering = 2;
  private const int NativeStateReady = 3;
  private const int NativeStateEnded = 4;
  // C.TIME_UNSET:ExoPlayer 时长未知的哨兵值。
  private const long TimeUnset = long.MinValue + 1;

  private readonly DispatcherService _dispatcher;
  private readonly ILogger<ExoPlayerMediaPlayer> _fileLog;
  private readonly ExoPlayerType _player;
  private readonly PlayerListener _listener;
  private readonly Android.OS.Handler _pollHandler;
  private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
  private readonly object _lock = new();

  private PlaybackState _state = PlaybackState.Idle;
  private bool _released;
  private long _lastPosMs = -1;
  private long _lastDurMs;
  private string? _cachePath;
  private string? _currentUrl;
  private readonly object _analysisLock = new();
  private global::Android.Media.Audiofx.Visualizer? _visualizer;
  private VisualizerListener? _visualizerListener;
  private int _visualizerSession = -1;
  private bool _audioAnalysisRequested;
  private float _audioEnergy;
  private float _bassEnergy;
  private float _waveEnergy;

  // 代数号:每次 PlayUrl/Stop 自增,迟到的错误/下载完成回调据此丢弃,防竞态。
  private int _generation;
  // 当前处于哪种播放模式:ExoPlayer 起播失败时降级为下载,下载模式再失败才报错。
  private bool _downloading;

  public ExoPlayerMediaPlayer(DispatcherService dispatcher, ILogger<ExoPlayerMediaPlayer>? fileLog = null)
  {
    _dispatcher = dispatcher;
    _fileLog = fileLog ?? NullLogger<ExoPlayerMediaPlayer>.Instance;

    // http<->https 互跳必须打开(网易云 CDN 常见重定向);UA/Referer 与旧后端一致
    var httpFactory = new AndroidX.Media3.DataSource.DefaultHttpDataSource.Factory()
        .SetUserAgent(UserAgent)
        .SetAllowCrossProtocolRedirects(true)
        .SetDefaultRequestProperties(new Dictionary<string, string>
        {
            ["Referer"] = "https://music.163.com",
        });

    // media3 1.11:ExoPlayer 接口化后 Builder 扁平化为 ExoPlayerBuilder 类,
    // DefaultMediaSourceFactory 移除了带 DataSource.Factory 的构造器,改无参 + 流式设置
    _player = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(global::Android.App.Application.Context)
        .SetMediaSourceFactory(new AndroidX.Media3.ExoPlayer.Source.DefaultMediaSourceFactory()
            .SetDataSourceFactory(httpFactory))
        .Build();
    _listener = new PlayerListener(this);
    _player.AddListener(_listener);

    // ExoPlayer 单线程契约:所有访问(含轮询读)都在创建线程,即主线程
    _pollHandler = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
    _pollHandler.PostDelayed(Poll, PollIntervalMs);
  }

  // ---- 生命周期与原生事件(UI 线程) ----

  private void OnPlaybackStateChanged(int playbackState)
  {
    if (playbackState == NativeStateEnded)
    {
      // 时点证据:自然播完应满足 PositionMs≈DurationMs;若远小于,即流被提前终止
      // (CDN 断流等),上层会当作播完自动切歌——靠这行区分两种切歌原因。
      long pos = 0, dur = 0;
      try { pos = _player.CurrentPosition; dur = _player.Duration; } catch { }
      _fileLog.LogInformation("Native playback completed. PositionMs={PositionMs} DurationMs={DurationMs} Mode={Mode}",
          pos, dur, _downloading ? "download" : "stream");
      _dispatcher.Post(() => RaiseState(PlaybackState.Idle));
    }
  }

  private void OnIsPlayingChanged(bool isPlaying)
  {
    if (isPlaying)
    {
      EnsureAudioVisualizer();
      _dispatcher.Post(() => RaiseState(PlaybackState.Playing));
    }
  }

  private void OnPlayerError(global::AndroidX.Media3.Common.PlaybackException? error)
  {
    Log($"ExoPlayer error: {error?.Message}");
    // UI 文案只给概要;完整错误(错误码/堆栈)进文件日志,便于事后排查
    var message = $"播放出错({error?.Message ?? "unknown"})";
    _fileLog.LogError("ExoPlayer error. Detail={Detail} Mode={Mode} PositionMs={PositionMs}",
        error?.ToString(), _downloading ? "download" : "stream", PositionMs);

    int gen;
    string? url;
    lock (_lock)
    {
      gen = _generation;
      url = _currentUrl;
    }
    if (!_downloading && url is { Length: > 0 })
    {
      // 流播失败,降级为下载后播本地文件
      Log("Falling back to download mode");
      _fileLog.LogInformation("Falling back to download mode");
      _dispatcher.Post(() =>
      {
        if (gen != Volatile.Read(ref _generation)) return;
        _player.Stop();
        _player.ClearMediaItems();
        RaiseState(PlaybackState.Loading);
        _ = DownloadAndPlayAsync(url, gen);
      });
      return;
    }
    _dispatcher.Post(() =>
    {
      if (gen != Volatile.Read(ref _generation)) return;
      ErrorOccurred?.Invoke(this, message);
      _player.Stop();
      _player.ClearMediaItems();
      RaiseState(PlaybackState.Idle);
    });
  }

  // ---- IAudioPlayer 基本控制 ----

  public PlaybackState State => _state;

  public long PositionMs
  {
    get
    {
      try { return _released ? 0 : _player.CurrentPosition; }
      catch { return 0; }
    }
    set
    {
      try { if (!_released) _player.SeekTo(Math.Max(0, value)); }
      catch { }
    }
  }

  public long DurationMs
  {
    get
    {
      try
      {
        if (_released) return 0;
        var d = _player.Duration;
        return d == TimeUnset ? 0 : d;
      }
      catch { return 0; }
    }
  }

  private int _volume;

  public int Volume
  {
    get => _volume;
    set
    {
      _volume = value;
      var v = Math.Clamp(value, 0, 100) / 100f;
      _player.Volume = v;
    }
  }

  public event EventHandler? StateChanged;

  public event EventHandler<long>? PositionChanged;

  public event EventHandler<long>? DurationChanged;

  public event EventHandler<string>? ErrorOccurred;

  public void PlayUrl(string url)
  {
    Log($"PlayUrl: {url}");
    StopInternal(releaseState: false);

    if (Path.IsPathRooted(url) && File.Exists(url))
    {
      lock (_lock)
      {
        _generation++;
        _currentUrl = url;
        _downloading = false;
      }

      RaiseState(PlaybackState.Loading);
      try
      {
        _player.SetMediaItem(new AndroidX.Media3.Common.MediaItem.Builder()
            .SetUri(global::Android.Net.Uri.FromFile(new Java.IO.File(url))).Build());
        _player.Prepare();
        _player.Play();
        Log("SetMediaItem(cached local) OK");
      }
      catch (Exception ex)
      {
        Log($"Cached local play exception: {ex.Message}");
        _fileLog.LogWarning("Local play exception. Detail={Detail}", ex.Message);
        ErrorOccurred?.Invoke(this, $"播放出错({ex.Message})");
        RaiseState(PlaybackState.Idle);
      }
      return;
    }

    // http 明文 URL 换 https,规避 native 网络栈明文限制(与旧后端一致)
    var streamUrl = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        ? "https://" + url["http://".Length..]
        : url;

    lock (_lock)
    {
      _generation++;
      _currentUrl = streamUrl;
      _downloading = false;
    }

    RaiseState(PlaybackState.Loading);
    try
    {
      _player.SetMediaItem(new AndroidX.Media3.Common.MediaItem.Builder()
          .SetUri(global::Android.Net.Uri.Parse(streamUrl)!).Build());
      _player.Prepare();
      _player.Play();
      Log("SetMediaItem(stream) OK");
    }
    catch (Exception ex)
    {
      Log($"Stream SetMediaItem exception: {ex.Message}");
      _fileLog.LogWarning("Stream set exception, falling back to download. Detail={Detail}", ex.Message);
      // native 层直接拒绝,走下载兜底
      _ = DownloadAndPlayAsync(streamUrl, Volatile.Read(ref _generation));
    }
  }

  /// <summary>兜底路径:把音频下载到缓存文件再播本地文件。</summary>
  private async Task DownloadAndPlayAsync(string url, int gen)
  {
    try
    {
      Log("Downloading...");
      lock (_lock) _downloading = true;
      var dir = global::Android.App.Application.Context.CacheDir!.AbsolutePath;
      var target = Path.Combine(dir, $"playback_{Guid.NewGuid():N}.mp3");
      using (var req = new HttpRequestMessage(HttpMethod.Get, url))
      {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");
        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
          Log($"Download failed status={(int)resp.StatusCode}");
          _fileLog.LogWarning("Download fallback failed. StatusCode={StatusCode}", (int)resp.StatusCode);
          _dispatcher.Post(() =>
          {
            if (gen != Volatile.Read(ref _generation)) return;
            ErrorOccurred?.Invoke(this, $"播放出错(HTTP {(int)resp.StatusCode})");
            RaiseState(PlaybackState.Idle);
          });
          return;
        }

        await using (var fs = File.Create(target))
          await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
      }

      Log($"Downloaded to {target}");
      if (gen != Volatile.Read(ref _generation))
      {
        TryDelete(target);
        return;
      }

      _dispatcher.Post(() =>
      {
        if (gen != Volatile.Read(ref _generation))
        {
          TryDelete(target);
          return;
        }
        try
        {
          _cachePath = target;
          _player.SetMediaItem(new AndroidX.Media3.Common.MediaItem.Builder()
              .SetUri(global::Android.Net.Uri.FromFile(new Java.IO.File(target))).Build());
          _player.Prepare();
          _player.Play();
          Log("SetMediaItem(local) OK");
        }
        catch (Exception ex)
        {
          Log($"Local play exception: {ex.Message}");
          ErrorOccurred?.Invoke(this, $"播放出错({ex.Message})");
          RaiseState(PlaybackState.Idle);
        }
      });
    }
    catch (Exception ex)
    {
      Log($"Download exception: {ex.Message}");
      _fileLog.LogWarning("Download fallback exception. Detail={Detail}", ex.Message);
      _dispatcher.Post(() =>
      {
        if (gen != Volatile.Read(ref _generation)) return;
        ErrorOccurred?.Invoke(this, $"播放出错({ex.Message})");
        RaiseState(PlaybackState.Idle);
      });
    }
  }

  public void Pause()
  {
    try { _player.Pause(); } catch { }
  }

  public void Resume()
  {
    try { _player.Play(); } catch { }
  }

  public void Stop()
  {
    StopInternal(releaseState: true);
  }

  /// <summary>清空音源与缓存文件。releaseState=false 供 PlayUrl 复用(随后自会报 Loading)。</summary>
  private void StopInternal(bool releaseState)
  {
    lock (_lock)
    {
      _generation++;
      _currentUrl = null;
      _downloading = false;
    }
    ReleaseAudioVisualizer();
    try { if (!_released) _player.Stop(); } catch { }
    try { if (!_released) _player.ClearMediaItems(); } catch { }
    DeleteCacheFile();
    _lastPosMs = -1;
    _lastDurMs = 0;
    if (releaseState) RaiseState(PlaybackState.Idle);
  }

  public void Dispose()
  {
    if (_released) return;
    _released = true;
    _audioAnalysisRequested = false;
    ReleaseAudioVisualizer();
    _pollHandler.RemoveCallbacks(Poll);
    try { _player.RemoveListener(_listener); } catch { }
    try { _player.Release(); } catch { }
    _http.Dispose();
    DeleteCacheFile();
  }

  private void DeleteCacheFile()
  {
    if (_cachePath is { } path) TryDelete(path);
    _cachePath = null;
  }

  private static void TryDelete(string path)
  {
    try
    {
      if (File.Exists(path)) File.Delete(path);
    }
    catch
    {
    }
  }

  /// <summary>200ms 轮询:进度/时长上报与状态收敛(自然播完/暂停判定)。
  /// 在主线程执行,满足 ExoPlayer 单线程契约。</summary>
  private void Poll()
  {
    if (_released) return;
    try
    {
      var pos = PositionMs;
      var dur = DurationMs;
      if (pos != _lastPosMs)
      {
        _lastPosMs = pos;
        var p = pos;
        _dispatcher.Post(() => PositionChanged?.Invoke(this, p));
      }

      if (dur != _lastDurMs)
      {
        _lastDurMs = dur;
        var d = dur;
        _dispatcher.Post(() => DurationChanged?.Invoke(this, d));
      }

      // ExoPlayer 状态映射:ENDED=播完(→Idle 自动切歌)、BUFFERING=Loading、READY 看是否出声
      var native = _player.PlaybackState;
      if (native == NativeStateEnded)
        RaiseState(PlaybackState.Idle);
      else if (native == NativeStateBuffering)
        RaiseState(PlaybackState.Loading);
      else if (native == NativeStateReady)
      {
        if (_player.IsPlaying)
          RaiseState(PlaybackState.Playing);
        else if (_state == PlaybackState.Playing)
          RaiseState(PlaybackState.Paused);
      }
      // NativeStateIdle:Stop 后的正常静止或错误兜底,不经 Poll 改状态

      EnsureAudioVisualizer();
    }
    catch
    {
    }
    _pollHandler.PostDelayed(Poll, PollIntervalMs);
  }

  // ---- 音频输出设备(AudioManager.GetDevices + ExoPlayer.SetPreferredDevice,均需 API 23) ----

  private IReadOnlyDictionary<string, global::Android.Media.AudioDeviceInfo> _deviceCache =
      new Dictionary<string, global::Android.Media.AudioDeviceInfo>(StringComparer.Ordinal);
  private IReadOnlyList<AudioOutputDevice> _outputDevices = Array.Empty<AudioOutputDevice>();
  private global::Android.Media.AudioDeviceInfo? _outputDevice;
  private string? _outputDeviceId;

  /// <summary>API 23 才有 GetDevices;更老的系统沿用系统默认输出。</summary>
  public bool SupportsOutputDeviceSelection =>
      OperatingSystem.IsAndroidVersionAtLeast(23) && TryGetAudioManager() is not null;

  public IReadOnlyList<AudioOutputDevice> OutputDevices => _outputDevices;

  public string? OutputDeviceId => _outputDeviceId;

  /// <summary>枚举本机输出设备(sink)。全同步本地调用,只是要拿 AudioManager 走 Context。</summary>
  public Task<IReadOnlyList<AudioOutputDevice>> RefreshOutputDevicesAsync()
  {
    var manager = TryGetAudioManager();
    if (manager is null || !OperatingSystem.IsAndroidVersionAtLeast(23))
      return Task.FromResult<IReadOnlyList<AudioOutputDevice>>(Array.Empty<AudioOutputDevice>());

    try
    {
      var infos = manager.GetDevices(global::Android.Media.GetDevicesTargets.Outputs) ?? [];
      var list = new List<AudioOutputDevice>(infos.Length);
      var map = new Dictionary<string, global::Android.Media.AudioDeviceInfo>(infos.Length, StringComparer.Ordinal);
      foreach (var info in infos)
      {
        if (info is null || !info.IsSink) continue; // 只要输出端(sink)
        var id = info.Id.ToString();
        map[id] = info;
        // ProductName 在部分机型上是空的,退化成类型名(如"扬声器"/"蓝牙设备")
        var name = string.IsNullOrWhiteSpace(info.ProductName)
            ? DescribeDeviceType(info.Type)
            : info.ProductName!;
        list.Add(new AudioOutputDevice(id, name, info.Type == global::Android.Media.AudioDeviceType.BuiltinSpeaker));
      }

      _deviceCache = map;
      _outputDevices = list;
      return Task.FromResult(_outputDevices);
    }
    catch (Exception ex)
    {
      Log($"RefreshOutputDevices failed: {ex.Message}");
      _deviceCache = new Dictionary<string, global::Android.Media.AudioDeviceInfo>(StringComparer.Ordinal);
      _outputDevices = Array.Empty<AudioOutputDevice>();
      return Task.FromResult(_outputDevices);
    }
  }

  public bool TrySetOutputDevice(string? deviceId)
  {
    if (string.IsNullOrEmpty(deviceId))
    {
      _outputDevice = null;
      _outputDeviceId = null;
      return ApplyPreferredDevice(); // 传 null 即请求回到系统默认路由
    }

    if (!_deviceCache.TryGetValue(deviceId, out var info)) return false; // 设备已断开
    _outputDevice = info;
    _outputDeviceId = deviceId;
    return ApplyPreferredDevice();
  }

  /// <summary>把当前选中的设备贴到播放器上。ExoPlayer 在任何状态下都接受该调用;
  /// 未起播时只记住偏好,出声后由音频路由自然生效。</summary>
  private bool ApplyPreferredDevice()
  {
    try
    {
      if (_released) return false;
      _player.SetPreferredAudioDevice(_outputDevice);
      return true;
    }
    catch (Exception ex)
    {
      Log($"SetPreferredAudioDevice failed: {ex.Message}");
      return false;
    }
  }

  private static Android.Media.AudioManager? TryGetAudioManager()
  {
    try
    {
      return global::Android.App.Application.Context
          .GetSystemService(global::Android.Content.Context.AudioService)
          as global::Android.Media.AudioManager;
    }
    catch
    {
      return null;
    }
  }

  /// <summary>设备类型的中文标签(ProductName 为空时的兜底名,也用于识别内置扬声器)。</summary>
  private static string DescribeDeviceType(global::Android.Media.AudioDeviceType type) => type switch
  {
    global::Android.Media.AudioDeviceType.BuiltinSpeaker => "内置扬声器",
    global::Android.Media.AudioDeviceType.BuiltinEarpiece => "听筒",
    global::Android.Media.AudioDeviceType.WiredHeadset => "有线耳机",
    global::Android.Media.AudioDeviceType.WiredHeadphones => "有线耳机",
    global::Android.Media.AudioDeviceType.BluetoothA2dp => "蓝牙音频设备",
    global::Android.Media.AudioDeviceType.BluetoothSco => "蓝牙通话设备",
    global::Android.Media.AudioDeviceType.BleHeadset or global::Android.Media.AudioDeviceType.BleSpeaker => "蓝牙 LE 设备",
    global::Android.Media.AudioDeviceType.UsbDevice or global::Android.Media.AudioDeviceType.UsbHeadset => "USB 音频设备",
    global::Android.Media.AudioDeviceType.Hdmi => "HDMI 音频设备",
    global::Android.Media.AudioDeviceType.LineAnalog => "模拟音频输出",
    global::Android.Media.AudioDeviceType.LineDigital => "数字音频输出",
    global::Android.Media.AudioDeviceType.AuxLine => "AUX 音频设备",
    global::Android.Media.AudioDeviceType.HearingAid => "助听设备",
    global::Android.Media.AudioDeviceType.Dock => "底座音频",
    global::Android.Media.AudioDeviceType.FmTuner => "FM 调谐器",
    _ => $"音频设备({type})",
  };

  // ---- 音频响应背景(Visualizer 波形/频谱) ----

  public float AudioEnergy => Volatile.Read(ref _audioEnergy);

  public float BassEnergy => Volatile.Read(ref _bassEnergy);

  public void SetAudioAnalysisEnabled(bool enabled)
  {
    _audioAnalysisRequested = enabled;
    if (!enabled)
    {
      ReleaseAudioVisualizer();
      return;
    }

    var context = global::Android.App.Application.Context;
    if (context.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio)
        != global::Android.Content.PM.Permission.Granted)
    {
      _ = global::ALyricEase.MainActivity.TryRequestAudioAnalysisPermission();
      return;
    }

    EnsureAudioVisualizer();
  }

  private void EnsureAudioVisualizer()
  {
    if (!_audioAnalysisRequested || _released) return;
    var context = global::Android.App.Application.Context;
    if (context.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio)
        != global::Android.Content.PM.Permission.Granted) return;

    // AudioSessionId 由音频汇点初始化时分配,起播前是未设置值;按会话号挂/重挂
    var session = _player.AudioSessionId;
    if (session <= 0) return;

    lock (_analysisLock)
    {
      if (_visualizer is not null && _visualizerSession == session) return;
      ReleaseAudioVisualizerLocked();
      try
      {
        var visualizer = new global::Android.Media.Audiofx.Visualizer(session);
        var range = global::Android.Media.Audiofx.Visualizer.GetCaptureSizeRange();
        var captureSize = range is { Length: >= 2 } ? Math.Min(1024, range[1]) : 256;
        visualizer.SetCaptureSize(captureSize);
        visualizer.SetScalingMode(global::Android.Media.Audiofx.VisualizerScalingMode.AsPlayed);
        var listener = new VisualizerListener(this);
        var captureRate = Math.Min(global::Android.Media.Audiofx.Visualizer.MaxCaptureRate, 20_000); // mHz = 20Hz
        visualizer.SetDataCaptureListener(listener, captureRate, waveform: true, fft: true);
        visualizer.SetEnabled(true);
        _visualizerListener = listener;
        _visualizer = visualizer;
        _visualizerSession = session;
        Log($"Audio visualizer enabled(session={session}, size={captureSize}, rate={captureRate}mHz)");
      }
      catch (Exception ex)
      {
        Log($"Audio visualizer unavailable: {ex.Message}");
        ReleaseAudioVisualizerLocked();
      }
    }
  }

  private void ReleaseAudioVisualizer()
  {
    lock (_analysisLock) ReleaseAudioVisualizerLocked();
    Volatile.Write(ref _waveEnergy, 0f);
    Volatile.Write(ref _bassEnergy, 0f);
    Volatile.Write(ref _audioEnergy, 0f);
  }

  private void ReleaseAudioVisualizerLocked()
  {
    if (_visualizer is { } visualizer)
    {
      try { visualizer.SetEnabled(false); } catch { }
      try { visualizer.Release(); } catch { }
      visualizer.Dispose();
      _visualizer = null;
    }
    _visualizerListener?.Dispose();
    _visualizerListener = null;
    _visualizerSession = -1;
  }

  private void OnWaveform(byte[] waveform)
  {
    if (!_audioAnalysisRequested || waveform.Length == 0) return;
    double sum = 0;
    foreach (var sample in waveform)
    {
      var centered = sample - 128.0;
      sum += centered * centered;
    }
    var rms = Math.Sqrt(sum / waveform.Length) / 128.0;
    var normalized = (float)Math.Clamp((rms - 0.01) / 0.24, 0, 1);
    Volatile.Write(ref _waveEnergy, normalized);
    PublishAudioEnergy();
  }

  private void OnFft(byte[] fft, int samplingRateMilliHz)
  {
    if (!_audioAnalysisRequested || fft.Length < 8) return;
    var sampleRate = samplingRateMilliHz / 1000.0;
    var binHz = sampleRate / fft.Length;
    var maxBin = Math.Clamp((int)Math.Ceiling(250 / binHz), 2, fft.Length / 2 - 1);
    var strongest = 0.0;
    for (var bin = 1; bin <= maxBin; bin++)
    {
      var real = unchecked((sbyte)fft[bin * 2]);
      var imaginary = unchecked((sbyte)fft[bin * 2 + 1]);
      strongest = Math.Max(strongest, Math.Sqrt(real * real + imaginary * imaginary));
    }
    Volatile.Write(ref _bassEnergy, (float)Math.Clamp(strongest / 128.0, 0, 1));
    PublishAudioEnergy();
  }

  private void PublishAudioEnergy()
  {
    var wave = Volatile.Read(ref _waveEnergy);
    var bass = Volatile.Read(ref _bassEnergy);
    Volatile.Write(ref _audioEnergy, Math.Clamp(wave * 0.72f + bass * 0.28f, 0f, 1f));
  }

  /// <summary>ExoPlayer 事件监听:错误与起播。事件在应用线程回调,经 _dispatcher 转发。
  /// 持有方字段是 strong ref,防监听器本体被 GC 后监听失效。</summary>
  private sealed class PlayerListener : Java.Lang.Object, AndroidX.Media3.Common.IPlayerListener
  {
    private readonly ExoPlayerMediaPlayer? _owner;

    public PlayerListener(ExoPlayerMediaPlayer owner) => _owner = owner;

    // JNI 反激活必需,缘由同 VisualizerListener(缺构造器会在 GC 后反激活时杀进程)。
    // 反激活路径拿不到旧 owner,回调在下方用 _owner?. 安全跳过。
    public PlayerListener(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public void OnPlaybackStateChanged(int playbackState) => _owner?.OnPlaybackStateChanged(playbackState);

    public void OnIsPlayingChanged(bool isPlaying) => _owner?.OnIsPlayingChanged(isPlaying);

    public void OnPlayerError(global::AndroidX.Media3.Common.PlaybackException? error) =>
        _owner?.OnPlayerError(error);
  }

  private sealed class VisualizerListener : Java.Lang.Object,
      global::Android.Media.Audiofx.Visualizer.IOnDataCaptureListener
  {
    private readonly ExoPlayerMediaPlayer? _owner;

    public VisualizerListener(ExoPlayerMediaPlayer owner) => _owner = owner;

    // JNI 反激活必需:GC 回收托管 peer 后,Java 侧波形回调会经 TypeManager.CreateProxy
    // 重建实例,缺这个构造器抛 MissingMethodException 直接终止进程(2026-10-26 真机日志实证)。
    // 反激活路径拿不到旧 owner,回调在下方用 _owner?. 安全跳过。
    public VisualizerListener(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public void OnWaveFormDataCapture(global::Android.Media.Audiofx.Visualizer? visualizer,
        byte[]? waveform, int samplingRate)
    {
      if (waveform is not null) _owner?.OnWaveform(waveform);
    }

    public void OnFftDataCapture(global::Android.Media.Audiofx.Visualizer? visualizer,
        byte[]? fft, int samplingRate)
    {
      if (fft is not null) _owner?.OnFft(fft, samplingRate);
    }
  }

  private void RaiseState(PlaybackState s)
  {
    if (s == _state) return;
    _state = s;
    StateChanged?.Invoke(this, EventArgs.Empty);
  }

  private static void Log(string message) => global::Android.Util.Log.Debug(Tag, message);
}
#endif
