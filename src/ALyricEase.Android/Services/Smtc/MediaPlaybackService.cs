#if ANDROID
// AndroidX Media 1.7+ 把 MediaSessionCompat / PlaybackStateCompat / MediaMetadataCompat /
// MediaButtonReceiver / NotificationCompat.MediaStyle 全部标了 @Deprecated(上游指向
// androidx.media3),绑定生成器据此打上 [Obsolete],于是本文件满屏 CS0618。
//
// 这些 API 在 Android 上依旧完整可用,且是不引入 Media3 + ExoPlayer 的前提下唯一能拿到
// 系统媒体横幅 / 锁屏控制 / 蓝牙媒体键的路径。迁到 Media3 需要为这里的自定义播放器
// (实际由 AndroidMediaPlayer 出声)实现 SimpleBasePlayer,是一次独立改造,不混在本次做。
// 因此整文件关闭 CS0618 —— 若哪天真的迁到 Media3,请连同本 pragma 一起删掉。
#pragma warning disable CS0618 // 类型或成员已过时

using System;
using System.Net.Http;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Support.V4.Media;
using Android.Support.V4.Media.Session;
using AndroidX.Media.Session;
using CoreNotificationCompat = AndroidX.Core.App.NotificationCompat;
using MediaNotificationCompat = AndroidX.Media.App.NotificationCompat;

// AndroidX 绑定对流畅 API 的可空标注很吵(Builder 每个方法都返回 Builder?),
// 本文件是纯 Android 互操作层:保留 ? 标注语法,但关闭可空性警告。
#nullable disable warnings

namespace ALyricEase.Services.Smtc;

/// <summary>Android 前台媒体服务:持有 MediaSessionCompat 并发布 MediaStyle 原生媒体通知,
/// 让状态栏下拉/锁屏出现系统媒体横幅(播放/暂停、上一曲/下一曲 + 进度条,可拖动 Seek)。
///
/// 实际音频仍在应用进程内由 AndroidMediaPlayer 播放,本服务只负责"媒体会话 + 横幅";
/// 横幅/系统媒体键事件经静态回调字段回传给最新的 SmtcService(Android) 实例,
/// 由它转发给 PlayerViewModel(避免应用重建后事件重复订阅)。</summary>
[Service(Name = "com.aika1024.alyricease.MediaPlaybackService",
         Exported = false,
         ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
[IntentFilter(new[] { "android.intent.action.MEDIA_BUTTON" })]
public sealed class MediaPlaybackService : Service
{
    // ---- 命令 Action(SmtcService 通过显式 Intent 驱动本服务) ----
    public const string ActionShow = "com.aika1024.alyricease.action.SHOW";
    public const string ActionUpdateMetadata = "com.aika1024.alyricease.action.UPDATE_METADATA";
    public const string ActionUpdateState = "com.aika1024.alyricease.action.UPDATE_STATE";
    public const string ActionPlayPause = "com.aika1024.alyricease.action.PLAY_PAUSE";
    public const string ActionNext = "com.aika1024.alyricease.action.NEXT";
    public const string ActionPrev = "com.aika1024.alyricease.action.PREV";
    public const string ActionHide = "com.aika1024.alyricease.action.HIDE";

    // ---- Intent 附加字段 ----
    public const string ExtraTitle = "title";
    public const string ExtraArtist = "artist";
    public const string ExtraAlbum = "album";
    public const string ExtraCoverUrl = "cover_url";
    public const string ExtraState = "state";
    public const string ExtraPosition = "position";
    public const string ExtraDuration = "duration";

    public const string ChannelId = "alyricease_media_playback";
    public const int NotificationId = 1001;

    private const string Tag = "ALyricEaseMediaSvc";

    /// <summary>服务是否已创建(供 SmtcService 判断能否直接发送更新指令)。</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>是否已进入前台(横幅可见)。只有前台时才值得即时推送元数据更新。</summary>
    public static bool IsForeground { get; private set; }

    // ---- 媒体按钮回调(单订阅者:最新的 SmtcService 实例覆盖旧的,防重复) ----
    public static Action? OnPlayPauseRequested;
    public static Action? OnNextRequested;
    public static Action? OnPreviousRequested;
    public static Action<long>? OnSeekRequested;

    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Handler _mainHandler = new(Looper.MainLooper!);

    private MediaSessionCompat? _session;
    private NotificationManager? _notificationManager;
    private bool _foreground;

    // 当前曲目/播放状态(由命令 intent 在主线程更新)
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private string _coverUrl = "";
    private Bitmap? _cover;
    private int _state = PlaybackStateCompat.StateStopped;
    private long _position;
    private long _duration;

    public override void OnCreate()
    {
        base.OnCreate();
        IsRunning = true;
        CreateNotificationChannel();
        _notificationManager = (NotificationManager?)GetSystemService(NotificationService);

        _session = new MediaSessionCompat(this, "ALyricEase");
        _session.SetCallback(new SessionCallback());
        _session.Active = true;
        Log("created");
    }

    /// <summary>纯启动型服务(不绑定),返回 null 即可。</summary>
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // 蓝牙/有线耳机媒体键:清单里的 MediaButtonReceiver 收到 MEDIA_BUTTON 广播后
        // 显式启动本服务,这里把事件解析为媒体会话回调(OnPlay/OnPause/OnSkipToNext...)。
        // HandleIntent 内部已把 KeyEvent 分发到会话,返回非 null 表示确实是媒体键事件。
        if (intent is not null && _session is not null
            && MediaButtonReceiver.HandleIntent(_session, intent) is not null)
        {
            // 媒体键可能在没有播放上下文时经 startForegroundService 冷启动本服务:
            // 必须先进入前台,否则 API 26+ 5 秒内未 StartForeground 会抛异常。
            // 先用当前内容占位;若媒体键触发播放,后续 SHOW 指令会刷新为完整通知。
            if (!_foreground)
            {
                ShowForegroundNotification();
                // 冷启动(应用未初始化,无订阅者):媒体键本身无法生效,
                // 立即收掉前台通知,避免留下"未知歌曲"僵尸横幅。
                if (OnPlayPauseRequested is null && OnNextRequested is null && OnPreviousRequested is null)
                    HideForegroundAndStop();
            }
            return StartCommandResult.NotSticky;
        }

        switch (intent?.Action)
        {
            case ActionShow:
                ApplyMetadata(intent);
                ApplyState(intent);
                ShowForegroundNotification();
                break;
            case ActionUpdateMetadata:
                ApplyMetadata(intent);
                RefreshNotification();
                break;
            case ActionUpdateState:
                ApplyState(intent);
                RefreshNotification();
                break;
            case ActionPlayPause:
                OnPlayPauseRequested?.Invoke();
                break;
            case ActionNext:
                OnNextRequested?.Invoke();
                break;
            case ActionPrev:
                OnPreviousRequested?.Invoke();
                break;
            case ActionHide:
                HideForegroundAndStop();
                return StartCommandResult.NotSticky;
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        IsForeground = false;
        try
        {
            if (_session is not null)
            {
                _session.Active = false;
                _session.Release();
                _session = null;
            }
        }
        catch
        {
        }
        try
        {
            _notificationManager?.Cancel(NotificationId);
        }
        catch
        {
        }
        _cover?.Recycle();
        _cover = null;
        Log("destroyed");
        base.OnDestroy();
    }

    // ---- 前台通知 ----

    /// <summary>进入前台并展示媒体通知(必须在服务启动后尽快调用,否则 API 26+ 会抛异常)。</summary>
    private void ShowForegroundNotification()
    {
        if (_session is null) return;
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(NotificationId, notification);
        _foreground = true;
        IsForeground = true;
        Log("foreground started");
    }

    /// <summary>更新已展示的前台通知(播放/暂停切换、切歌、封面加载完成等)。</summary>
    private void RefreshNotification()
    {
        if (!_foreground || _notificationManager is null) return;
        try
        {
            _notificationManager.Notify(NotificationId, BuildNotification());
        }
        catch (Exception ex)
        {
            Log($"notify failed: {ex.Message}");
        }
    }

    private Notification BuildNotification()
    {
        var isPlaying = _state == PlaybackStateCompat.StatePlaying;
        var playPauseIcon = isPlaying
            ? global::Android.Resource.Drawable.IcMediaPause
            : global::Android.Resource.Drawable.IcMediaPlay;

        var style = new MediaNotificationCompat.MediaStyle()
            .SetMediaSession(_session!.SessionToken)
            .SetShowActionsInCompactView(0, 1, 2);

        var builder = new CoreNotificationCompat.Builder(this, ChannelId)
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
            .SetVisibility(CoreNotificationCompat.VisibilityPublic)
            .SetOngoing(isPlaying)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(ContentIntent())
            .SetStyle(style)
            .SetContentTitle(string.IsNullOrEmpty(_title) ? "未知歌曲" : _title)
            .SetContentText(_artist)
            .SetSubText(_album)
            .AddAction(global::Android.Resource.Drawable.IcMediaPrevious, "上一曲",
                ActionPendingIntent(ActionPrev))
            .AddAction(playPauseIcon, isPlaying ? "暂停" : "播放",
                ActionPendingIntent(ActionPlayPause))
            .AddAction(global::Android.Resource.Drawable.IcMediaNext, "下一曲",
                ActionPendingIntent(ActionNext));

        if (_cover is not null) builder.SetLargeIcon(_cover);
        return builder.Build();
    }

    private PendingIntent ActionPendingIntent(string action)
    {
        var intent = new Intent(this, typeof(MediaPlaybackService)).SetAction(action);
        return PendingIntent.GetService(this, action.GetHashCode(), intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private PendingIntent ContentIntent()
    {
        var intent = new Intent(this, typeof(global::ALyricEase.MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(this, 0, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private void HideForegroundAndStop()
    {
        if (_foreground)
        {
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    StopForeground(StopForegroundFlags.Remove);
                else
                    StopForeground(true);
            }
            catch
            {
            }
            _foreground = false;
            IsForeground = false;
        }
        StopSelf();
    }

    // ---- 曲目信息 / 播放状态 ----

    private void ApplyMetadata(Intent? intent)
    {
        if (intent is null) return;
        var title = intent.GetStringExtra(ExtraTitle);
        if (!string.IsNullOrEmpty(title)) _title = title;
        _artist = intent.GetStringExtra(ExtraArtist) ?? "";
        _album = intent.GetStringExtra(ExtraAlbum) ?? "";

        var url = intent.GetStringExtra(ExtraCoverUrl) ?? "";
        if (url != _coverUrl)
        {
            _coverUrl = url;
            _cover?.Recycle();
            _cover = null;
            if (!string.IsNullOrEmpty(url)) _ = LoadCoverAsync(url);
        }
        PushSessionMetadata();
    }

    private void ApplyState(Intent? intent)
    {
        if (intent is null) return;
        _state = intent.GetIntExtra(ExtraState, PlaybackStateCompat.StateStopped);
        var pos = intent.GetLongExtra(ExtraPosition, -1L);
        if (pos >= 0) _position = pos;
        var dur = intent.GetLongExtra(ExtraDuration, -1L);
        var durationChanged = dur >= 0 && dur != _duration;
        if (durationChanged) _duration = dur;
        PushSessionState();
        if (durationChanged) PushSessionMetadata();
    }

    private void PushSessionMetadata()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var b = new MediaMetadataCompat.Builder()
                .PutString(MediaMetadataCompat.MetadataKeyTitle, string.IsNullOrEmpty(_title) ? "未知歌曲" : _title)
                .PutString(MediaMetadataCompat.MetadataKeyArtist, _artist)
                .PutString(MediaMetadataCompat.MetadataKeyAlbum, _album);
            if (_duration > 0) b.PutLong(MediaMetadataCompat.MetadataKeyDuration, _duration);
            if (_cover is not null) b.PutBitmap(MediaMetadataCompat.MetadataKeyAlbumArt, _cover);
            session.SetMetadata(b.Build());
        }
        catch (Exception ex)
        {
            Log($"metadata failed: {ex.Message}");
        }
    }

    private void PushSessionState()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var actions = PlaybackStateCompat.ActionPlay | PlaybackStateCompat.ActionPause |
                          PlaybackStateCompat.ActionPlayPause | PlaybackStateCompat.ActionSkipToNext |
                          PlaybackStateCompat.ActionSkipToPrevious | PlaybackStateCompat.ActionSeekTo;
            var b = new PlaybackStateCompat.Builder()
                .SetActions(actions)
                .SetState(_state, Math.Max(0, _position), _state == PlaybackStateCompat.StatePlaying ? 1.0f : 0.0f);
            session.SetPlaybackState(b.Build());
        }
        catch (Exception ex)
        {
            Log($"state failed: {ex.Message}");
        }
    }

    private async Task LoadCoverAsync(string url)
    {
        try
        {
            var bytes = await CoverHttp.GetByteArrayAsync(url).ConfigureAwait(false);
            var bitmap = await Task.Run(() => BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length))
                .ConfigureAwait(false);
            if (bitmap is null) return;
            _mainHandler.Post(() =>
            {
                if (_coverUrl != url)
                {
                    bitmap.Recycle(); // 下载期间已切歌,丢弃
                    return;
                }
                var old = _cover;
                _cover = bitmap;
                old?.Recycle();
                PushSessionMetadata();
                RefreshNotification();
            });
        }
        catch (Exception ex)
        {
            Log($"cover failed: {ex.Message}");
        }
    }

    private void CreateNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var channel = new NotificationChannel(ChannelId, "媒体播放", NotificationImportance.Low)
        {
            Description = "显示当前播放的音乐与控制按钮",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        var nm = (NotificationManager?)GetSystemService(NotificationService);
        nm?.CreateNotificationChannel(channel);
    }

    private static void Log(string message) => global::Android.Util.Log.Debug(Tag, message);

    /// <summary>媒体会话回调:系统媒体键、锁屏控制、横幅进度条拖动都会走到这里。</summary>
    private sealed class SessionCallback : MediaSessionCompat.Callback
    {
        public override void OnPlay() => OnPlayPauseRequested?.Invoke();

        public override void OnPause() => OnPlayPauseRequested?.Invoke();

        public override void OnSkipToNext() => OnNextRequested?.Invoke();

        public override void OnSkipToPrevious() => OnPreviousRequested?.Invoke();

        public override void OnSeekTo(long pos) => OnSeekRequested?.Invoke(pos);
    }
}
#endif
