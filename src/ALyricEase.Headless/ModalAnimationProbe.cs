using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>模态弹层动效回归(--modalanim):按 Fluent 2 的 ContentDialog 曲线,
/// 打开时卡片 scale 1.05 → 1(250ms)、关闭时 1 → 1.05(167ms),淡入淡出 83ms 线性。
///
/// 要守住的不变量:
/// ① 卡片能被按约定的 DialogCard 名称找到(找不到会退化成纯淡入淡出,观感差但不报错);
/// ② 打开首帧确实从 1.05 起跳(不是直接 1,否则等于没有缩放);
/// ③ 动画播完后 scale 收敛到 1(不是弹回 1.05 —— FillMode=None + 基值写回的问题);
/// ④ 关闭播完后折叠且 scale 复位成 1(下次打开不会带着 1.05 的基值)。</summary>
internal static class ModalAnimationProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var failures = 0;

        var vm = ServiceLocator.Get<MainViewModel>();
        vm.Settings.OpenProxySettingsCommand.Execute(null);
        var view = new ProxySettingsDialogView { DataContext = vm };
        var window = new Window { Width = 640, Height = 560, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var card = view.FindControl<Border>("DialogCard");
        Expect(ref failures, card is not null, "卡片应能按 DialogCard 名称找到");
        if (card is null)
        {
            Console.WriteLine("[modalanim] FAIL: 找不到 DialogCard,后续断言跳过");
            return 1;
        }

        // ---- 打开 ----
        ModalVisibilityTransition.SetIsOpen(view, true);
        Dispatcher.UIThread.RunJobs();

        var transition = ModalVisibilityTransition.WaitForTransitionAsync(view);
        var startScale = ScaleOf(card);
        Expect(ref failures, Math.Abs(startScale - 1.05) < 0.001,
            $"打开首帧应停在 1.05,实际 {startScale:F4}");
        Expect(ref failures, view.IsVisible, "打开后应可见");

        await Task.Delay(120);
        if (transition.IsFaulted) Console.Error.WriteLine($"[modalanim][DIAG] 动画任务异常: {transition.Exception}");
        var midScale = ScaleOf(card);
        Expect(ref failures, midScale is > 1.0 and < 1.05,
            $"120ms 处应处于 1.0~1.05 之间(动画进行中),实际 {midScale:F4}");

        await Task.Delay(320);
        var endScale = ScaleOf(card);
        Expect(ref failures, Math.Abs(endScale - 1.0) < 0.001,
            $"播完后应收敛到 1,实际 {endScale:F4}");
        Expect(ref failures, Math.Abs(view.Opacity - 1.0) < 0.001,
            $"播完后不透明度应为 1,实际 {view.Opacity:F3}");

        // ---- 关闭 ----
        ModalVisibilityTransition.SetIsOpen(view, false);
        Dispatcher.UIThread.RunJobs();

        // 退场采样:放大只有 5%,而淡出 83ms 就把不透明度打到 0 —— 必须看清
        // "前 83ms 到底放大了多少",否则用户就是"看不见缩放"。
        await Task.Delay(40);
        var scale40 = ScaleOf(card);
        var opacity40 = view.Opacity;
        await Task.Delay(40);
        var scale80 = ScaleOf(card);
        var opacity80 = view.Opacity;
        Console.WriteLine($"[modalanim] 退场 40ms: scale={scale40:F4} ({(scale40 - 1) * 100:F2}%) opacity={opacity40:F3}");
        Console.WriteLine($"[modalanim] 退场 80ms: scale={scale80:F4} ({(scale80 - 1) * 100:F2}%) opacity={opacity80:F3}");
        // 0.13%/40ms 那种幅度肉眼等于没有(此时已剩 43% 不透明);要求淡出结束前至少放大 2%。
        Expect(ref failures, scale40 >= 1.02,
            $"退场 40ms 时放大幅度应 ≥2%(看得见),实际 {(scale40 - 1) * 100:F2}%");

        await Task.Delay(400);

        Expect(ref failures, !view.IsVisible, "关闭播完后应折叠");
        Expect(ref failures, Math.Abs(ScaleOf(card) - 1.0) < 0.001,
            $"关闭后 scale 应复位成 1,实际 {ScaleOf(card):F4}");

        Console.WriteLine(failures == 0
            ? "[modalanim] PASS 首帧 1.05 起跳、中途递减、收敛到 1、关闭后折叠并复位"
            : $"[modalanim] FAIL: {failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>读卡片的实际缩放:RenderTransform 是 TransformOperations,取矩阵 M11 最稳。</summary>
    private static double ScaleOf(Control card) =>
        card.RenderTransform?.Value.M11 ?? double.NaN;

    private static void Expect(ref int failures, bool condition, string message)
    {
        if (condition) return;
        failures++;
        Console.Error.WriteLine($"[modalanim][FAIL] {message}");
    }
}
