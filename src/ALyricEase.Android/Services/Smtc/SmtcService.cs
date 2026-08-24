#if ANDROID
using System;
using System.Threading.Tasks;
using Android.Content;
using Android.Support.V4.Media.Session;
using ALyricEase.Infrastructure;
using ALyricEase.Services.Audio;

namespace ALyricEase.Services.Smtc;

/// <summary>Android 端 ISmtcService 实现:把播放器状态/曲目信息转发给前台
/// MediaPlaybackService(媒体会话 + 原生媒体横幅),并把横幅按钮事件回传给
/// PlayerViewModel。播放引擎仍是 AndroidMediaPlayer,未改动。</summary>
public sealed class SmtcService : ISmtcService, IDisposable
{
    private readonly IAudioPlayer _player;
    private readonly DispatcherService _dispatcher;

    private Context? _context;
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _coverUrl = "";
    private long _lastPosSentMs = -1;
    private int _hideGeneration;
    private bool _disposed;

    /// <summary>横幅播放/暂停按钮 → UI 线程。</summary>
    public event Action? PlayPauseRequested;

    /// <summary>横幅下一曲 → UI 线程。</summary>
    public event Action? NextRequested;

    /// <summary>横幅上一曲 → UI 线程。</summary>
    public event Action? PreviousRequested;

    /// <summary>横幅/系统进度条拖动 → 目标毫秒(UI 线程)。</summary>
    public event Action<long>? SeekRequested;

    public SmtcService(IAudioPlayer player, DispatcherService dispatcher)
    {
        _player = player;
        _dispatcher = dispatcher;
    }

    /// <summary>在 App 启动时调用(UI 线程)。</summary>
    public void Initialize(IntPtr hwnd)
    {
        if (_context is not null) return;
        _context = global::Android.App.Application.Context;

        // 单订阅者覆盖式注册:应用重建后旧实例的订阅被新实例顶替,避免按钮事件重复触发。
        MediaPlaybackService.OnPlayPauseRequested = OnServicePlayPause;
        MediaPlaybackService.OnNextRequested = OnServiceNext;
        MediaPlaybackService.OnPreviousRequested = OnServicePrevious;
        MediaPlaybackService.OnSeekRequested = OnServiceSeek;

        _player.StateChanged += OnPlayerStateChanged;
        _player.DurationChanged += OnPlayerDurationChanged;
        _player.PositionChanged += OnPlayerPositionChanged;
    }

    /// <summary>播放新歌时由 PlayerViewModel 调用(UI 线程):缓存元数据并同步给媒体服务。</summary>
    public void SetNowPlaying(string title, string artist, string album, string coverUrl)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "未知歌曲" : title;
        _artist = artist ?? "";
        _album = album ?? "";
        _coverUrl = string.IsNullOrEmpty(coverUrl) ? "" : CoverLoader.BuildSizedUrl(coverUrl, 300);

        // 服务未启动(或尚未进入前台)时只缓存:等 Loading/Playing 状态到来再随 Show 指令一并携带。
        if (_context is null || !MediaPlaybackService.IsForeground) return;
        SendIntent(MediaPlaybackService.ActionUpdateMetadata, setState: false);
    }

    private void OnPlayerStateChanged(object? sender, EventArgs e)
    {
        switch (_player.State)
        {
            case PlaybackState.Loading:
            case PlaybackState.Playing:
                StartForegroundService();
                break;
            case PlaybackState.Paused:
                SendState();
                break;
            case PlaybackState.Idle:
                // 先把横幅更新为"已停止"(播放按钮),横幅保留一段时间;
                // 自动切歌(Idle→Loading 需网络取地址,可能 >1s)会取消隐藏,避免横幅闪烁。
                SendState();
                ScheduleHide();
                break;
        }
    }

    private void OnPlayerDurationChanged(object? sender, long value) => SendState();

    private void OnPlayerPositionChanged(object? sender, long value)
    {
        // 进度事件约 200ms 一次,限频到 ~1s 再同步给媒体会话(横幅进度条无需更高频率)
        if (Math.Abs(value - _lastPosSentMs) < 1000) return;
        _lastPosSentMs = value;
        SendState();
    }

    /// <summary>开始播放/加载:启动前台服务并携带完整元数据 + 状态(幂等)。</summary>
    private void StartForegroundService()
    {
        ++_hideGeneration; // 取消挂起的延迟隐藏(自动切歌时 Idle→Loading 快速衔接)
        if (_context is null) return;
        var intent = new Intent(_context, typeof(MediaPlaybackService))
            .SetAction(MediaPlaybackService.ActionShow)
            .PutExtra(MediaPlaybackService.ExtraTitle, _title)
            .PutExtra(MediaPlaybackService.ExtraArtist, _artist)
            .PutExtra(MediaPlaybackService.ExtraAlbum, _album)
            .PutExtra(MediaPlaybackService.ExtraCoverUrl, _coverUrl)
            .PutExtra(MediaPlaybackService.ExtraState, ToSessionState(_player.State))
            .PutExtra(MediaPlaybackService.ExtraPosition, _player.PositionMs)
            .PutExtra(MediaPlaybackService.ExtraDuration, _player.DurationMs);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                _context.StartForegroundService(intent);
            else
                _context.StartService(intent);
        }
        catch (Exception ex)
        {
            Log($"start failed: {ex.Message}");
        }
    }

    private void SendState()
    {
        if (_context is null || !MediaPlaybackService.IsRunning) return;
        SendIntent(MediaPlaybackService.ActionUpdateState, setState: true);
    }

    private void SendIntent(string action, bool setState)
    {
        if (_context is null) return;
        var intent = new Intent(_context, typeof(MediaPlaybackService)).SetAction(action);
        if (setState)
        {
            intent
                .PutExtra(MediaPlaybackService.ExtraState, ToSessionState(_player.State))
                .PutExtra(MediaPlaybackService.ExtraPosition, _player.PositionMs)
                .PutExtra(MediaPlaybackService.ExtraDuration, _player.DurationMs);
        }
        try
        {
            _context.StartService(intent);
        }
        catch
        {
            // 服务已停止/进程受限,忽略(不影响播放)
        }
    }

    /// <summary>延迟隐藏:歌曲自然播完 → Idle 后若 2s 内没有新歌(自动切歌失败/队列为空),
    /// 隐藏横幅并停止前台服务;期间有新歌启动(++_hideGeneration)则取消。</summary>
    private void ScheduleHide()
    {
        if (_context is null) return;
        var gen = ++_hideGeneration;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            if (gen != _hideGeneration || _context is null) return;
            try
            {
                _context.StartService(new Intent(_context, typeof(MediaPlaybackService))
                    .SetAction(MediaPlaybackService.ActionHide));
            }
            catch
            {
                // 进程已被杀/服务已停,忽略
            }
        });
    }

    private static int ToSessionState(PlaybackState s) => s switch
    {
        PlaybackState.Playing => PlaybackStateCompat.StatePlaying,
        PlaybackState.Paused => PlaybackStateCompat.StatePaused,
        PlaybackState.Loading => PlaybackStateCompat.StateBuffering,
        _ => PlaybackStateCompat.StateStopped,
    };

    // ---- 媒体服务按钮事件 → UI 线程 ----

    private void OnServicePlayPause() => _dispatcher.Post(() => PlayPauseRequested?.Invoke());

    private void OnServiceNext() => _dispatcher.Post(() => NextRequested?.Invoke());

    private void OnServicePrevious() => _dispatcher.Post(() => PreviousRequested?.Invoke());

    private void OnServiceSeek(long pos) => _dispatcher.Post(() => SeekRequested?.Invoke(pos));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (MediaPlaybackService.OnPlayPauseRequested == OnServicePlayPause)
            MediaPlaybackService.OnPlayPauseRequested = null;
        if (MediaPlaybackService.OnNextRequested == OnServiceNext)
            MediaPlaybackService.OnNextRequested = null;
        if (MediaPlaybackService.OnPreviousRequested == OnServicePrevious)
            MediaPlaybackService.OnPreviousRequested = null;
        if (MediaPlaybackService.OnSeekRequested == OnServiceSeek)
            MediaPlaybackService.OnSeekRequested = null;

        _player.StateChanged -= OnPlayerStateChanged;
        _player.DurationChanged -= OnPlayerDurationChanged;
        _player.PositionChanged -= OnPlayerPositionChanged;
    }

    private static void Log(string message) => global::Android.Util.Log.Debug("ALyricEaseSmtc", message);
}
#endif
