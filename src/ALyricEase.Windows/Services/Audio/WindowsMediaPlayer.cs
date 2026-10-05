#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;

namespace ALyricEase.Services.Audio;

/// <summary>Windows 原生音频播放(Windows.Media.Playback.MediaPlayer),进程内单例。
/// WinRT 事件需要线程上有消息泵且 PositionChanged 只 ~3-4 次/秒,故用 System.Threading.Timer
/// 200ms 轮询 PlaybackSession(Position/NaturalDuration/PlaybackState) 作为权威进度/状态源,
/// 统一经 DispatcherService.Post 转 UI 线程(契约:事件回调已在 UI 线程)。
/// 已知限制:WinRT MediaPlayer 不支持自定义 HTTP 头(无 Referer/UA)——见计划风险章节。</summary>
public sealed class WindowsMediaPlayer : IAudioPlayer
{
  private const int PollIntervalMs = 200;

  private readonly DispatcherService _dispatcher;
  private readonly MediaPlayer _mp;
  private MediaSource? _mediaSource;
  private Timer? _timer;
  private volatile PlaybackState _state = PlaybackState.Idle;
  private bool _hadContent;
  private long _lastPosMs = -1;
  private long _lastDurMs;
  private readonly object _analysisGate = new();
  private CancellationTokenSource? _analysisCancellation;
  private Thread? _analysisThread;
  private float _audioEnergy;
  private int _analysisGeneration;
  private int _analysisRouteVersion;

  // 输出设备:只缓存 FindAllAsync 返回的 DeviceInformation 实例(见 RefreshOutputDevicesAsync 的说明)。
  private IReadOnlyDictionary<string, DeviceInformation> _deviceCache =
      new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);
  private IReadOnlyList<AudioOutputDevice> _outputDevices = Array.Empty<AudioOutputDevice>();
  private DeviceInformation? _outputDevice;
  private string? _outputDeviceId;

  public WindowsMediaPlayer(DispatcherService dispatcher)
  {
    _dispatcher = dispatcher;
    _mp = new MediaPlayer { AutoPlay = false, AudioCategory = MediaPlayerAudioCategory.Media };
    // 关掉 CommandManager 自动 SMTC 集成:SMTC 会话由 SmtcService 手动驱动。
    // 不关的话每个 MediaPlayer 实例都会抢同一个系统会话 —— 交叉淡化下新旧两实例并存,
    // 旧实例淡完 Stop 会把会话打成 Closed,第三方歌词软件(读 SMTC)以为媒体关闭直接退出;
    // 切歌瞬间旧实例还会把过期的元数据/进度推回去,与新歌互踩。
    _mp.CommandManager.IsEnabled = false;
    _mp.MediaFailed += OnMediaFailed;
    _mp.MediaEnded += (_, _) => _dispatcher.Post(() => RaiseState(PlaybackState.Idle));
  }

  public PlaybackState State => _state;

  public long PositionMs
  {
    get
    {
      try
      {
        return (long)_mp.PlaybackSession.Position.TotalMilliseconds;
      }
      catch
      {
        return 0;
      } // 未就绪
    }
    set
    {
      try
      {
        _mp.PlaybackSession.Position = TimeSpan.FromMilliseconds(Math.Max(0, value));
      }
      catch
      {
        /* 未就绪时忽略 */
      }
    }
  }

  public long DurationMs
  {
    get
    {
      try
      {
        var d = _mp.PlaybackSession.NaturalDuration;
        return d > TimeSpan.Zero ? (long)d.TotalMilliseconds : 0; // 0 = 未知
      }
      catch
      {
        return 0;
      }
    }
  }

  public int Volume
  {
    get => (int)Math.Round(_mp.Volume * 100.0);
    set => _mp.Volume = Math.Clamp(value, 0, 100) / 100.0; // MediaPlayer.Volume 是 0..1 的 double
  }

  // ---- 音频输出设备 ----

  public bool SupportsOutputDeviceSelection => true;

  public IReadOnlyList<AudioOutputDevice> OutputDevices => _outputDevices;

  public string? OutputDeviceId => _outputDeviceId;

  /// <summary>枚举系统输出设备(DeviceClass.AudioRender)。两个坑:
  /// ① <c>DeviceInformation.FindAllAsync</c> 是 WinRT 异步操作:在 STA(UI)线程上同步等它,
  ///    完成回调没消息泵就永远不回来(死锁)⇒ 整段枚举丢到线程池(MTA)上等,回调用线程再落字段;
  /// ② <c>MediaPlayer.AudioDevice</c> **只认 FindAllAsync 返回的实例**,用
  ///    <c>DeviceInformation.CreateFromIdAsync</c> 自己造的对象喂进去不生效 ⇒ 必须缓存实例备切换。</summary>
  public async Task<IReadOnlyList<AudioOutputDevice>> RefreshOutputDevicesAsync()
  {
    try
    {
      var (devices, cache) = await Task.Run(async () =>
      {
        string? defaultId = null;
        try
        {
          // 默认设备 Id 是 {0.0.0.00000000}.{guid} 形式,DeviceInformation.Id 是
          // \\?\SWD#MMDEVAPI#{...}#{...} 的复合串 ⇒ 只能做包含匹配。
          defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        }
        catch
        {
          // 拿不到只是少了"系统默认"标注,枚举照常
        }

        var infos = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
        var list = new List<AudioOutputDevice>(infos.Count);
        var map = new Dictionary<string, DeviceInformation>(infos.Count, StringComparer.Ordinal);
        foreach (var info in infos)
        {
          map[info.Id] = info;
          var isDefault = !string.IsNullOrEmpty(defaultId)
                          && info.Id.Contains(defaultId, StringComparison.OrdinalIgnoreCase);
          list.Add(new AudioOutputDevice(info.Id, info.Name, isDefault));
        }

        return (Devices: (IReadOnlyList<AudioOutputDevice>)list,
                Cache: (IReadOnlyDictionary<string, DeviceInformation>)map);
      }).ConfigureAwait(true);

      _deviceCache = cache;
      _outputDevices = devices;
      return _outputDevices;
    }
    catch (Exception)
    {
      // 枚举失败(无音频子系统/服务被禁)按"没有设备"处理,不抛给 UI
      _deviceCache = new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);
      _outputDevices = Array.Empty<AudioOutputDevice>();
      return _outputDevices;
    }
  }

  public bool TrySetOutputDevice(string? deviceId)
  {
    try
    {
      if (string.IsNullOrEmpty(deviceId))
      {
        // null = 回到系统默认设备
        _mp.AudioDevice = null;
        _outputDevice = null;
        _outputDeviceId = null;
        Interlocked.Increment(ref _analysisRouteVersion);
        return true;
      }

      if (!_deviceCache.TryGetValue(deviceId, out var info)) return false; // 设备已拔出/未枚举到
      _mp.AudioDevice = info;
      _outputDevice = info;
      _outputDeviceId = deviceId;
      Interlocked.Increment(ref _analysisRouteVersion);
      return true;
    }
    catch (Exception)
    {
      return false; // 设备被占用/后端拒绝:保持原设备
    }
  }

  /// <summary>换 Source 前把已选设备再贴一次 —— MediaPlayer 换源后有可能回到默认设备。</summary>
  private void ReapplyOutputDevice()
  {
    if (_outputDevice is null) return;
    try
    {
      _mp.AudioDevice = _outputDevice;
    }
    catch
    {
      // 设备中途被拔:退回默认设备播放,好过整首播不出来
      _outputDevice = null;
      _outputDeviceId = null;
      Interlocked.Increment(ref _analysisRouteVersion);
    }
  }

  public event EventHandler? StateChanged;

  public event EventHandler<long>? PositionChanged;

  public event EventHandler<long>? DurationChanged;

  public event EventHandler<string>? ErrorOccurred;

  public float AudioEnergy => Volatile.Read(ref _audioEnergy);

  public float BassEnergy => 0f; // WASAPI 会话峰值没有频谱；Android 后端会提供低频能量。

  public void SetAudioAnalysisEnabled(bool enabled)
  {
    Thread? stoppingThread = null;
    lock (_analysisGate)
    {
      if (enabled)
      {
        if (_analysisCancellation is not null) return;
        var cancellation = _analysisCancellation = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _analysisGeneration);
        var thread = _analysisThread = new Thread(() => AnalyzeAudioLoop(cancellation, generation))
        {
          IsBackground = true,
          Name = "ALyricEase.AudioEnergy.Windows",
        };
        thread.Start();
      }
      else
      {
        Interlocked.Increment(ref _analysisGeneration);
        _analysisCancellation?.Cancel();
        _analysisCancellation = null;
        stoppingThread = _analysisThread;
        _analysisThread = null;
        Volatile.Write(ref _audioEnergy, 0f);
      }
    }

    // 关闭播放器前等采样线程归还会话 COM 引用，避免它与 MediaPlayer.Dispose 并发。
    // 正常最多等待一个 50ms 采样周期；超时则不阻塞 UI。
    if (stoppingThread is not null && stoppingThread != Thread.CurrentThread)
        stoppingThread.Join(250);
  }

  public void PlayUrl(string url)
  {
    Stop();
    Interlocked.Increment(ref _analysisRouteVersion);
    try
    {
      _mediaSource?.Dispose();
      var sourceUri = Path.IsPathRooted(url)
          ? new Uri(Path.GetFullPath(url))
          : new Uri(url);
      _mediaSource = MediaSource.CreateFromUri(sourceUri);
      _mp.Source = _mediaSource;
      ReapplyOutputDevice();
      _hadContent = true;
      _lastPosMs = -1;
      _lastDurMs = 0;
      StartPolling();
      _mp.Play();
    }
    catch (Exception)
    {
      _dispatcher.Post(() => ErrorOccurred?.Invoke(this, "播放出错"));
    }
  }

  public void Pause()
  {
    try
    {
      _mp.Pause();
    }
    catch
    {
      /* 忽略 */
    }
  }

  public void Resume()
  {
    try
    {
      _mp.Play();
    }
    catch
    {
      /* 忽略 */
    }
  }

  /// <summary>Stop:MediaPlayer 无 Stop() → Pause + 清源 + 复位,再上报 Idle(契约)。</summary>
  public void Stop()
  {
    try
    {
      _mp.Pause();
    }
    catch
    {
    }

    try
    {
      _mp.Source = null;
    }
    catch
    {
    }

    _hadContent = false;
    StopPolling();
    _lastPosMs = -1;
    RaiseState(PlaybackState.Idle);
  }

  public void Dispose()
  {
    SetAudioAnalysisEnabled(false);
    _mp.MediaFailed -= OnMediaFailed;
    StopPolling();
    try
    {
      _mp.Pause();
    }
    catch
    {
    }

    try
    {
      _mp.Source = null;
    }
    catch
    {
    }

    _mediaSource?.Dispose();
    _mp.Dispose(); // = Close()
  }

  // ---- 轮询:线程池 Timer,不依赖消息泵;结果统一经 DispatcherService.Post 上 UI 线程 ----

  private void StartPolling() => _timer ??= new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);

  private void StopPolling()
  {
    _timer?.Dispose();
    _timer = null;
  }

  private void Poll()
  {
    try
    {
      var session = _mp.PlaybackSession;
      var pos = (long)session.Position.TotalMilliseconds;
      var dur = ReadDuration(session);
      var ms = session.PlaybackState;

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

      // 状态机映射(轮询为主,不依赖 PlaybackStateChanged 事件)
      var next = ms switch
      {
        MediaPlaybackState.Playing => PlaybackState.Playing,
        MediaPlaybackState.Paused => PlaybackState.Paused,
        MediaPlaybackState.None => _state, // 是否转 Idle 由下面的结束/停止分支决定
        _ => _state, // Opening/Buffering:保持当前
      };
      if (ms == MediaPlaybackState.None && _state == PlaybackState.Playing && _hadContent)
        next = PlaybackState.Idle; // 播到末尾(MediaEnded 事件的轮询等价物)

      if (next != _state)
        _dispatcher.Post(() => RaiseState(next));
    }
    catch
    {
      // 未就绪/竞态读取异常忽略
    }
  }

  private void AnalyzeAudioLoop(CancellationTokenSource cancellation, int generation)
  {
    var token = cancellation.Token;
    var initialized = WindowsAudioSessionMeter.InitializeApartment();
    WindowsAudioSessionMeter? meter = null;
    var observedRouteVersion = -1;
    var silentSamples = 0;
    try
    {
      while (!token.IsCancellationRequested)
      {
        var playing = _state == PlaybackState.Playing;
        var routeVersion = Volatile.Read(ref _analysisRouteVersion);
        if (playing && (meter is null || routeVersion != observedRouteVersion || silentSamples >= 40))
        {
          meter?.Dispose();
          meter = WindowsAudioSessionMeter.TryCreate(_outputDeviceId, Environment.ProcessId);
          observedRouteVersion = routeVersion;
          silentSamples = 0;
        }

        var energy = 0f;
        if (playing && meter?.TryGetPeak(out var peak) == true)
        {
          // 平方根把低电平动态展开；0.6 振幅探针约映射到 0.76，保留强弱段差异。
          energy = Math.Clamp((MathF.Sqrt(Math.Clamp(peak, 0f, 1f)) - 0.08f) / 0.92f, 0f, 1f);
          silentSamples = peak <= 0.0005f ? silentSamples + 1 : 0;
        }
        else if (playing)
        {
          silentSamples++;
        }
        else
        {
          // 暂停/空闲时不反复枚举会话；恢复播放仍复用原 meter，切歌则由 routeVersion 强制刷新。
          silentSamples = 0;
        }

        if (generation == Volatile.Read(ref _analysisGeneration))
          Volatile.Write(ref _audioEnergy, energy);
        if (token.WaitHandle.WaitOne(50)) break; // 20Hz 分析；渲染端按帧插值
      }
    }
    catch
    {
      // 无音频设备/会话切换竞态时静默退化为固定慢速背景。
    }
    finally
    {
      meter?.Dispose();
      if (initialized) WindowsAudioSessionMeter.UninitializeApartment();
      cancellation.Dispose();
      if (generation == Volatile.Read(ref _analysisGeneration))
        Volatile.Write(ref _audioEnergy, 0f);
    }
  }

  private static long ReadDuration(MediaPlaybackSession session)
  {
    try
    {
      var d = session.NaturalDuration;
      return d > TimeSpan.Zero ? (long)d.TotalMilliseconds : 0;
    }
    catch
    {
      return 0;
    }
  }

  private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
  {
    _dispatcher.Post(() =>
    {
      ErrorOccurred?.Invoke(this, "播放出错"); // 契约固定文案
      RaiseState(PlaybackState.Idle);
    });
  }

  private void RaiseState(PlaybackState state)
  {
    if (state == _state) return; // 幂等:仅变化时上报
    _state = state;
    if (state == PlaybackState.Idle) StopPolling();
    StateChanged?.Invoke(this, EventArgs.Empty);
  }
}
#endif
