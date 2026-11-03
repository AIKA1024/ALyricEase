using System.Runtime.InteropServices;
using ALyricEase.Services.Hotkeys;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Win32;

namespace ALyricEase.Services;

/// <summary>
/// Windows 系统级热键:<c>RegisterHotKey</c> 挂到主窗口的 HWND,在窗口过程里收 <c>WM_HOTKEY</c>。
/// <para>
/// 注册成功后该组合被系统直接吞给本进程,前台窗口收不到 KeyDown —— 所以不会和应用内分发重复触发;
/// 注册失败(被其他程序占用)则系统不吞键,应用在前台时仍由窗口内分发照常响应。
/// </para>
/// <para>
/// 主窗口最小化到托盘时窗口仍然存在,消息照收,所以"应用在后台"也包括托盘状态。
/// </para>
/// </summary>
public sealed class Win32GlobalHotkeyRegistrar : IGlobalHotkeyRegistrar, IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004;
    /// <summary>按住不重复触发(否则长按会连续切歌);Win7 起支持。</summary>
    private const uint ModNoRepeat = 0x4000;

    private readonly Window _window;
    private readonly Action<string> _onPressed;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private readonly Dictionary<int, string> _actionsByHotkeyId = new();
    private readonly Dictionary<string, int> _hotkeyIdsByAction = new(StringComparer.Ordinal);
    private int _nextId = 1;

    /// <param name="window">拿 HWND 的窗口(主窗口;关闭后系统自动回收其热键)。</param>
    /// <param name="onPressed">命中热键时回调动作 Id(已切回 UI 线程)。</param>
    public Win32GlobalHotkeyRegistrar(Window window, Action<string> onPressed)
    {
        _window = window;
        _onPressed = onPressed;
        _hook = OnWndProc;
        Win32Properties.AddWndProcHookCallback(window, _hook);
    }

    public bool TryRegister(string actionId, HotkeyGesture gesture)
    {
        if (_hotkeyIdsByAction.ContainsKey(actionId)) return true;
        if (!VirtualKeys.TryGet(gesture.Key, out var virtualKey)) return false;

        var handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;

        // id 只在本进程内有意义(WM_HOTKEY 的 wParam),从 1 递增即可。
        var id = _nextId++;
        if (!RegisterHotKey(handle, id, ToModifiers(gesture.Modifiers), virtualKey))
            return false;

        _hotkeyIdsByAction[actionId] = id;
        _actionsByHotkeyId[id] = actionId;
        return true;
    }

    public void Unregister(string actionId)
    {
        if (!_hotkeyIdsByAction.Remove(actionId, out var id)) return;
        _actionsByHotkeyId.Remove(id);
        var handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero) UnregisterHotKey(handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var (actionId, _) in _hotkeyIdsByAction.ToArray())
            Unregister(actionId);
    }

    /// <summary>探针用:该动作当前占用的热键 id(未注册为 null)。</summary>
    internal int? GetHotkeyId(string actionId)
        => _hotkeyIdsByAction.TryGetValue(actionId, out var id) ? id : null;

    private IntPtr OnWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        if (!_actionsByHotkeyId.TryGetValue(wParam.ToInt32(), out var actionId)) return IntPtr.Zero;

        handled = true;
        // 不在窗口过程里直接跑动作(可能关窗/改集合):排到下一轮 UI 消息。
        Dispatcher.UIThread.Post(() => _onPressed(actionId));
        return IntPtr.Zero;
    }

    private static uint ToModifiers(KeyModifiers modifiers)
    {
        var result = ModNoRepeat;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= ModControl;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= ModShift;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= ModAlt;
        // Win 组合在录入阶段就被拒收,这里不映射 MOD_WIN。
        return result;
    }

    public void Dispose() => UnregisterAll();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// Avalonia <see cref="Key"/> → Win32 虚拟键码。
/// <para>
/// Avalonia 的键值沿用 WPF/Win32 的 VK 编号,但这里只放行白名单:
/// 传错一个值会让 <c>RegisterHotKey</c> 静默返回 false,不如提前判定"不可注册"。
/// </para>
/// </summary>
internal static class VirtualKeys
{
    public static bool TryGet(Key key, out uint virtualKey)
    {
        switch (key)
        {
            case >= Key.A and <= Key.Z:
            case >= Key.D0 and <= Key.D9:
            case >= Key.F1 and <= Key.F24:
                virtualKey = (uint)key;
                return true;
            case Key.Space: virtualKey = 0x20; return true;
            case Key.Return: virtualKey = 0x0D; return true;
            case Key.Back: virtualKey = 0x08; return true;
            case Key.Delete: virtualKey = 0x2E; return true;
            case Key.Insert: virtualKey = 0x2D; return true;
            case Key.Pause: virtualKey = 0x13; return true;
            case Key.Scroll: virtualKey = 0x91; return true;
            case Key.PrintScreen: virtualKey = 0x2C; return true;
            case Key.MediaNextTrack: virtualKey = 0xB0; return true;
            case Key.MediaPreviousTrack: virtualKey = 0xB1; return true;
            case Key.MediaStop: virtualKey = 0xB2; return true;
            case Key.MediaPlayPause: virtualKey = 0xB3; return true;
            case Key.VolumeMute: virtualKey = 0xAD; return true;
            case Key.VolumeDown: virtualKey = 0xAE; return true;
            case Key.VolumeUp: virtualKey = 0xAF; return true;
            case Key.BrowserBack: virtualKey = 0xA6; return true;
            case Key.BrowserForward: virtualKey = 0xA7; return true;
            case Key.BrowserRefresh: virtualKey = 0xA8; return true;
            case Key.BrowserSearch: virtualKey = 0xAA; return true;
            case Key.BrowserFavorites: virtualKey = 0xAB; return true;
            case Key.BrowserHome: virtualKey = 0xAC; return true;
            case Key.Home: virtualKey = 0x24; return true;
            case Key.End: virtualKey = 0x23; return true;
            case Key.PageUp: virtualKey = 0x21; return true;
            case Key.PageDown: virtualKey = 0x22; return true;
            case Key.Left: virtualKey = 0x25; return true;
            case Key.Up: virtualKey = 0x26; return true;
            case Key.Right: virtualKey = 0x27; return true;
            case Key.Down: virtualKey = 0x28; return true;
            case Key.OemPlus: virtualKey = 0xBB; return true;
            case Key.OemMinus: virtualKey = 0xBD; return true;
            case Key.OemComma: virtualKey = 0xBC; return true;
            case Key.OemPeriod: virtualKey = 0xBE; return true;
            case Key.OemQuestion: virtualKey = 0xBF; return true;
            case Key.OemSemicolon: virtualKey = 0xBA; return true;
            case Key.OemQuotes: virtualKey = 0xDE; return true;
            case Key.OemOpenBrackets: virtualKey = 0xDB; return true;
            case Key.OemCloseBrackets: virtualKey = 0xDD; return true;
            case Key.OemPipe: virtualKey = 0xDC; return true;
            case Key.OemTilde: virtualKey = 0xC0; return true;
            default:
                virtualKey = 0;
                return false;
        }
    }
}
