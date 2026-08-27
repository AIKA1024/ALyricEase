using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging; // TEMP-DIAG
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>响应式布局无头复现:不同窗口宽度下检查 PlayerBarView 的类应用与按钮可见性。</summary>
public static class Program
{
    private static Window? _window;

    [STAThread]
    public static void Main(string[] args)
    {
        AppBuilder.Configure<HeadlessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        HeadlessApp.ConfigureServices();

        if (args.Length > 0 && args[0] == "--probe")
        {
            PerfProbe.Run();
            return;
        }

        // QQ 音乐 API 冒烟:匿名打真实接口验证歌单修复(不依赖登录 Cookie)
        if (args.Length > 0 && args[0] == "--qqapi")
        {
            // 放线程池执行:STA Main + Avalonia SyncContext 下直接阻塞等待会与 await 延续互等(死锁)
            Environment.ExitCode = System.Threading.Tasks.Task.Run(QqApiProbe.RunAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // QQ 音乐红心端到端实测(需本机有效 Cookie):AddSonglist→云端复检→DelSonglist 还原
        if (args.Length > 0 && args[0] == "--qqlike")
        {
            Environment.ExitCode = System.Threading.Tasks.Task.Run(QqApiProbe.RunLikeProbeAsync)
                .GetAwaiter().GetResult();
            return;
        }

        // TEMP-DIAG:正在播放覆盖层是否盖住标题栏
        if (args.Length > 0 && args[0] == "--npcover")
        {
            NpCoverProbe.Run(args.Length > 1 && args[1] == "anim");
            return;
        }

        // TEMP-DIAG:真实 MainWindow.axaml 解析验证
        if (args.Length > 0 && args[0] == "--realwin")
        {
            RealWindowProbe.Run();
            return;
        }

        _window = new Window
        {
            Content = new AppShell { DataContext = ServiceLocator.Get<MainViewModel>() },
        };
        _window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var width in new[] { 400.0, 640.0, 700.0, 1200.0 })
        {
            _window.Width = width;
            _window.Height = 800;
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            Dump(width);
        }

        SyntheticTest();
        TapBackgroundTest();
    }

    /// <summary>窄屏下点播放条空白区(非按钮)应打开正在播放页。</summary>
    private static void TapBackgroundTest()
    {
        _window!.Width = 400;
        _window.Height = 800;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var bar = _window.GetVisualDescendants().OfType<PlayerBarView>().FirstOrDefault();
        var shellVm = _window.GetVisualDescendants().OfType<AppShell>().FirstOrDefault()?.DataContext as MainViewModel;
        if (bar is null || shellVm is null)
        {
            Console.WriteLine("[tap] 找不到 PlayerBarView/AppShell");
            return;
        }

        // 播放条内容行空白处(标题左侧与封面之间偏上,避开按钮/文本也可命中:Grid 有透明背景)
        var point = bar.TranslatePoint(new Point(200, 45), _window);
        if (point is null)
        {
            Console.WriteLine("[tap] 坐标换算失败");
            return;
        }

        _window.MouseMove(point.Value);
        _window.MouseDown(point.Value, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        _window.MouseUp(point.Value, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Console.WriteLine($"[tap] 点击播放条空白区后 ShowNowPlaying={shellVm.ShowNowPlaying} (期望 True)");

        DragProgressTest(bar, shellVm);
    }

    /// <summary>窄屏下拖动紧凑进度条:球和 ScrubPositionMs 应跟随。</summary>
    private static void DragProgressTest(PlayerBarView bar, MainViewModel shellVm)
    {
        shellVm.CloseNowPlayingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var vm = (bar.DataContext as ViewModels.PlayerViewModel)!;
        var point = bar.TranslatePoint(new Point(100, 2), _window!)!.Value;
        _window!.MouseMove(point);
        _window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var moved = bar.TranslatePoint(new Point(300, 2), _window)!.Value;
        _window.MouseMove(moved);
        Dispatcher.UIThread.RunJobs();
        DumpAndShot(bar, "CompactPlayer", "aly-narrow-drag"); // TEMP-DIAG
        _window.MouseUp(moved, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // 按父容器区分宽/紧凑实例:父级隐藏时子元素自身 IsVisible 仍为 true,不能用它筛
        var progress = bar.GetVisualDescendants().OfType<PlayerProgressBar>()
            .FirstOrDefault(p => p.GetVisualParent() is Grid { Name: "CompactPlayer" });
        var thumb = progress?.FindControl<Grid>("Thumb");
        var bubble = progress?.FindControl<Border>("Bubble");
        Console.WriteLine($"[drag] 拖到 300/400 处: ScrubPositionMs={vm.ScrubPositionMs:F0} (期望≈135000) " +
            $"球Margin.Left={thumb?.Margin.Left:F0} (期望≈290) 气泡可见={bubble?.IsVisible} (期望 True)");
        var fill = progress?.FindControl<Ellipse>("ThumbFill");
        Console.WriteLine($"[ball] 静止内球大小: {fill?.Width:F0}x{fill?.Height:F0} (期望 10x10)");

        WideBubbleTest();
    }

    /// <summary>宽屏下拖动进度条时应显示时间气泡。</summary>
    private static void WideBubbleTest()
    {
        _window!.Width = 1200;
        _window.Height = 800;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var bar = _window.GetVisualDescendants().OfType<PlayerBarView>().FirstOrDefault();
        var shellVm = _window.GetVisualDescendants().OfType<AppShell>().FirstOrDefault()?.DataContext as MainViewModel;
        if (bar is null || shellVm is null) return;

        var progress = bar.GetVisualDescendants().OfType<PlayerProgressBar>()
            .FirstOrDefault(p => p.GetVisualParent() is Grid { Name: "WidePlayer" });
        if (progress is null)
        {
            Console.WriteLine("[bubble] 找不到宽屏进度条");
            return;
        }

        var point = bar.TranslatePoint(new Point(600, 0), _window)!.Value;
        _window.MouseMove(point);
        _window.MouseDown(point, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        DumpAndShot(bar, "WidePlayer", "aly-wide-drag"); // TEMP-DIAG

        var bubble = progress.FindControl<Border>("Bubble");
        Console.WriteLine($"[bubble] 拖动中 Bubble.IsVisible={bubble?.IsVisible} margin=({bubble?.Margin.Left:F0},{bubble?.Margin.Top:F0}) (期望 True)");

        _window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[bubble] 松开后 Bubble.IsVisible={bubble?.IsVisible} (期望 False)");
    }

    /// <summary>TEMP-DIAG:打印指定播放条实例中气泡状态并渲染窗口截图。</summary>
    private static void DumpAndShot(PlayerBarView bar, string parentGrid, string name)
    {
        var prog = bar.GetVisualDescendants().OfType<PlayerProgressBar>()
            .FirstOrDefault(p => p.GetVisualParent() is Grid g && g.Name == parentGrid);
        var bub = prog?.FindControl<Border>("Bubble");
        var thumb = prog?.FindControl<Grid>("Thumb");
        Console.WriteLine($"[diag:{name}] prog={prog?.Bounds} thumb.Margin=({thumb?.Margin.Left:F0},{thumb?.Margin.Top:F0}) " +
            $"bubble.IsVisible={bub?.IsVisible} bubble.Margin=({bub?.Margin.Left:F0},{bub?.Margin.Top:F0}) bubble.Bounds={bub?.Bounds}");
        if (bub is { IsVisible: true } && prog is not null)
        {
            var tl = bub.TranslatePoint(new Point(0, 0), _window!);
            Console.WriteLine($"[diag:{name}] bubble 窗口坐标 TL={tl}");
        }
        var px = new PixelSize((int)_window!.ClientSize.Width, (int)_window!.ClientSize.Height);
        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(_window);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name + ".png");
        rtb.Save(path);
        Console.WriteLine($"[diag:{name}] 截图: {path}");
    }

    /// <summary>合成用例:UserControl.Styles 里 "UserControl.class 后代" 选择器是否能激活。</summary>
    private static void SyntheticTest()
    {
        var host = new SyHost();
        var grid = new Grid { Name = "SynGrid" };
        host.Content = grid;

        // 与 PlayerBarView 相同的结构:基样式设 False,带 UserControl 前缀的样式设 True
        host.Styles.Add(new Style(s => s.OfType<Grid>().Name("SynGrid"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, false) },
        });
        host.Styles.Add(new Style(s => s.OfType<UserControl>().Class("sy").Descendant().OfType<Grid>().Name("SynGrid"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });

        var win = new Window { Content = host, Width = 300, Height = 200 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[synthetic] 未加类: grid.IsVisible={grid.IsVisible} (期望 False)");

        host.Classes.Add("sy");
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[synthetic] 加类后: grid.IsVisible={grid.IsVisible} (期望 True; 若为 False 则前缀选择器失效)");

        // 变体A:去掉类型名,只用 .sy 类选择器做前缀
        var hostA = new SyHost();
        var gridA = new Grid { Name = "SynGridA" };
        hostA.Content = gridA;
        hostA.Styles.Add(new Style(s => s.Class("sy").Descendant().OfType<Grid>().Name("SynGridA"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });
        var winA = new Window { Content = hostA, Width = 300, Height = 200 };
        winA.Show();
        Dispatcher.UIThread.RunJobs();
        hostA.Classes.Add("sy");
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[synthetic] 变体A(.sy 前缀): gridA.IsVisible={gridA.IsVisible} (期望 True)");

        // 变体B:类放在 UserControl 内部的中间层 Grid 上,前缀匹配该中间层(非样式宿主)
        var hostB = new SyHost();
        var middle = new Grid { Name = "Middle" };
        var gridB = new Grid { Name = "SynGridB" };
        middle.Children.Add(gridB);
        hostB.Content = middle;
        hostB.Styles.Add(new Style(s => s.OfType<Grid>().Name("Middle").Class("sy").Descendant().OfType<Grid>().Name("SynGridB"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, true) },
        });
        var winB = new Window { Content = hostB, Width = 300, Height = 200 };
        winB.Show();
        Dispatcher.UIThread.RunJobs();
        middle.Classes.Add("sy");
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[synthetic] 变体B(内部中间层带类): gridB.IsVisible={gridB.IsVisible} (期望 True)");
    }

    private sealed class SyHost : UserControl
    {
    }

    private static void Dump(double width)
    {
        var bar = _window?.GetVisualDescendants().OfType<PlayerBarView>().FirstOrDefault();

        // 模拟有歌曲播放:让进度区可见,验证紧凑条进度球位置计算
        var vm = bar?.DataContext as ViewModels.PlayerViewModel;
        if (vm is { CurrentSong: null })
        {
            vm.CurrentSong = new Models.Song { Id = 1, Name = "测试歌曲", DurationMs = 180_000 };
            vm.DurationMs = 180_000;
            vm.PositionMs = 60_000;
            Dispatcher.UIThread.RunJobs();
        }
        if (bar is null)
        {
            Console.WriteLine($"[{width}] PlayerBarView NOT FOUND");
            return;
        }

        var classes = string.Join(",", new[] { "wide", "compact", "narrow" }.Where(c => bar.Classes.Contains(c)));
        var wide = bar.FindControl<Grid>("WidePlayer");
        var compact = bar.FindControl<Grid>("CompactPlayer");

        Console.WriteLine($"--- width={width} window={_window!.ClientSize.Width:F0} bounds={bar.Bounds.Width:F0} classes=[{classes}]");
        Console.WriteLine($"    WidePlayer.IsVisible={wide?.IsVisible} CompactPlayer.IsVisible={compact?.IsVisible}");

        if (wide is { IsVisible: true })
            Console.WriteLine($"    Wide 按钮数: {CountButtons(wide)}");
        if (compact is { IsVisible: true })
        {
            Console.WriteLine($"    Compact 按钮数: {CountButtons(compact)} bar高={bar.Bounds.Height:F0}");
            var progress = compact.GetVisualDescendants().OfType<PlayerProgressBar>()
                .FirstOrDefault(p => p.IsVisible);
            var thumb = progress?.FindControl<Grid>("Thumb");
            var track = progress?.FindControl<Grid>("Track");
            if (thumb is not null && track is not null)
                Console.WriteLine($"    紧凑进度球: track宽={track.Bounds.Width:F0} 球Margin.Left={thumb.Margin.Left:F0} 球可见={thumb.IsVisible}");

            // 无真实歌曲时强制显示按钮组和图标,检查布局与字形
            var panel = compact.GetVisualDescendants().OfType<StackPanel>()
                .FirstOrDefault(p => p.Orientation == Orientation.Horizontal);
            if (panel is not null)
            {
                panel.IsVisible = true;
                Dispatcher.UIThread.RunJobs();
                foreach (var b in panel.Children.OfType<Button>())
                {
                    var icons = b.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.Text?.Length > 0).ToList();
                    foreach (var icon in icons) icon.IsVisible = true;
                }
                Dispatcher.UIThread.RunJobs();
                foreach (var b in panel.Children.OfType<Button>())
                {
                    var icon = b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text?.Length > 0);
                    Console.WriteLine($"    按钮 {b.Bounds.Width:F0}x{b.Bounds.Height:F0} 图标='{Describe(icon?.Text)}' 图标bounds={icon?.Bounds.Width:F0}x{icon?.Bounds.Height:F0}");
                }
            }
        }
    }

    private static string Describe(string? text) =>
        text is null ? "(无)"
            : text.All(c => c > 0x7F) && text.Length > 0 ? $"U+{(int)text[0]:X4}"
            : text;

    private static int CountButtons(Grid root) =>
        root.GetVisualDescendants().OfType<Button>().Count(b => b.IsVisible);
}
