using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using ALyricEase.Services.Sharing;
using Windows.ApplicationModel.DataTransfer;
using WinRT;

namespace ALyricEase;

/// <summary>--sharetest [direct|flyout|closed]:Windows 分享面板真机探针。
/// 生产路径(WindowsShareService)把所有异常吞成 false,且 ShowShareUIForWindow 不抛异常 ≠ 面板真的弹出;
/// 唯一可靠观测量是 DataRequested 事件(面板真正打开时 Windows 才回调)。
/// 三个场景分进程跑(分享面板互斥,同进程连调会被 RETRYLATER 干扰):
///   direct = 直接对可见前台窗口调 ShowShareUIForWindow(基线,应弹出)
///   flyout = 先展开 MenuFlyout(模拟真实"点菜单项"时弹窗开着)再调
///   closed = 展开 MenuFlyout 后 Hide,等弹窗关闭再调
/// DataRequested=True 即面板弹出;False = 调用成功但面板没出现(即用户看到的"点击没反应")。</summary>
internal static class ShareProbe
{
    private static readonly Guid DataTransferManagerIid =
        new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow([In] nint appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] nint appWindow);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    private static void ForceForeground(nint hwnd)
    {
        // 探针从后台控制台启动 ⇒ Windows 抢焦点防护会让 SetForegroundWindow 失败;
        // 经典解法:先按一下 Alt 再 Set(真机点菜单时进程拥有前台,不需要这招)。
        keybd_event(0x12, 0, 0, 0); // VK_MENU down
        var ok = SetForegroundWindow(hwnd);
        keybd_event(0x12, 0, 2, 0); // VK_MENU up
        Console.WriteLine($"[share] ForceForeground → {ok}, 现在前台=0x{GetForegroundWindow():X}");
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

        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        var abi = interop.GetForWindow(hwnd, ref iid);
        var manager = MarshalInterface<DataTransferManager>.FromAbi(abi);
        Console.WriteLine("[share] interop 三步 OK");

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
                    await Task.Delay(600); // 等弹窗完全展开(此刻 Popup 是前台窗口)
                    Console.WriteLine($"[share] 弹窗已展开,前台=0x{GetForegroundWindow():X}");
                    if (mode == "closed")
                    {
                        menu.Hide();
                        await Task.Delay(800); // 等弹窗完全关闭
                        Console.WriteLine($"[share] 弹窗已关闭,前台=0x{GetForegroundWindow():X}");
                    }

                    // 端到端走生产路径(含 WindowsShareService 里的 SetForegroundWindow 修复)
                    using var svc = new WindowsShareService();
                    var ok = await svc.ShareUriAsync(
                        hwnd, "分享歌曲:测试", "测试描述", new Uri("https://music.163.com/song?id=186016"));
                    Console.WriteLine($"[share] 生产 ShareUriAsync → {ok}");
                    break;
                }
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
