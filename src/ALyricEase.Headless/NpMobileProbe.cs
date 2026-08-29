using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>复现 Android MainView 结构(Grid 48,* + 顶部横幅 + 横跨两行的正在播放覆盖层),
/// 手机尺寸下验证:打开时覆盖层盖住顶部横幅,关闭时横幅露出;顺带打印详情页控件几何。</summary>
public static class NpMobileProbe
{
    private const int W = 412;
    private const int H = 800;

    public static void Run()
    {
        // 不含 ExtendClientArea:Avalonia 的 AndroidInsetsManager 已按系统栏 insets 给视图加过 padding,
        // 所以 MainView 的 y=0 本来就在状态栏下方,用普通窗口模拟即可。
        var window = new Window { Width = W, Height = H, Background = Brushes.White };

        var root = new Grid { RowDefinitions = new RowDefinitions("48,*") };

        // 顶部横幅:染成纯红,方便用像素采样判断有没有被盖住
        var banner = new Border { Background = Brushes.Red };
        Grid.SetRow(banner, 0);
        root.Children.Add(banner);

        var shell = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() };
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);

        var overlay = new Grid { Name = "NowPlayingOverlay", ZIndex = 100, ClipToBounds = true };
        overlay.Children.Add(new NowPlayingView());
        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, 2);
        root.Children.Add(overlay);

        window.Content = root;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainViewModel)shell.DataContext!;
        var np = overlay.GetVisualDescendants().OfType<NowPlayingView>().First();

        Console.WriteLine($"[npm] 窗口={window.Bounds.Width:F0}x{window.Bounds.Height:F0}");
        Console.WriteLine($"[npm] banner={banner.Bounds.Width:F0}x{banner.Bounds.Height:F0} " +
                          $"overlay={overlay.Bounds.Width:F0}x{overlay.Bounds.Height:F0} (期望 {W}x{H})");

        overlay.Transitions = null; // 探针只看终态,不要 0.4s 过渡干扰采样

        // 1) 关闭态:覆盖层整块滑到屏幕外 → 横幅应露出(红)
        //    位移取 Controller 同款算法:Bounds.Height + 8
        var closedY = overlay.Bounds.Height + 8;
        SetTranslateY(overlay, closedY);
        Console.WriteLine($"[npm] 关闭态位移={closedY:F0} (Controller 算法:Bounds.Height+8)");
        Sample(window, "关闭态", expectRed: true);

        // 2) 打开:覆盖层归位 → 横幅应被完全盖住(不红)
        vm.OpenNowPlayingCommand.Execute(null);
        SetTranslateY(overlay, 0);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var tl = overlay.TranslatePoint(new Point(0, 0), window);
        Console.WriteLine($"[npm] 打开后 overlay 窗口坐标 TL={tl} (期望 (0,0))");
        Sample(window, "打开态", expectRed: false);

        // 3) 详情页控件几何:确认横跨后布局没算出重叠/越界
        foreach (var name in new[] { "Artwork", "InfoPanel", "ProgressArea", "CollapseButton", "PlayButton", "QueueToggle" })
        {
            var c = np.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x.Name == name);
            if (c is null) { Console.WriteLine($"[npm] {name}: 未找到"); continue; }
            var t = c.RenderTransform?.Value ?? Matrix.Identity;
            var y = c.Bounds.Top + t.M32;
            var x = c.Bounds.Left + t.M31;
            Console.WriteLine($"[npm] {name}: x={x:F0} y={y:F0} {c.Bounds.Width:F0}x{c.Bounds.Height:F0} " +
                              $"底={y + c.Bounds.Height:F0}");
        }

        Console.WriteLine("[npm] done");
    }

    private static void SetTranslateY(Control c, double y)
    {
        var builder = TransformOperations.CreateBuilder(1);
        builder.AppendTranslate(0, y);
        c.RenderTransform = builder.Build();
    }

    /// <summary>在顶部横幅带内采样若干点,统计仍为"横幅红"的比例。
    /// 打开态应为 0(被盖住),关闭态应为全部(横幅露出)。</summary>
    private static void Sample(Window window, string stage, bool expectRed)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(W, H), new Vector(96, 96));
        rtb.Render(window);
        var stride = W * 4;
        var buf = Marshal.AllocHGlobal(stride * H);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, W, H), buf, stride * H, stride);
            // 横幅带(y<48)内取 5 点,含左右两端与贴边位置
            var probes = new[] { (10, 10), (60, 24), (206, 4), (360, 10), (400, 44) };
            var red = 0;
            foreach (var (px, py) in probes)
            {
                var b = Marshal.ReadByte(buf, py * stride + px * 4 + 0);
                var g = Marshal.ReadByte(buf, py * stride + px * 4 + 1);
                var r = Marshal.ReadByte(buf, py * stride + px * 4 + 2);
                if (r > 200 && g < 60 && b < 60) red++;
            }
            var expect = expectRed ? $"全部({probes.Length})为红 = 横幅露出" : "无红 = 已被盖住";
            var ok = expectRed ? red == probes.Length : red == 0;
            Console.WriteLine($"[npm] {stage}: 横幅带采样 {red}/{probes.Length} 点红 | 期望 {expect} " +
                              $"→ {(ok ? "OK" : "不符预期")}");
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
