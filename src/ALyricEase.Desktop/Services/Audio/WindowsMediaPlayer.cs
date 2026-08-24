#if WINDOWS
using System;
using System.Threading;
using ALyricEase.Infrastructure;
using Windows.Media.Core;
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
  private PlaybackState _state = PlaybackState.Idle;
  private bool _hadContent;
  private long _lastPosMs = -1;
  private long _lastDurMs;

  public WindowsMediaPlayer(DispatcherService dispatcher)
  {
    _dispatcher = dispatcher;
    _mp = new MediaPlayer { AutoPlay = false, AudioCategory = MediaPlayerAudioCategory.Media };
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

  public event EventHandler? StateChanged;

  public event EventHandler<long>? PositionChanged;

  public event EventHandler<long>? DurationChanged;

  public event EventHandler<string>? ErrorOccurred;

  public void PlayUrl(string url)
  {
    Stop();
    try
    {
      _mediaSource?.Dispose();
      _mediaSource = MediaSource.CreateFromUri(new Uri(url));
      _mp.Source = _mediaSource;
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