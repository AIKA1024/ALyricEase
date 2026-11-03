using System.ComponentModel;
using System.Runtime.InteropServices;
using ALyricEase.Infrastructure;
using ALyricEase.Infrastructure.Logging;
using ALyricEase.Services.Hotkeys;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;

namespace ALyricEase.Services;

/// <summary>独立置顶窗口，不归主窗口所有，因此最小化到托盘后仍显示。</summary>
public sealed class DesktopLyricController : IDisposable
{
    private readonly DesktopLyricViewModel _model;
    private readonly AppStateStore _state;
    private readonly HotkeyService? _hotkeys;
    private DesktopLyricWindow? _window;

    /// <summary>探针用:当前歌词窗口(未开启时为 null)。</summary>
    internal DesktopLyricWindow? LyricWindow => _window;

    /// <param name="hotkeys">
    /// 与主窗口共用的快捷键分发：桌面歌词是独立 TopLevel，焦点落在这上面时
    /// 主窗口的隧道处理器收不到按键，就再也解不开穿透（窗口本身已点不动）。
    /// </param>
    public DesktopLyricController(DesktopLyricViewModel model, AppStateStore state,
        HotkeyService? hotkeys = null)
    {
        _model = model;
        _state = state;
        _hotkeys = hotkeys;
        model.PropertyChanged += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopLyricViewModel.Enabled)) Refresh();
        if (e.PropertyName == nameof(DesktopLyricViewModel.IsWindowVisible)) SyncVisibility();
        if (e.PropertyName == nameof(DesktopLyricViewModel.ClickThrough)) ApplyClickThrough();
    }

    private void Refresh()
    {
        if (!_model.Enabled) { CloseWindow(); return; }
        if (_window is null) CreateWindow();
        SyncVisibility();
    }

    /// <summary>
    /// 按"该不该出现"显示或隐藏窗口。暂停只是 Hide，不销毁窗口：位置、合成路径都保留，
    /// 继续播放时原地回来，不会重新算一遍屏幕落点。
    /// </summary>
    private void SyncVisibility()
    {
        var window = _window;
        if (window is null) return;
        if (_model.IsWindowVisible)
        {
            if (window.IsVisible) return;
            // 首帧前固定 layered 样式，切换穿透只改输入标志，避免首切时改变合成路径。
            ApplyClickThrough();
            window.Show();
            ApplyClickThrough();
        }
        else if (window.IsVisible)
        {
            window.Hide();
        }
    }

    private void CreateWindow()
    {
        var window = new DesktopLyricWindow { DataContext = _model };
        _window = window;
        var saved = _state.DesktopLyricX is { } x && _state.DesktopLyricY is { } y
            ? new PixelPoint(x, y) : (PixelPoint?)null;
        // Headless 没有屏幕实现,这里允许 null;真机上 Screens 不会为空。
        var screens = window.Screens;
        var screen = screens is null ? null
            : saved is { } point ? screens.ScreenFromPoint(point) : screens.Primary;
        screen ??= screens?.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            window.Width = Math.Min(720, area.Width / scale);
            var width = (int)(window.Width * scale);
            // Y 不再拿"估计高度"上拉:SizeToContent 的真实高度 Show 之前拿不到,用固定值会把
            // 靠屏幕底保存的位置白白顶上去(拖到底,重启却弹回上面)。这里只保证左上角在工作区内,
            // 首帧后由 ClampIntoWorkArea 按真实高度收边。
            window.Position = new PixelPoint(
                Math.Clamp(saved?.X ?? area.X + (area.Width - width) / 2, area.X, Math.Max(area.X, area.Right - width)),
                Math.Clamp(saved?.Y ?? area.Bottom - (int)(120 * scale), area.Y, Math.Max(area.Y, area.Bottom - 1)));
        }
        window.PositionChanged += OnPositionChanged;
        window.Closed += OnClosed;
        // 高度是 SizeToContent 算出来的:首帧与内容换行后按真实高度把底部收进工作区,
        // 否则贴底保存的位置下次启动会露出屏幕。
        window.Opened += (_, _) =>
        {
            var openedAt = window.Position;
            var openedScreen = window.Screens.ScreenFromPoint(openedAt) ?? window.Screens.Primary;
            AppLogging.Factory.CreateLogger("DesktopLyric").LogInformation(
                "Lyric window opened. Pos={Pos} Client={Client} Screen={Screen} Area={Area}",
                openedAt, window.ClientSize, openedScreen?.Bounds.ToString() ?? "null",
                openedScreen?.WorkingArea.ToString() ?? "null");
            ClampIntoWorkArea(window);
        };
        window.SizeChanged += (_, _) =>
        {
            AppLogging.Factory.CreateLogger("DesktopLyric").LogInformation(
                "Lyric window resized. Client={Client} Pos={Pos}", window.ClientSize, window.Position);
            ClampIntoWorkArea(window);
        };
        if (_hotkeys is not null)
            window.AddHandler(InputElement.KeyDownEvent, OnHotkeyKeyDown, RoutingStrategies.Tunnel);

        AppLogging.Factory.CreateLogger("DesktopLyric").LogInformation(
            "Restore lyric window. Saved=({X},{Y}) Screen={Screen} Area={Area} Final={Final}",
            _state.DesktopLyricX, _state.DesktopLyricY,
            screen?.Bounds.ToString() ?? "null", screen?.WorkingArea.ToString() ?? "null", window.Position);
    }

    /// <summary>把窗口底部收进所在屏幕的工作区(只上推,不下推;尺寸本身不动)。</summary>
    private static void ClampIntoWorkArea(DesktopLyricWindow window)
    {
        if (!window.IsVisible) return;
        var position = window.Position;
        var screens = window.Screens;
        if (screens is null) return;
        var screen = screens.ScreenFromPoint(position) ?? screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var pixelHeight = (int)Math.Ceiling(window.ClientSize.Height * screen.Scaling);
        // 首帧前 SizeChanged 会先报一次 (Width, Width) 的假尺寸(实测 720x720),按它算高度会把
        // 贴底的窗口顶到 1040-720=320。尺寸不可信时什么都不做,等真实尺寸那次。
        if (!DesktopLyricPlacement.IsPlausibleHeight(pixelHeight, area.Height)) return;
        var top = DesktopLyricPlacement.ClampTop(position.Y, pixelHeight, area.Y, area.Bottom);
        if (top != position.Y)
        {
            window.Position = new PixelPoint(position.X, top);
            AppLogging.Factory.CreateLogger("DesktopLyric").LogInformation(
                "Clamp lyric window. Pos={Pos} Height={Height} Area={Area} -> {Top}",
                position, pixelHeight, area, top);
        }
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = (sender as TopLevel)?.FocusManager?.GetFocusedElement();
        _hotkeys?.TryHandle(focused, e);
    }

    private void ApplyClickThrough()
    {
        var handle = _window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        const int transparent = 0x20, layered = 0x80000;
        var styles = GetWindowLong(handle, -20);
        var updated = (styles | layered) & ~transparent;
        if (_model.ClickThrough) updated |= transparent;
        if (updated != styles) SetWindowLong(handle, -20, updated);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        var position = _window?.ExpandedPosition ?? e.Point;
        _state.DesktopLyricX = position.X;
        _state.DesktopLyricY = position.Y;
        _state.ScheduleSave();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 只有用户主动关(× / Alt+F4)才算"不要歌词了"。应用退出时 Avalonia 会连带关闭
        // 所有窗口,那条路径若也写 false,下次启动就看不到歌词了(state.json 里只剩 false)。
        var closedByUser = _window?.ClosedByUser ?? true;
        AppLogging.Factory.CreateLogger("DesktopLyric").LogInformation(
            "Lyric window closed. ByUser={ByUser} Saved=({X},{Y})",
            closedByUser, _state.DesktopLyricX, _state.DesktopLyricY);
        CloseWindow();
        if (closedByUser) _model.Enabled = false;
    }

    private void CloseWindow()
    {
        var window = _window;
        if (window is null) return;
        _window = null;
        window.PositionChanged -= OnPositionChanged;
        window.Closed -= OnClosed;
        if (_hotkeys is not null)
            window.RemoveHandler(InputElement.KeyDownEvent, OnHotkeyKeyDown);
        window.Close();
    }

    public void Dispose()
    {
        _model.PropertyChanged -= OnChanged;
        CloseWindow();
        _state.Flush();
    }
}
