using System.Runtime.InteropServices;
using ALyricEase.Services;
using ALyricEase.Services.Hotkeys;
using Avalonia.Controls;
using Avalonia.Input;

namespace ALyricEase;

/// <summary>
/// 系统级热键真机探针(--hotkeytest)。Headless 里用的是假注册器,这里验证真链路:
/// <c>RegisterHotKey</c> 能占住组合、真实按键能变成 <c>WM_HOTKEY</c> 并解析成动作 Id、注销后可再次占用。
/// <para>
/// ⚠ 必须用 await 让出 UI 线程:阻塞式 <c>Dispatcher.UIThread.RunJobs()</c> 只跑 Avalonia 的
/// dispatcher 队列,不驱动 Win32 消息循环,窗口消息(含 WM_HOTKEY)会一直躺在队列里 —— 探针会假失败。
/// </para>
/// </summary>
internal static class GlobalHotkeyProbe
{
    public static async Task RunAsync()
    {
        var window = new Window { Width = 200, Height = 120 };
        var received = new List<string>();
        window.Show();
        await Task.Delay(200);
        // 与 App 里的顺序一致:先 Show 再挂钩子 —— 必须验证这个顺序也能收到 WM_HOTKEY。
        var registrar = new Win32GlobalHotkeyRegistrar(window, id => received.Add(id));

        try
        {
            // 单键:白名单(Pause / ScrollLock / 媒体键)可以注册;空格这类会被共享规则挡在 UI 之前。
            Check(GlobalHotkeyRules.CanRegister(new HotkeyGesture(Key.Pause, KeyModifiers.None)),
                "Pause 允许作为单键全局热键");
            Check(!GlobalHotkeyRules.CanRegister(new HotkeyGesture(Key.Space, KeyModifiers.None)),
                "空格不允许作为单键全局热键");
            if (registrar.TryRegister("probe-pause", new HotkeyGesture(Key.Pause, KeyModifiers.None)))
            {
                registrar.Unregister("probe-pause");
                Console.WriteLine("[hotkeytest] Pause 单键注册成功");
            }
            else
            {
                // 被其他程序(某些播放器会占 Pause)占用不算失败 —— 规则本身仍然正确。
                Console.WriteLine("[hotkeytest] Pause 单键被其他程序占用，跳过实际注册");
            }

            // 挑一个没被别的软件占用的组合;三个都占说明环境不适合跑这条探针。
            var gesture = HotkeyGesture.None;
            foreach (var candidate in new[]
                     {
                         new HotkeyGesture(Key.F12, KeyModifiers.Control | KeyModifiers.Shift),
                         new HotkeyGesture(Key.F11, KeyModifiers.Control | KeyModifiers.Alt),
                         new HotkeyGesture(Key.F10, KeyModifiers.Alt | KeyModifiers.Shift),
                     })
            {
                if (!registrar.TryRegister("probe-primary", candidate)) continue;
                gesture = candidate;
                break;
            }

            Check(!gesture.IsEmpty, "三个候选组合都被占用，请先关掉占用它们的程序");
            var hotkeyId = registrar.GetHotkeyId("probe-primary");
            Check(hotkeyId is not null, "注册成功并拿到热键 id");

            // ① 消息路径:RegisterHotKey 成功即表示系统认了这个组合,之后按键会以 WM_HOTKEY
            //    投到注册窗口。这里直接投同样的消息,验证窗口过程 → 动作 Id 的解析。
            //    ② "物理按键 → WM_HOTKEY" 由系统负责:模拟注入(keybd_event/SendInput)在
            //    非前台、跨完整性级别时会被丢弃,拿它做断言只会得到假失败,故不测。
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            PostMessage(handle, WmHotkey, new IntPtr(hotkeyId!.Value), IntPtr.Zero);
            await Task.Delay(300);
            Check(received.Contains("probe-primary"), "WM_HOTKEY 被解析成动作 Id 并回调");

            // 同一动作重复注册是空操作(不重复占坑、不换 id),也就不会一次按键触发两次。
            Check(registrar.TryRegister("probe-primary", gesture), "重复注册同一动作返回成功");
            Check(registrar.GetHotkeyId("probe-primary") == hotkeyId, "重复注册不换 id");
            received.Clear();
            PostMessage(handle, WmHotkey, new IntPtr(hotkeyId.Value), IntPtr.Zero);
            await Task.Delay(300);
            Check(received.Count == 1, "重复注册不会让一次按键触发两次");

            registrar.Unregister("probe-primary");
            Check(registrar.TryRegister("probe-primary", gesture), "注销后能重新占用同一组合");
            registrar.UnregisterAll();

            Console.WriteLine($"[hotkeytest] 全部通过（探测组合 {gesture.ToDisplayString()}）");
        }
        finally
        {
            registrar.Dispose();
            window.Close();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private const uint WmHotkey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
