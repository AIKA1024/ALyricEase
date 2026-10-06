using System.Diagnostics;
using System.IO;
using System.Reflection;
using ALyricEase.Controls;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>检查合成器实际位移，不以 Started 回调代替动画播放验证。
/// 覆盖一级、二级、二级单独重开和打开后重新布局，支持原生 Popup 与 overlay。</summary>
public static class SubMenuAnimationProbe
{
    private sealed class CapturingMenu : MenuFlyout
    {
        public MenuFlyoutPresenter Presenter { get; private set; } = null!;
        protected override Control CreatePresenter() => Presenter = (MenuFlyoutPresenter)base.CreatePresenter();
    }

    public static async Task RunAsync(bool desktop, bool hover = false)
    {
        var initialPacerDemand = UiFramePacer.Demand;
        var anchor = new Button { Content = "菜单", Width = 100 };
        var window = new Window
        {
            Width = 1000, Height = 600,
            Content = new StackPanel { Margin = new Thickness(40), Children = { anchor } },
        };
        var menu = new CapturingMenu();
        var artists = new MenuItem { Header = "表演者" };
        for (var i = 0; i < 6; i++) artists.Items.Add(new MenuItem { Header = $"歌手 {i + 1}" });
        menu.Items.Add(artists);
        menu.Items.Add(new MenuItem { Header = "专辑：测试专辑" });
        var starts = new List<Visual>();
        void OnStarted(Visual surface, double dx, double dy) => starts.Add(surface);
        FlyoutOpenAnimation.Started += OnStarted;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            menu.ShowAt(anchor);
            await CheckMovement("一级", menu.Presenter, window, desktop);
            var popup = artists.GetVisualDescendants().OfType<Popup>().Single();
            popup.ShouldUseOverlayLayer = !desktop;
            if (hover)
                await MeasureHoverAsync(artists, popup, desktop);
            else
                artists.IsSubMenuOpen = true;
            Assert(desktop != popup.IsUsingOverlayLayer, "弹窗宿主不正确");
            var surface = popup.Child as Border ?? throw new InvalidOperationException("没有子菜单表面");
            await CheckMovement("二级", surface, window, desktop, relayout: true);
            artists.IsSubMenuOpen = false;
            await Task.Delay(30);
            var previousStarts = starts.Count(x => ReferenceEquals(x, surface));
            artists.IsSubMenuOpen = true;
            await CheckMovement("二级重开", surface, window, desktop);
            Assert(starts.Count(x => ReferenceEquals(x, surface)) == previousStarts + 1,
                "二级菜单单独重开没有重播一次动画");
            Console.WriteLine($"[submenu-animation] {(desktop ? "desktop" : "overlay")} PASS");
        }
        finally
        {
            FlyoutOpenAnimation.Started -= OnStarted;
            menu.Hide();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert(UiFramePacer.Demand == initialPacerDemand, "关闭菜单后帧泵持有者没有释放");
        }
    }

    private static async Task MeasureHoverAsync(MenuItem item, Popup popup, bool desktop)
    {
        var root = TopLevel.GetTopLevel(item)!;
        // Avalonia 12 将原始输入的构造入口设为内部 API；仅测试通过反射注入，
        // 仍经过实际的命中测试和 PointerEntered 路由，而非直接打开子菜单。
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var mouse = typeof(MouseDevice).GetConstructors(instanceFlags).Single().Invoke([null]);
        var eventConstructor = typeof(RawPointerEventArgs).GetConstructors(instanceFlags)
            .Single(x => x.GetParameters()[4].ParameterType == typeof(Point));
        var inputRoot = typeof(TopLevel).GetProperty("InputRoot", instanceFlags)!.GetValue(root)!;
        var platform = typeof(TopLevel).GetProperty("PlatformImpl", instanceFlags)!.GetValue(root)!;
        var input = (Delegate)platform.GetType().GetProperty("Input", instanceFlags)!.GetValue(platform)!;
        void Move(Point point) => input.DynamicInvoke(eventConstructor.Invoke(
            [mouse, (ulong)Environment.TickCount64, inputRoot,
             RawPointerEventType.Move, point, RawInputModifiers.None]));
        Move(new Point(-20, -20));
        var clock = Stopwatch.StartNew();
        var entered = -1d;
        var opened = -1d;
        var started = -1d;
        var popupOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = popup.Child!;
        void OnStarted(Visual target, double dx, double dy)
        {
            if (ReferenceEquals(target, surface)) started = clock.Elapsed.TotalMilliseconds;
        }
        void OnEntered(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
        {
            if (ReferenceEquals(args.Source, item)) entered = clock.Elapsed.TotalMilliseconds;
        }
        void OnOpened(object? sender, EventArgs args)
        {
            opened = clock.Elapsed.TotalMilliseconds;
            popupOpened.TrySetResult();
        }
        item.PointerEnteredItem += OnEntered;
        popup.Opened += OnOpened;
        FlyoutOpenAnimation.Started += OnStarted;
        try
        {
            Move(item.TranslatePoint(new Point(16, item.Bounds.Height / 2), root)!.Value);
            // 等待阶段不轮询：Task.Delay 循环会不断唤醒 Windows 消息泵，
            // 掩盖鼠标静止时 DispatcherTimer 的延迟，改变被测行为。
            await popupOpened.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var visible = -1d;
            while (clock.ElapsedMilliseconds < 2000)
            {
                if (!desktop) AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                if (ElementComposition.GetElementVisual(surface) is { } visual)
                {
                    var server = typeof(CompositionObject).GetProperty("Server", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(visual)!;
                    if (Math.Abs(ReadY(server, "Translation")) > 0.5
                        && Convert.ToDouble(server.GetType().GetProperty("Opacity")!.GetValue(server)) > 0.01)
                    {
                        visible = clock.Elapsed.TotalMilliseconds;
                        break;
                    }
                }
                await Task.Delay(4);
            }
            Console.WriteLine($"[submenu-hover] enter={entered:F1}ms popup={opened:F1}ms " +
                              $"animation={started:F1}ms rendered-motion={visible:F1}ms");
            Assert(entered >= 0 && opened >= 0 && started >= 0 && visible >= 0,
                "悬停路径没有完整到达动画画面");
        }
        finally
        {
            item.PointerEnteredItem -= OnEntered;
            popup.Opened -= OnOpened;
            FlyoutOpenAnimation.Started -= OnStarted;
        }
    }

    private static async Task CheckMovement(string label, Visual surface, Window window, bool desktop, bool relayout = false)
    {
        var samples = new List<double>();
        var clock = Stopwatch.StartNew();
        foreach (var time in new[] { 20, 60, 120, 320 })
        {
            var delay = time - (int)clock.ElapsedMilliseconds;
            if (delay > 0) await Task.Delay(delay);
            if (relayout && time == 60)
            {
                ((Control)surface).InvalidateMeasure();
                ((Control)surface).InvalidateArrange();
            }
            Dispatcher.UIThread.RunJobs();
            if (!desktop) AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var visual = ElementComposition.GetElementVisual(surface)
                         ?? throw new InvalidOperationException("没有合成视觉");
            // 反射仅用于测试，读取实际合成帧中的 Offset + Translation，而非 UI 线程基值。
            var server = typeof(CompositionObject).GetProperty("Server", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(visual)!;
            var y = ReadY(server, "Offset") + ReadY(server, "Translation");
            samples.Add(y);
            Console.WriteLine($"[submenu-animation] {label} t={clock.ElapsedMilliseconds}ms y={y:F2}");
            if (!desktop && time == 60)
            {
                using var frame = window.GetLastRenderedFrame();
                frame?.Save(Path.Combine(Path.GetTempPath(), $"alyricease-{label}-animation.png"),
                    new PngBitmapEncoderOptions());
            }
        }
        var end = samples[^1];
        // 原生宿主的第一次 GPU 提交可能晚于 20ms；未提交的默认值不能当成画面终态。
        var movingSamples = samples.Take(samples.Count - 1).Select(y => Math.Abs(y - end))
            .Where(distance => distance > 0.5).ToList();
        Assert(movingSamples.Count >= 2, $"{label}没有持续滑入，动画可能被首次布局/合成同步覆盖");
        Assert(movingSamples[^1] < movingSamples[0], $"{label}没有朝终态移动");
    }

    private static double ReadY(object server, string property)
    {
        var value = server.GetType().GetProperty(property)!.GetValue(server)!;
        return Convert.ToDouble(value.GetType().GetProperty("Y")?.GetValue(value)
                                ?? value.GetType().GetField("Y")!.GetValue(value));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[submenu-animation] " + message);
    }
}
