#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using global::Android.Runtime;
using ExoPlayerType = AndroidX.Media3.ExoPlayer.IExoPlayer;
using PlayerConstants = AndroidX.Media3.Common.BasePlayer.InterfaceConsts;

namespace ALyricEase.Services.Audio;

/// <summary>Android 音频播放(Media3/ExoPlayer)。控制和事件处理均在 UI 线程;
/// 状态/时长/Seek/音频会话由 Media3 事件驱动,仅在实际播放时定时采样进度。
/// 起播网络连接失败或超时允许一次可取消的下载兜底。</summary>
public sealed class ExoPlayerMediaPlayer : IAudioPlayer
{
  private const string Tag = "ALyricEasePlayer";
  private const int PollIntervalMs = 200;
  private const string UserAgent =
      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

  // C.TIME_UNSET:ExoPlayer 时长未知的哨兵值。
  private const long TimeUnset = long.MinValue + 1;

  private readonly DispatcherService _dispatcher;
  private readonly ILogger<ExoPlayerMediaPlayer> _fileLog;
  private readonly ExoPlayerType _player;
  private readonly PlayerListener _listener;
  private readonly DispatcherTimer _progressTimer;
  private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
  private CancellationTokenSource? _downloadCancellation;

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

  // UI 线程拥有会话状态;代数号阻止迟到的事件/下载结果影响新歌。
  private int _generation;
  private bool _fallbackAttempted;
  private bool _hasStarted;
  private bool _syncPending;

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
    // DefaultHttpDataSource 仅支持网络;通用工厂按 URI 分流 file:// 缓存/本地文件,
    // HTTP(S) 仍交给上面配置了请求头与重定向的工厂。
    var dataSourceFactory = new AndroidX.Media3.DataSource.DefaultDataSource.Factory(
        global::Android.App.Application.Context, httpFactory);

    // media3 1.11:ExoPlayer 接口化后 Builder 扁平化为 ExoPlayerBuilder 类。
    _player = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(global::Android.App.Application.Context)
        .SetMediaSourceFactory(new AndroidX.Media3.ExoPlayer.Source.DefaultMediaSourceFactory(dataSourceFactory))
        .Build();
    _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
    _progressTimer.Tick += OnProgressTick;
    _listener = new PlayerListener(this);
    _player.AddListener(_listener);
  }

  // ---- 生命周期与原生事件(UI 线程) ----

  // 将同一批 native 事件合并为一次 UI 更新。保留这一层排队以避免上层自动切歌
  // 在 Media3 listener 调用栈内重入;执行时读取最新状态,不捕获易过期的 Playing/Idle。
  private void QueuePlayerUpdate()
  {
    if (_released || _syncPending) return;
    _syncPending = true;
    var generation = _generation;
    _dispatcher.Post(() =>
    {
      if (!IsCurrent(generation)) return;
      _syncPending = false;
      SynchronizePlayer();
    });
  }

  private bool IsCurrent(int generation) => !_released && generation == _generation;

  private void SynchronizePlayer()
  {
    var generation = _generation;
    var native = _player.PlaybackState;
    var isPlaying = _player.IsPlaying;
    if (isPlaying) _progressTimer.Start();
    else _progressTimer.Stop();

    if (isPlaying) EnsureAudioVisualizer();
    else ReleaseAudioVisualizer();

    // IDLE 可能来自 Stop、换源或错误恢复,由相应控制路径显式上报,不能当成播完。
    if (native == PlayerConstants.StateEnded)
    {
      if (_state != PlaybackState.Idle)
        _fileLog.LogInformation("Native playback completed. PositionMs={PositionMs} DurationMs={DurationMs} Fallback={Fallback}",
            PositionMs, DurationMs, _fallbackAttempted);
      RaiseState(PlaybackState.Idle);
    }
    else if (native == PlayerConstants.StateBuffering)
      RaiseState(_player.PlayWhenReady ? PlaybackState.Loading : PlaybackState.Paused);
    else if (native == PlayerConstants.StateReady)
      RaiseState(isPlaying ? PlaybackState.Playing : PlaybackState.Paused);

    // StateChanged / DurationChanged / PositionChanged 都可能同步触发换歌。
    if (!IsCurrent(generation) || native is not (PlayerConstants.StateBuffering or PlayerConstants.StateReady)) return;
    var duration = DurationMs;
    if (duration != _lastDurMs)
    {
      _lastDurMs = duration;
      DurationChanged?.Invoke(this, duration);
    }
    if (IsCurrent(generation)) PublishPosition();
  }

  private void OnPlayerError(global::AndroidX.Media3.Common.PlaybackException? error)
  {
    if (_released) return;
    var generation = _generation;
    var url = _currentUrl;
    var code = error?.ErrorCode;
    var message = $"播放出错({error?.Message ?? "unknown"})";
    _fileLog.LogError("ExoPlayer error. Detail={Detail} Fallback={Fallback} PositionMs={PositionMs}",
        error?.ToString(), _fallbackAttempted, PositionMs);
    _progressTimer.Stop();

    // 只对尚未起播的 HTTP(S) 连接失败/超时换网络栈重试一次。
    // 解码、鉴权、资源不存在、本地文件和播放中断流都不重新整曲下载。
    var canDownload = !_fallbackAttempted && !_hasStarted
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && code is AndroidX.Media3.Common.PlaybackException.ErrorCodeIoNetworkConnectionFailed
            or AndroidX.Media3.Common.PlaybackException.ErrorCodeIoNetworkConnectionTimeout;
    if (canDownload) _fallbackAttempted = true;
    _dispatcher.Post(() =>
    {
      if (!IsCurrent(generation)) return;
      if (!canDownload)
      {
        FailPlayback(message);
        return;
      }

      _fileLog.LogInformation("Falling back to download mode. ErrorCode={ErrorCode}", code);
      _player.Stop();
      _player.ClearMediaItems();
      RaiseState(PlaybackState.Loading);
      if (!IsCurrent(generation)) return;
      var cancellation = _downloadCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      _ = DownloadAndPlayAsync(url!, generation, cancellation);
    });
  }

  private void FailPlayback(string message)
  {
    StopInternal(releaseState: false);
    var generation = _generation;
    // 错误先于 Idle 上报,上层据此抑制“播完自动下一首”。
    ErrorOccurred?.Invoke(this, message);
    if (IsCurrent(generation)) RaiseState(PlaybackState.Idle);
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
    if (_released) return;
    StopInternal(releaseState: false);
    var generation = _generation;
    _currentUrl = url;
    RaiseState(PlaybackState.Loading);
    if (!IsCurrent(generation)) return;
    try { StartMedia(url); }
    catch (Exception ex)
    {
      _fileLog.LogWarning(ex, "Unable to set playback source");
      FailPlayback($"播放出错({ex.Message})");
    }
  }

  private void StartMedia(string source)
  {
    // 保留服务端 URL 的协议;本地路径统一转 file://,交给 DefaultDataSource 分流。
    var uri = Path.IsPathRooted(source)
        ? global::Android.Net.Uri.FromFile(new Java.IO.File(source))
        : global::Android.Net.Uri.Parse(source);
    using var builder = new AndroidX.Media3.Common.MediaItem.Builder();
    builder.SetUri(uri);
    _player.SetMediaItem(builder.Build());
    _player.Prepare();
    _player.Play();
  }

  private async Task DownloadAndPlayAsync(string url, int generation, CancellationTokenSource cancellation)
  {
    var token = cancellation.Token;
    string? target = null;
    try
    {
      var dir = global::Android.App.Application.Context.CacheDir!.AbsolutePath;
      target = Path.Combine(dir, $"playback_{Guid.NewGuid():N}.mp3");
      using var request = new HttpRequestMessage(HttpMethod.Get, url);
      request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
      request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com");
      using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                 .ConfigureAwait(false))
      {
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
      }

      await _dispatcher.InvokeAsync(() =>
      {
        if (!IsCurrent(generation)) return;
        token.ThrowIfCancellationRequested();
        _cachePath = target;
        target = null; // 文件所有权交给 StopInternal;失败/过期下载仍由 finally 清理。
        try { StartMedia(_cachePath!); }
        catch (Exception ex) { FailPlayback($"播放出错({ex.Message})"); }
      }).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
      await _dispatcher.InvokeAsync(() =>
      {
        if (!IsCurrent(generation)) return; // 切歌/停止/释放引起的取消不打扰新会话。
        _fileLog.LogWarning(ex, "Download fallback failed");
        FailPlayback(ex is OperationCanceledException ? "播放出错(下载超时)" : $"播放出错({ex.Message})");
      }).ConfigureAwait(false);
    }
    finally
    {
      if (target is not null) TryDelete(target);
      await _dispatcher.InvokeAsync(() =>
      {
        if (ReferenceEquals(_downloadCancellation, cancellation)) _downloadCancellation = null;
        cancellation.Dispose();
      }).ConfigureAwait(false);
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
    _generation++;
    _syncPending = false;
    _currentUrl = null;
    _fallbackAttempted = false;
    _hasStarted = false;
    _progressTimer.Stop();
    var cancellation = _downloadCancellation;
    _downloadCancellation = null;
    cancellation?.Cancel(); // 下载任务的 finally 负责 Dispose。
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
    StopInternal(releaseState: false);
    _released = true;
    _audioAnalysisRequested = false;
    _progressTimer.Tick -= OnProgressTick;
    try { _player.RemoveListener(_listener); } catch { }
    try { _player.Release(); } catch { }
    _listener.Dispose();
    _http.Dispose();
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

  private void OnProgressTick(object? sender, EventArgs e)
  {
    if (_released) return;
    PublishPosition();
  }

  private void PublishPosition()
  {
    var position = PositionMs;
    if (position == _lastPosMs) return;
    _lastPosMs = position;
    PositionChanged?.Invoke(this, position);
  }

  // ---- 音频输出设备(AudioManager.GetDevices + ExoPlayer.SetPreferredDevice,均需 API 23) ----

  private IReadOnlyDictionary<string, global::Android.Media.AudioDeviceInfo> _deviceCache =
      new Dictionary<string, global::Android.Media.AudioDeviceInfo>(StringComparer.Ordinal);
  private IReadOnlyList<AudioOutputDevice> _outputDevices = Array.Empty<AudioOutputDevice>();
  private global::Android.Media.AudioDeviceInfo? _outputDevice;
  private string? _outputDeviceId;

  /// <summary>项目最低支持 API 23,可直接枚举输出设备。</summary>
  public bool SupportsOutputDeviceSelection => TryGetAudioManager() is not null;

  public IReadOnlyList<AudioOutputDevice> OutputDevices => _outputDevices;

  public string? OutputDeviceId => _outputDeviceId;

  /// <summary>枚举本机输出设备(sink)。全同步本地调用,只是要拿 AudioManager 走 Context。</summary>
  public Task<IReadOnlyList<AudioOutputDevice>> RefreshOutputDevicesAsync()
  {
    var manager = TryGetAudioManager();
    if (manager is null)
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

  private static global::Android.Media.AudioManager? TryGetAudioManager()
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
    if (!_audioAnalysisRequested || _released || !_player.IsPlaying) return;
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

  /// <summary>ExoPlayer 事件监听:整批事件合并更新,起播标记用于限制下载兜底。
  /// 持有方字段是 strong ref,防监听器本体被 GC 后监听失效。</summary>
  private sealed class PlayerListener : Java.Lang.Object, AndroidX.Media3.Common.IPlayerListener
  {
    private readonly ExoPlayerMediaPlayer? _owner;

    public PlayerListener(ExoPlayerMediaPlayer owner) => _owner = owner;

    // JNI 反激活必需,缘由同 VisualizerListener(缺构造器会在 GC 后反激活时杀进程)。
    // 反激活路径拿不到旧 owner,回调在下方用 _owner?. 安全跳过。
    public PlayerListener(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public void OnEvents(AndroidX.Media3.Common.IPlayer? player, AndroidX.Media3.Common.PlayerEvents? events)
    {
      // 交叉淡化会高频修改音量;这类事件不应额外驱动进度和可视化检查。
      if (events is not null && (events.Contains(PlayerConstants.EventPlaybackStateChanged)
          || events.Contains(PlayerConstants.EventIsPlayingChanged)
          || events.Contains(PlayerConstants.EventPlayWhenReadyChanged)
          || events.Contains(PlayerConstants.EventTimelineChanged)
          || events.Contains(PlayerConstants.EventPositionDiscontinuity)
          || events.Contains(PlayerConstants.EventAudioSessionId)))
        _owner?.QueuePlayerUpdate();
    }

    public void OnIsPlayingChanged(bool isPlaying)
    {
      if (isPlaying && _owner is { } owner) owner._hasStarted = true;
    }

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
