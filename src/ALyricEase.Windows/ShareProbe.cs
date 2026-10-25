using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Services.Sharing;
using Windows.ApplicationModel.DataTransfer;
using WinRT;

namespace ALyricEase;

/// <summary>--sharetest [direct|flyout|closed|click|clickraw]:Windows 分享面板真机探针。
/// 生产路径(WindowsShareService)把所有异常吞成 false,且 ShowShareUIForWindow 不抛异常 ≠ 面板真的弹出;
/// 唯一可靠观测量是 DataRequested 事件(面板真正打开时 Windows 才回调)。
/// 场景分进程跑(分享面板互斥,同进程连调会被 RETRYLATER 干扰):
///   direct    = 直接对可见前台窗口调 ShowShareUIForWindow(基线)
///   flyout    = 展开 MenuFlyout(程序化 ShowAt,无真实点击)后走生产 ShareUriAsync
///   closed    = 展开 MenuFlyout 后 Hide 再走生产 ShareUriAsync
///   click     = SendInput **真实鼠标点击**菜单项,处理器走生产 ShareUriAsync(含 SetForegroundWindow 修复)
///   clickraw  = 同 click 但处理器直接调 ShowShareUIForWindow(无 SetForegroundWindow,复现旧代码行为)
/// click/clickraw 的意义:真实点击会让 Avalonia Popup 获得激活 ⇒ 主窗口可能不再是前台,
/// 这才是"早期版本(12.1.1)能分享、现在(12.1.3)不行"最可能的回归点。
/// ⚠ 探针必须在**发布版(AOT)**上也跑一次:2026-10-25 的"又没反应"就是 JIT 全通、AOT 在
/// 取 interop 那步就死(内置 COM 缺失),只跑 Debug 永远复现不出来。</summary>
internal static class ShareProbe
{
    private static readonly Guid DataTransferManagerIid =
        new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    /// <summary>旧路径(依赖内置 COM)的接口声明,只用于诊断它在本宿主能否用。</summary>
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow([In] nint appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] nint appWindow);
    }

    /// <summary>打印两条取用 interop 的路径在本宿主的成败:定位 JIT(可用)/ AOT(旧路径抛)的差异。</summary>
    private static void DiagnoseInterop()
    {
        // 内置 COM 是否可用:任意托管对象取 CCW,PNSE 即 AOT 下没有内置 COM。
        try
        {
            var unknown = Marshal.GetIUnknownForObject(new object());
            Marshal.Release(unknown);
            Console.WriteLine("[share] 内置 COM 可用(GetIUnknownForObject OK)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[share] 内置 COM 不可用: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var legacy = DataTransferManager.As<IDataTransferManagerInterop>();
            Console.WriteLine($"[share] 旧路径 As<T>() 可用: {legacy.GetType().FullName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[share] 旧路径 As<T>() 不可用: {ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
        }

        Console.WriteLine(ShareInterop.TryProbe(out var detail)
            ? $"[share] 生产路径 ShareInterop 可用 ({detail})"
            : $"[share] 生产路径 ShareInterop 不可用: {detail}");
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, nint dwExtraInfo);

    private const uint MouseEventfLeftdown = 0x0002;
    private const uint MouseEventfLeftup = 0x0004;

    private static void ForceForeground(nint hwnd)
    {
        // 探针从后台控制台启动 ⇒ Windows 抢焦点防护会让 SetForegroundWindow 失败;
        // 经典解法:先按一下 Alt 再 Set(真机点菜单时进程拥有前台,不需要这招)。
        keybd_event(0x12, 0, 0, 0); // VK_MENU down
        var ok = SetForegroundWindow(hwnd);
        keybd_event(0x12, 0, 2, 0); // VK_MENU up
        Console.WriteLine($"[share] ForceForeground → {ok}, 现在前台=0x{GetForegroundWindow():X}");
    }

    private static void RealClick(int screenX, int screenY)
    {
        SetCursorPos(screenX, screenY);
        Thread.Sleep(80);
        mouse_event(MouseEventfLeftdown, 0, 0, 0, 0);
        Thread.Sleep(40);
        mouse_event(MouseEventfLeftup, 0, 0, 0, 0);
    }

    public static async Task RunAsync(string mode)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"[share] 模式={mode}");
        var window = new Window { Width = 500, Height = 360, Title = "share-probe" };
        window.Show();
        await Task.Delay(400);
        window.Activate();
        await Task.Delay(400);

        var hwnd = window.TryGetPlatformHandle()?.Handle ?? 0;
        Console.WriteLine($"[share] hwnd=0x{hwnd:X}");
        if (hwnd == 0)
        {
            Console.WriteLine("[share] FAIL 拿不到窗口句柄");
            window.Close();
            return;
        }

        DiagnoseInterop();
        var iid = DataTransferManagerIid;
        var abi = ShareInterop.GetForWindow(hwnd, ref iid);
        var manager = MarshalInterface<DataTransferManager>.FromAbi(abi);
        Console.WriteLine("[share] interop 三步 OK(生产路径 ShareInterop)");

        var dataRequested = false;
        manager.DataRequested += (_, e) =>
        {
            dataRequested = true;
            Console.WriteLine("[share] DataRequested 触发 ⇒ 分享面板真的打开了");
            e.Request.Data.Properties.Title = "分享歌曲:测试";
            e.Request.Data.SetWebLink(new Uri("https://music.163.com/song?id=186016"));
            e.Request.Data.SetText("测试 https://music.163.com/song?id=186016");
        };

        MenuFlyout? menu = null;
        try
        {
            // 前置条件:ShowShareUIForWindow 要求目标窗口是前台窗口,否则静默无效
            Console.WriteLine($"[share] 调用前前台窗口=0x{GetForegroundWindow():X} (目标=0x{hwnd:X})");
            ForceForeground(hwnd);
            await Task.Delay(200);

            switch (mode)
            {
                case "flyout":
                case "closed":
                {
                    var button = new Button { Content = "菜单锚点" };
                    window.Content = button;
                    Dispatcher.UIThread.RunJobs();
                    menu = new MenuFlyout();
                    menu.Items.Add(new MenuItem { Header = "分享" });
                    button.ContextFlyout = menu;
                    menu.ShowAt(button);
                    await Task.Delay(600);
                    Console.WriteLine($"[share] 弹窗已展开,前台=0x{GetForegroundWindow():X}");
                    if (mode == "closed")
                    {
                        menu.Hide();
                        await Task.Delay(800);
                        Console.WriteLine($"[share] 弹窗已关闭,前台=0x{GetForegroundWindow():X}");
                    }

                    // 端到端走生产路径(含 WindowsShareService 里的 SetForegroundWindow 修复)
                    using var svc = new WindowsShareService();
                    var ok = await svc.ShareUriAsync(
                        hwnd, "分享歌曲:测试", "测试描述", new Uri("https://music.163.com/song?id=186016"));
                    Console.WriteLine($"[share] 生产 ShareUriAsync → {ok}");
                    break;
                }

                case "click":
                case "clickraw":
                {
                    Console.WriteLine($"[share] 进入真实点击分支 (mode={mode})");
                    window.Topmost = true; // 后台启动抢不到前台 ⇒ 置顶保证 SendInput 的点击落在菜单上
                    // 真实点击菜单项:复现"用户手点"时的窗口激活状态。
                    // click     = 走生产 ShareUriAsync(含修复)
                    // clickraw  = 处理器里直接 interop.ShowShareUIForWindow(等价修复前的行为)
                    var button = new Button
                    {
                        Content = "菜单锚点",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                        Margin = new Thickness(60, 60, 0, 0),
                    };
                    window.Content = button;
                    Dispatcher.UIThread.RunJobs();

                    MenuItem? shareItem = null;
                    menu = new MenuFlyout();
                    shareItem = new MenuItem { Header = "分享" };
                    shareItem.Click += (_, _) =>
                    {
                        var fg = GetForegroundWindow();
                        var popupRoot = (TopLevel.GetTopLevel(shareItem!))?.TryGetPlatformHandle()?.Handle ?? 0;
                        Console.WriteLine($"[share] 菜单项 Click: 前台=0x{fg:X} 主窗口=0x{hwnd:X} " +
                            $"弹窗HWND=0x{popupRoot:X} (前台是弹窗={fg == popupRoot && popupRoot != 0})");
                        try
                        {
                            if (mode == "clickraw")
                            {
                                ShareInterop.ShowShareUIForWindow(hwnd);
                                Console.WriteLine("[share] clickraw: ShowShareUIForWindow 已调(无 SetForegroundWindow)");
                            }
                            else
                            {
                                using var svc = new WindowsShareService();
                                var ok = svc.ShareUriAsync(
                                    hwnd, "分享歌曲:测试", "真实点击", new Uri("https://music.163.com/song?id=186016"));
                                Console.WriteLine($"[share] click: 生产 ShareUriAsync → {ok}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[share] 处理器异常: {ex.Message} (0x{ex.HResult:X8})");
                        }
                    };
                    menu.Items.Add(shareItem);
                    button.ContextFlyout = menu;
                    menu.ShowAt(button);
                    await Task.Delay(600); // 弹窗完全展开

                    // 菜单项屏幕坐标:弹窗根 TopLevel.PointToScreen(项中心)
                    if (TopLevel.GetTopLevel(shareItem) is { } popup
                        && shareItem.TranslatePoint(
                            new Point(shareItem.Bounds.Width / 2, shareItem.Bounds.Height / 2), popup)
                            is { } pt)
                    {
                        var screen = popup.PointToScreen(pt);
                        Console.WriteLine($"[share] 真实点击菜单项 屏幕=({screen.X},{screen.Y}) 前台=0x{GetForegroundWindow():X}");
                        RealClick(screen.X, screen.Y);
                    }
                    else
                    {
                        Console.WriteLine("[share] FAIL 拿不到菜单项坐标");
                    }

                    await Task.Delay(1500);
                    break;
                }

                default:
                    Console.WriteLine("[share] 直接调用 ShowShareUIForWindow(主窗口)...");
                    ShareInterop.ShowShareUIForWindow(hwnd);
                    break;
            }

            for (var i = 0; i < 15 && !dataRequested; i++)
                await Task.Delay(100);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[share] 调用失败: {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
        }

        Console.WriteLine($"[share] 结果: DataRequested={dataRequested} " +
            "(True=分享面板真的弹出;False=调用没抛异常但面板没出现 ⇒ 即用户看到的\"点击没反应\")");
        window.Close();
    }
}
