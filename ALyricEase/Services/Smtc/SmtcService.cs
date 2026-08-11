#if WINDOWS
using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Services.Audio;
using Windows.Media;
using Windows.Storage.Streams;

namespace ALyricEase.Services.Smtc;

/// <summary>Windows 系统媒体传输控制(SMTC)集成:音量飞沫/媒体键显示当前曲目、
/// 封面、播放状态,支持播放/暂停与系统进度条拖动。
/// 桌面(Win32)应用没有 GetForCurrentSession,须经 COM 互操作
/// ISystemMediaTransportControlsInterop::GetForWindow 取句柄;SMTC 无歌词 API,
/// 歌词仍由应用内滚动视图展示。任何失败静默降级(不阻塞播放)。</summary>
public sealed class SmtcService : ISmtcService, IDisposable
{
    // ISystemMediaTransportControlsInterop — systemmediatransportcontrolsinterop.h
    private const string SmtcRuntimeClass = "Windows.Media.SystemMediaTransportControls";
    private static readonly Guid SmtcInteropIid = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");

    // ISystemMediaTransportControls — Windows SDK IDL 稳定契约(投影 ABI 接口 GuidAttribute 一致)
    private static readonly Guid SmtcInterfaceIid = new("99FA3FF4-1742-42A6-902E-087D41F965EC");

    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly IAudioPlayer _player;
    private readonly DispatcherService _dispatcher;
    private SystemMediaTransportControls? _controls;
    private bool _enabled;
    private DateTime _lastTimelineUpdate;

    /// <summary>SMTC 按钮(播放/暂停)按下 → UI 线程。</summary>
    public event Action? PlayPauseRequested;

    /// <summary>SMTC 下一曲 → UI 线程。</summary>
    public event Action? NextRequested;

    /// <summary>SMTC 上一曲 → UI 线程。</summary>
    public event Action? PreviousRequested;

    /// <summary>系统进度条拖动 → 目标毫秒(UI 线程)。</summary>
    public event Action<long>? SeekRequested;

    public SmtcService(IAudioPlayer player, DispatcherService dispatcher)
    {
        _player = player;
        _dispatcher = dispatcher;
    }

    /// <summary>必须在窗口创建后于 UI 线程调用。hwnd 取自主窗口平台句柄。</summary>
    public void Initialize(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            _controls = CreateControlsForWindow(hwnd);
            if (_controls is null) return;

            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsNextEnabled = true;
            _controls.IsPreviousEnabled = true;
            _controls.ButtonPressed += OnButtonPressed;
            _controls.PlaybackPositionChangeRequested += OnPositionChangeRequested;

            _player.StateChanged += OnStateChanged;
            _player.DurationChanged += OnDurationChanged;
            _player.PositionChanged += OnPositionChanged;

            SyncPlaybackStatus(_player.State);
            _enabled = true;
        }
        catch (Exception)
        {
            _controls = null; // 非桌面上下文/无窗口 → 不使用 SMTC
        }
    }

    /// <summary>播放新歌时由 PlayerViewModel 调用(UI 线程):更新曲目信息 + 封面。</summary>
    public void SetNowPlaying(string title, string artist, string album, string coverUrl)
    {
        if (!_enabled || _controls is null) return;
        try
        {
            var updater = _controls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.IsNullOrWhiteSpace(title) ? "未知歌曲" : title;
            updater.MusicProperties.Artist = artist;
            updater.MusicProperties.AlbumTitle = album;
            updater.Update();

            if (!string.IsNullOrEmpty(coverUrl))
                _ = SetCoverAsync(coverUrl); // 后台拉取,失败不影响
        }
        catch (Exception)
        {
            // 曲目信息更新失败 → 静默
        }
    }

    public void Dispose()
    {
        if (_controls is not null)
        {
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
        }
        _player.StateChanged -= OnStateChanged;
        _player.DurationChanged -= OnDurationChanged;
        _player.PositionChanged -= OnPositionChanged;
        try { if (_controls is not null) _controls.PlaybackStatus = MediaPlaybackStatus.Stopped; } catch { }
        _controls = null;
        _enabled = false;
    }

    /// <summary>经 ISystemMediaTransportControlsInterop::GetForWindow 获取桌面窗口关联的 SMTC。
    /// 现代 .NET 不再支持 InterfaceIsIInspectable 封送 → 直接走原始 vtable 槽位调用。
    /// 槽位 6 = IUnknown×3 + IInspectable×3(GetIids/GetRuntimeClassName/GetTrustLevel)+ GetForWindow。</summary>
    private static unsafe SystemMediaTransportControls? CreateControlsForWindow(IntPtr hwnd)
    {
        try
        {
            var factory = GetInterop();
            if (factory == IntPtr.Zero) return null;

            var vftbl = *(IntPtr**)factory;
            var getForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vftbl[6];

            var riid = SmtcInterfaceIid;
            IntPtr native;
            var hr = getForWindow(factory, hwnd, &riid, &native);
            if (hr != 0 || native == IntPtr.Zero) return null;

            return SystemMediaTransportControls.FromAbi(native);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static unsafe IntPtr GetInterop()
    {
        WindowsCreateString(SmtcRuntimeClass, SmtcRuntimeClass.Length, out var hstring);
        try
        {
            var hr = RoGetActivationFactory(hstring, SmtcInteropIid, out var factoryPtr);
            return hr != 0 ? IntPtr.Zero : factoryPtr;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
            case SystemMediaTransportControlsButton.Pause:
                _dispatcher.Post(() => PlayPauseRequested?.Invoke());
                break;
            case SystemMediaTransportControlsButton.Next:
                _dispatcher.Post(() => NextRequested?.Invoke());
                break;
            case SystemMediaTransportControlsButton.Previous:
                _dispatcher.Post(() => PreviousRequested?.Invoke());
                break;
        }
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        var ms = (long)args.RequestedPlaybackPosition.TotalMilliseconds;
        _dispatcher.Post(() => SeekRequested?.Invoke(ms));
    }

    private void OnStateChanged(object? sender, EventArgs e) => SyncPlaybackStatus(_player.State);

    private void SyncPlaybackStatus(PlaybackState state)
    {
        if (!_enabled || _controls is null) return;
        try
        {
            var (status, playEnabled, pauseEnabled) = state switch
            {
                PlaybackState.Playing => (MediaPlaybackStatus.Playing, false, true),
                PlaybackState.Paused => (MediaPlaybackStatus.Paused, true, false),
                _ => (MediaPlaybackStatus.Stopped, true, false),
            };
            _controls.PlaybackStatus = status;
            _controls.IsPlayEnabled = playEnabled;
            _controls.IsPauseEnabled = pauseEnabled;
        }
        catch (Exception)
        {
            // 状态同步失败 → 静默
        }
    }

    private void OnDurationChanged(object? sender, long value) => UpdateTimeline();

    private void OnPositionChanged(object? sender, long value)
    {
        // 进度事件约 100ms 一次,系统进度条不需要那么频繁 → 限频 ~1s
        if (DateTime.UtcNow - _lastTimelineUpdate < TimeSpan.FromSeconds(1)) return;
        UpdateTimeline();
    }

    private void UpdateTimeline()
    {
        if (!_enabled || _controls is null) return;
        try
        {
            _lastTimelineUpdate = DateTime.UtcNow;
            var end = TimeSpan.FromMilliseconds(_player.DurationMs);
            var pos = TimeSpan.FromMilliseconds(_player.PositionMs);
            var props = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = end,
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = end,
                Position = pos > end ? end : pos,
            };
            _controls.UpdateTimelineProperties(props);
        }
        catch (Exception)
        {
            // 时间线更新失败 → 静默
        }
    }

    private async Task SetCoverAsync(string url)
    {
        try
        {
            // SMTC 缩略图用 300px 小图,避免系统端保留大图
            var bytes = await CoverHttp.GetByteArrayAsync(CoverLoader.BuildSizedUrl(url, 300)).ConfigureAwait(false);
            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer()).AsTask().ConfigureAwait(false);
            stream.Seek(0);

            _dispatcher.Post(() =>
            {
                try
                {
                    if (_controls is null) return;
                    _controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                    _controls.DisplayUpdater.Update();
                }
                catch (Exception)
                {
                    // 封面设置失败 → 静默
                }
            });
        }
        catch (Exception)
        {
            // 封面下载失败 → 静默
        }
    }

    // ---- COM 互操作(combase.dll) ----

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern void WindowsDeleteString(IntPtr hstring);
}
#endif
