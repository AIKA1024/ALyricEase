using System;
using System.IO;
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

/// <summary>TEMP-DIAG:复现 MainWindow 结构(亚克力/ExtendClientArea + 窗口级覆盖层),
/// 验证正在播放覆盖层盖住标题栏。与真实 MainWindow.axaml 结构一致:覆盖层直接挂窗口网格
/// 横跨两行(不进 AppShell)。两种模式:--npcover(清过渡直接终态)/ --npcover-anim(走 0.4s 动画)。</summary>
public static class NpCoverProbe
{
    public static void Run(bool withAnimation)
    {
        var window = new Window
        {
            Width = 1200,
            Height = 720,
            Title = "ALyricEase",
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur],
            WindowDecorations = WindowDecorations.BorderOnly,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 48,
        };

        var acrylic = new ExperimentalAcrylicBorder
        {
            Material = new ExperimentalAcrylicMaterial
            {
                BackgroundSource = AcrylicBackgroundSource.Digger,
                TintColor = Colors.Black,
                TintOpacity = 0.5,
            },
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("48,*") };
        var titleBar = new Grid
        {
            Background = Brushes.Red,
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        titleBar.Children.Add(new Button { Content = "←" });
        var close = new Button { Content = "✕" };
        Grid.SetColumn(close, 2);
        titleBar.Children.Add(close);
        root.Children.Add(titleBar);

        var shell = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() };
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);

        // 窗口级覆盖层:横跨两行 → 盖住标题栏(与 MainWindow.axaml 一致)
        var overlay = new Grid
        {
            Name = "NowPlayingOverlay",
            ZIndex = 100,
            ClipToBounds = true,
        };
        overlay.Children.Add(new NowPlayingView());
        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, 2);
        root.Children.Add(overlay);

        acrylic.Child = root;
        window.Content = acrylic;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)shell.DataContext!;
        Console.WriteLine($"[np] shellBounds={shell.Bounds.Width:F0}x{shell.Bounds.Height:F0} " +
            $"overlayBounds={overlay.Bounds.Width:F0}x{overlay.Bounds.Height:F0}");

        if (!withAnimation)
            overlay.Transitions = null; // 直接到终态
        vm.OpenNowPlayingCommand.Execute(null);

        if (withAnimation)
        {
            for (var i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(25);
                Dispatcher.UIThread.RunJobs();
            }
        }

        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var tl = overlay.TranslatePoint(new Point(0, 0), window)!;
        Console.WriteLine($"[np] 打开后({(withAnimation ? "动画" : "终态")}): overlay TL(窗口坐标)={tl} " +
            $"overlay={overlay.Bounds.Width:F0}x{overlay.Bounds.Height:F0}");

        using (var rtb = new RenderTargetBitmap(new PixelSize(1200, 720), new Vector(96, 96)))
        {
            rtb.Render(window);
            var stride = 1200 * 4;
            var buf = Marshal.AllocHGlobal(stride * 720);
            try
            {
                rtb.CopyPixels(new PixelRect(0, 0, 1200, 720), buf, stride * 720, stride);
                foreach (var p in new[] { (600, 10), (10, 10), (1190, 10), (600, 100) })
                    Console.WriteLine($"[np] 像素({p.Item1},{p.Item2}) BGRA=" +
                        $"{Marshal.ReadByte(buf, p.Item2 * stride + p.Item1 * 4 + 0)}," +
                        $"{Marshal.ReadByte(buf, p.Item2 * stride + p.Item1 * 4 + 1)}," +
                        $"{Marshal.ReadByte(buf, p.Item2 * stride + p.Item1 * 4 + 2)}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        Console.WriteLine("[np] done");
    }
}
