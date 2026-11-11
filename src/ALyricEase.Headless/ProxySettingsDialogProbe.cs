using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>应用网络代理弹窗的回归(--proxydialog):出三张截图(跟随系统 / 手动配置 / 最小窗口),
/// 并验证"切手动配置时卡片长高、缩回时压矮"这段尺寸过渡确实走了合成动画。
/// 只渲染视图本身,不碰状态与网络。</summary>
internal static class ProxySettingsDialogProbe
{
    public static void Run()
    {
        var failures = 0;
        var vm = ServiceLocator.Get<MainViewModel>();
        vm.Settings.OpenProxySettingsCommand.Execute(null);
        vm.Settings.ProxyModeIndex = 0;   // 不依赖本机落盘的草稿值,固定从"跟随系统"起测

        var view = new ProxySettingsDialogView { DataContext = vm };
        var window = new Window { Width = 720, Height = 620, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Render(window, "system");

        var card = view.FindControl<Border>("DialogCard")!;
        var surface = view.FindControl<Border>("DialogSurface")!;
        var content = view.FindControl<Grid>("DialogContent")!;
        var collapsed = card.Bounds.Height;

        // ---- 展开:切到手动配置,卡片长高,由背景层做合成伸展 ----
        vm.Settings.ProxyModeIndex = 2;
        vm.Settings.ProxyHost = "127.0.0.1";
        vm.Settings.ProxyPortText = "7890";
        vm.Settings.ProxyStatusText = "连接成功 · 42 ms(HTTP 200)";
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var expanded = card.Bounds.Height;
        var visual = ElementComposition.GetElementVisual(surface);
        Expect(ref failures, expanded > collapsed + 40,
            $"切手动配置后卡片应变高(实际 {collapsed:F0} → {expanded:F0})");
        Expect(ref failures, view.IsMorphing, "展开后应进入尺寸过渡");
        Expect(ref failures, visual is not null && Math.Abs(visual.CenterPoint.Y - (expanded / 2)) < 1,
            $"缩放中心应落在卡片中心(实际 {visual?.CenterPoint.Y:F1},期望 {expanded / 2:F1})");
        // 内容必须在布局跳变前就被压透明,否则旧布局会先闪一帧。
        // 读合成侧的 Opacity:UI 层那个临时值在动画启动时已经放开,读数恒为 1。
        Expect(ref failures, ReadOpacity(content) < 0.01,
            $"过渡刚开始时内容应不可见(实际 Opacity={ReadOpacity(content):F2})");

        var grow = SampleScale(surface, [40, 120, 300]);
        Console.WriteLine($"[proxydialog] 展开 {collapsed:F0} → {expanded:F0} " +
                          $"scale={string.Join(",", grow.Select(s => s.ToString("F3")))}");
        var growFloor = collapsed / expanded;
        Expect(ref failures, grow.Any(s => s > growFloor - 0.01 && s < 0.99),
            "展开过程中应出现介于起止比例之间的中间态");
        Expect(ref failures, Math.Abs(grow[^1] - 1) < 0.01, $"展开结束应回到 1(实际 {grow[^1]:F3})");
        Pump(80);
        Expect(ref failures, !view.IsMorphing, "展开结束应复位");
        Expect(ref failures, content.Opacity > 0.99, $"展开结束内容应恢复可见(实际 {content.Opacity:F2})");
        Render(window, "manual");

        // ---- 收起:反向也要有过渡,而不是直接跳回去 ----
        vm.Settings.ProxyModeIndex = 0;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var shrinkFrom = expanded / card.Bounds.Height;
        Expect(ref failures, view.IsMorphing, "收起后也应进入尺寸过渡");
        var shrink = SampleScale(surface, [40, 120, 300]);
        Console.WriteLine($"[proxydialog] 收起 {expanded:F0} → {card.Bounds.Height:F0} " +
                          $"scale={string.Join(",", shrink.Select(s => s.ToString("F3")))}");
        Expect(ref failures, shrink.Any(s => s > 1.01 && s < shrinkFrom),
            "收起过程中应出现大于 1 的中间态");
        Expect(ref failures, Math.Abs(shrink[^1] - 1) < 0.01, $"收起结束应回到 1(实际 {shrink[^1]:F3})");
        Pump(80);
        Console.WriteLine($"[proxydialog] 收起复位诊断: {view.MorphDiagnostics}");
        Expect(ref failures, !view.IsMorphing, "收起结束应复位");

        // ---- 最小窗口高度:手动配置展开(最占高度),弹窗应收缩到 MaxHeight,按钮仍可见 ----
        vm.Settings.ProxyModeIndex = 2;
        window.Width = 520;
        window.Height = 560;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Pump(360);   // 等这次过渡播完再截图,否则内容是透明的
        Expect(ref failures, card.Bounds.Height <= 560 - 40,
            $"小窗口下卡片应被 MaxHeight 收住(实际 {card.Bounds.Height:F0})");
        Render(window, "small");

        Console.WriteLine(failures == 0
            ? "[proxydialog] PASS 展开/收起均走合成伸展,复位与内容可见性正常"
            : $"[proxydialog] FAIL {failures} 项");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    /// <summary>按时间点采样背景层的实时缩放(合成侧,不是基值)。</summary>
    private static List<double> SampleScale(Border surface, int[] elapsedMarks)
    {
        var samples = new List<double>();
        var clock = Stopwatch.StartNew();
        foreach (var mark in elapsedMarks)
        {
            Pump(Math.Max(1, mark - (int)clock.ElapsedMilliseconds));
            samples.Add(ReadScale(surface));
        }
        return samples;
    }

    /// <summary>
    /// <c>CompositionVisual.Scale</c> 的 getter 读的是基值(起动画前写进去的终值),
    /// 实时值只在 server 侧,只能反射拿。
    /// </summary>
    private static double ReadScale(Border surface) => ReadServer(surface, "Scale") is Vector3D scale ? scale.Y : 1;

    private static double ReadOpacity(Control element) =>
        ReadServer(element, "Opacity") is { } value ? Convert.ToDouble(value) : 1;

    private static object? ReadServer(Control element, string property)
    {
        if (ElementComposition.GetElementVisual(element) is not { } visual) return null;
        var server = typeof(CompositionObject)
            .GetProperty("Server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(visual)!;
        return server.GetType().GetProperty(property)!.GetValue(server);
    }

    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        } while (clock.ElapsedMilliseconds < milliseconds);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Expect(ref int failures, bool condition, string message)
    {
        if (condition) return;
        failures++;
        Console.WriteLine($"[proxydialog] FAIL {message}");
    }

    private static void Render(Window window, string state)
    {
        var size = new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(Path.GetTempPath(), $"aly-proxydialog-{state}.png");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine($"[proxydialog:{state}] {path}");
    }
}
