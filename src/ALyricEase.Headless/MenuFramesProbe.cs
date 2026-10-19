using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>
/// MenuFlyout 打开动画逐帧探针(--menu-frames)。
///
/// 为什么单独做:菜单是独立弹窗,动画跑在合成器上,Bounds/属性都读不到真实位移;
/// 而 UI 线程抓屏(编码 PNG)本身要 20~40ms,会把动画帧打乱。所以这里
/// **后台线程按 ~8ms 抓屏幕**,UI 线程只负责 ShowAt,抓到的是不被打扰的真实呈现。
///
/// 判定方式:先抓一帧"打开前"作基线,之后每帧与基线求差,取变化像素的包围盒 = 菜单当前
/// 在屏幕上的矩形。打印 top/bottom/height 随时间的变化,就能看出:
///   ① 首帧是否已经在最终位置(闪现)  ② 是否从半张滑入  ③ 位移曲线是否像 cubic-bezier(0,0,0,1)。
/// </summary>
internal static class MenuFramesProbe
{
    private static readonly string[] Items =
    [
        "播放", "下一首", "添加到播放列表", "查看专辑", "复制链接", "属性",
    ];

    public static async Task<int> RunAsync()
    {
        var window = new Window
        {
            Width = 720,
            Height = 520,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#1F1F1F")),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowActivated = true,
        };

        var button = new Button
        {
            Content = "打开菜单",
            Width = 120,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(170, 150, 0, 0),
        };

        var root = new Panel { Children = { button } };
        window.Content = root;
        window.Show();
        window.Topmost = true;
        window.Activate();
        await DrainAsync(500);

        long shownAtTs = 0;
        long openedAtTs = 0;
        long startedAtTs = 0;
        double startedDx = 0, startedDy = 0;
        bool startedAnimated = false;

        void OnOpened(object? sender, EventArgs e) => openedAtTs = Stopwatch.GetTimestamp();

        void OnStarted(Visual surface, double dx, double dy)
        {
            startedAtTs = Stopwatch.GetTimestamp();
            startedDx = dx;
            startedDy = dy;
            startedAnimated = FlyoutOpenAnimation.LastAnimated;
        }

        static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
        double Rel(long ts) => ts == 0 ? -1 : Ms(shownAtTs, ts);

        var origin = window.PointToScreen(default);
        var scale = window.RenderScaling;

        // 只抓菜单可能出现的区域(菜单在按钮下方,约 150x260 + 阴影),降低抓屏开销。
        var regionX = (int)Math.Round(120 * scale);
        var regionY = (int)Math.Round(130 * scale);
        var width = (int)Math.Round(420 * scale);
        var height = (int)Math.Round(360 * scale);

        var baseline = ScreenCapture.GrabScreenRect(origin.X + regionX, origin.Y + regionY, width, height);

        var flyout = new MenuFlyout();
        foreach (var text in Items)
        {
            flyout.Items.Add(new MenuItem { Header = text });
        }

        // 拆账用:一个没有任何模板可实例化的裸 Popup,只测"建弹窗窗口 + 首次合成"这段。
        // 必须排在预热之前(进程里第一个弹窗),否则窗口路径已被预热掉,量不到。
        // 与 menu 的差值 = 模板实例化(托管侧,AOT 能砍)那部分。
        if (Environment.GetEnvironmentVariable("ALY_MENU_PREWARM_BARE") == "1")
        {
            var bare = new Popup
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                Child = new Border { Width = 40, Height = 12, Background = Brushes.Transparent },
            };
            root.Children.Add(bare);
            var bareSw = Stopwatch.GetTimestamp();
            bare.IsOpen = true;
            bare.IsOpen = false;
            Console.WriteLine($"[menu-frames] bare popup={Stopwatch.GetElapsedTime(bareSw).TotalMilliseconds:0}ms (第一个弹窗)");
            root.Children.Remove(bare);
        }

        if (Environment.GetEnvironmentVariable("ALY_MENU_PREWARM") == "1")
        {
            // 复刻生产现场:AttachShell 里调预热时 ShellHost 还是 IsVisible=false(壳层尚未挂载),
            // 必须确认"不可见锚点"这条路径仍然把模板/建窗成本付掉了。
            Control anchor = button;
            if (Environment.GetEnvironmentVariable("ALY_MENU_PREWARM_ANCHOR") == "hidden")
            {
                anchor = new ContentControl { IsVisible = false };
                root.Children.Add(anchor);
            }

            // 走生产代码的预热(启动阶段在 AttachShell 里调的同一个方法),而不是探针自己拼一个。
            var sw = Stopwatch.GetTimestamp();
            FlyoutPrewarm.WarmMenu(anchor);
            var menuMs = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            sw = Stopwatch.GetTimestamp();
            FlyoutPrewarm.WarmFlyout(button);
            var flyoutMs = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            Console.WriteLine($"[menu-frames] prewarmed anchor={anchor.GetType().Name} menu={menuMs:0}ms " +
                              $"flyout={flyoutMs:0}ms (warmed={FlyoutPrewarm.IsMenuWarmed}/{FlyoutPrewarm.IsFlyoutWarmed})");
            await DrainAsync(200);
        }

        for (var cycle = 1; cycle <= 2; cycle++)
        {
            var frames = new List<(long Ts, ScreenCapture.Frame Frame)>();
            var stop = false;

            var grabber = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var frame = ScreenCapture.GrabScreenRect(origin.X + regionX, origin.Y + regionY, width, height);
                    frames.Add((Stopwatch.GetTimestamp(), frame));
                    Thread.Sleep(6);
                }
            })
            {
                IsBackground = true,
                Name = "MenuFramesProbe.Grabber",
            };

            grabber.Start();
            await Task.Delay(40);

            shownAtTs = Stopwatch.GetTimestamp();
            openedAtTs = 0;
            startedAtTs = 0;
            Console.WriteLine($"[menu-frames] ===== cycle {cycle} =====");

            FlyoutOpenAnimation.Started += OnStarted;
            flyout.Opened += OnOpened;
            flyout.ShowAt(button);

            await Task.Delay(650);
            Volatile.Write(ref stop, true);
            grabber.Join();

            FlyoutOpenAnimation.Started -= OnStarted;
            flyout.Opened -= OnOpened;

            Console.WriteLine($"[menu-frames] c{cycle} 抓帧 {frames.Count} 张");

            var index = 0;
            foreach (var (ts, frame) in frames)
            {
                if (frame.IsEmpty) continue;

                var box = DiffBox(baseline, frame, 12, minRowPixels: 24);
                var bottom = MenuBottomEdge(frame, x: 48, top: 40);
                if (box is null && bottom < 0) continue;

                if (index++ == 0)
                {
                    Console.WriteLine($"[menu-frames] c{cycle} FIRST at={Ms(shownAtTs, ts):0}ms " +
                                      $"opened={Rel(openedAtTs):0}ms started={Rel(startedAtTs):0}ms h={box.Value.Height} " +
                                      $"anim={startedAnimated} dx={startedDx:0} dy={startedDy:0} " +
                                      $"sinceStart={Ms(startedAtTs, ts):0}ms");
                }

                if (index % 4 == 0)
                {
                    ScreenCapture.Save(frame, $"menuframes-{cycle}-{Ms(shownAtTs, ts):000}");
                }

                Console.WriteLine(
                    $"[menu-frames] c{cycle} t={Ms(shownAtTs, ts),4:0}ms bottom={bottom,4} " +
                    $"visible={Math.Max(0, bottom - 56),4} (final=166)");
            }

            flyout.Hide();
            await DrainAsync(260);
        }

        window.Close();
        return 0;
    }

    /// <summary>在某一列上从下往上找"菜单底色"的最后一像素(= 揭示下沿)。找不到返回 -1。</summary>
    private static int MenuBottomEdge(ScreenCapture.Frame frame, int x, int top)
    {
        if (frame.IsEmpty || x < 0 || x >= frame.Width) return -1;

        for (var y = frame.Height - 1; y >= top; y--)
        {
            var offset = y * frame.Stride + x * 4;
            var b = frame.Bgra[offset];
            var g = frame.Bgra[offset + 1];
            var r = frame.Bgra[offset + 2];
            // 菜单底色 #2C2C2C(44);背景 #1F1F1F(31)。取值域放宽一点避开文字/阴影。
            if (r >= 38 && r <= 70 && g >= 38 && g <= 70 && b >= 38 && b <= 70)
            {
                return y;
            }
        }

        return -1;
    }

    /// <summary>与基线差异超过阈值的像素包围盒(设备像素);没有变化返回 null。</summary>
    private static PixelRect? DiffBox(ScreenCapture.Frame baseline, ScreenCapture.Frame frame, int threshold, int minRowPixels)
    {
        if (baseline.IsEmpty || frame.IsEmpty ||
            baseline.Width != frame.Width || baseline.Height != frame.Height)
        {
            return null;
        }

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        for (var y = 0; y < frame.Height; y++)
        {
            var row = y * frame.Stride;
            var rowCount = 0;
            var rowMinX = int.MaxValue;
            var rowMaxX = int.MinValue;

            for (var x = 0; x < frame.Width; x++)
            {
                var offset = row + x * 4;
                var db = Math.Abs(frame.Bgra[offset] - baseline.Bgra[offset]);
                var dg = Math.Abs(frame.Bgra[offset + 1] - baseline.Bgra[offset + 1]);
                var dr = Math.Abs(frame.Bgra[offset + 2] - baseline.Bgra[offset + 2]);
                if (Math.Max(db, Math.Max(dg, dr)) <= threshold) continue;

                rowCount++;
                if (x < rowMinX) rowMinX = x;
                if (x > rowMaxX) rowMaxX = x;
            }

            // 整行至少 minRowPixels 个像素变了才算菜单的一行,滤掉阴影/抗锯齿的零散像素。
            if (rowCount < minRowPixels) continue;

            if (minY == int.MaxValue) minY = y;
            maxY = y;
            if (rowMinX < minX) minX = rowMinX;
            if (rowMaxX > maxX) maxX = rowMaxX;
        }

        if (minY == int.MaxValue) return null;

        return new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static async Task DrainAsync(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(16);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
