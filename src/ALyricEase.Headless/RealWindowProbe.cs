using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>TEMP-DIAG:用与真实 MainWindow.axaml 逐字节一致的编译型副本
/// (TestMainWindow.axaml)复现真实应用的 XAML 属性应用路径,验证正在播放覆盖层
/// 是否真的盖住标题栏。输出 inset/margin/Bounds/TranslatePoint + 像素采样。</summary>
public static class RealWindowProbe
{
    public static void Run()
    {
        // 与 App.axaml.cs 完全一致:new MainWindow { DataContext = vm }
        var window = new TestMainWindow
        {
            DataContext = ServiceLocator.Get<MainViewModel>(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var shell = window.GetVisualDescendants().OfType<AppShell>().FirstOrDefault();
        if (shell is null)
        {
            Console.WriteLine("[rw] 找不到 AppShell");
            return;
        }
        var overlay = window.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "NowPlayingOverlay");
        if (overlay is null)
        {
            Console.WriteLine("[rw] 找不到 NowPlayingOverlay");
            return;
        }

        Console.WriteLine($"[rw] shellBounds={shell.Bounds.Width:F0}x{shell.Bounds.Height:F0} " +
            $"overlayMargin={overlay.Margin} overlayBounds={overlay.Bounds.Width:F0}x{overlay.Bounds.Height:F0}");

        if (shell.DataContext is not MainViewModel vm)
        {
            Console.WriteLine("[rw] DataContext 不是 MainViewModel");
            return;
        }

        overlay.Transitions = null; // 直接到终态
        vm.OpenNowPlayingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var tl = overlay.TranslatePoint(new Point(0, 0), window);
        Console.WriteLine($"[rw] 打开后: overlay TL(窗口坐标)={tl} overlayBounds={overlay.Bounds.Width:F0}x{overlay.Bounds.Height:F0}");

        var w = (int)window.ClientSize.Width;
        var h = (int)window.ClientSize.Height;
        using (var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96)))
        {
            rtb.Render(window);
            var stride = w * 4;
            var buf = Marshal.AllocHGlobal(stride * h);
            try
            {
                rtb.CopyPixels(new PixelRect(0, 0, w, h), buf, stride * h, stride);
                foreach (var p in new[] { (w / 2, 10), (10, 10), (w - 10, 10), (w / 2, 100) })
                    Console.WriteLine($"[rw] 像素({p.Item1},{p.Item2}) BGRA=" +
                        $"{Marshal.ReadByte(buf, p.Item2 * stride + p.Item1 * 4 + 0)}," +
                        $"{Marshal.ReadByte(buf, p.Item2 * stride + p.Item1 * 4 + 1)}," +
                        $"{Marshal.ReadByte(buf, p.Item2 * stride + p.Item1 * 4 + 2)}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        Console.WriteLine("[rw] done");
    }
}
