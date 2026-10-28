using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>正在播放覆盖层"关闭位"跟随自身高度的回归(<c>--npoverlay</c>)。
///
/// 背景:关闭位 = 覆盖层 Bounds.Height + 8,只在构造/宿主 SizeChanged 时算一次。宿主的
/// SizeChanged 早于子树 arrange 时读到的是**旧**高度;若宿主之后尺寸不再变化(Android 首次
/// insets/测量修正后的常态),就再没人纠正 ⇒ 屏幕底部露出一条详情页顶栏(模拟器上遇到,
/// 真机时序不同没撞上)。开一次再收起会按新高度重算,所以表现为"用一会儿自己好了"。
///
/// 这里用"宿主尺寸不变、只有覆盖层所在行变高"来复刻同一条通道:
///   场景 A 布局前建控制器(高度 0 → 兜底值) → 首次 arrange 后必须落到真实高度
///   场景 B 关态下覆盖层变高(宿主不发 SizeChanged) → 关闭位必须跟着变高
/// 两个场景失败都等价于"页面的最后 高度差 那一截留在屏幕里"。</summary>
public static class NpOverlayPinProbe
{
    /// <summary>与 NowPlayingOverlayController.s_slideMargin 一致(留 8px 余量,避免边缘反锯齿露一条线)。</summary>
    private const double SlideMargin = 8;

    private const double FirstHeight = 300;
    private const double GrownHeight = 420;

    public static int Run()
    {
        var window = new Window { Width = 400, Height = 400, Background = Brushes.Black };

        // 宿主尺寸固定 400x400;覆盖层只占第一行 —— 之后只改这一行的高度,宿主自身不重排尺寸,
        // 正好是"宿主不会再发 SizeChanged"的那条路径。
        var root = new Grid { RowDefinitions = new RowDefinitions($"{FirstHeight},*") };
        var overlay = new Grid { Name = "NowPlayingOverlay", ZIndex = 100, ClipToBounds = true };
        overlay.Children.Add(new Border { Background = Brushes.DarkSlateBlue });
        Grid.SetRow(overlay, 0);
        root.Children.Add(overlay);
        window.Content = root;

        var vm = ServiceLocator.Get<MainViewModel>();

        // 场景 A:控制器在首次布局之前建立(此刻 Bounds.Height = 0,只能落兜底值)
        var controller = new NowPlayingOverlayController(overlay, vm);
        window.Show();
        Pump();

        var visual = ElementComposition.GetElementVisual(overlay);
        if (visual is null)
        {
            Console.WriteLine("[npoverlay] [SKIP] 拿不到合成视觉,本探针需要 Skia 真渲染(检查 UseHeadlessDrawing)");
            return 2;
        }

        var exit = 0;
        exit |= Check("A 首次布局后", visual, overlay);

        // 场景 B(用户实际看到的那个):行高刚改、布局还没跑,宿主这时调 UpdateClosedPosition
        // —— 此刻读到的仍是旧高度(300),按它摆好关闭位;随后布局才把覆盖层撑到 420。
        // 宿主自己尺寸没变 ⇒ 不会再通知 ⇒ 只有覆盖层自己的 SizeChanged 能纠正,
        // 否则就是"屏幕底部露出 420-308=112px 的详情页顶栏"(反复开关一次才好)。
        root.RowDefinitions[0].Height = new GridLength(GrownHeight);
        controller.UpdateClosedPosition();
        Pump();
        exit |= Check("B 宿主抢跑后", visual, overlay);

        controller.Dispose();
        Console.WriteLine(exit == 0 ? "[npoverlay] PASS" : "[npoverlay] FAIL");
        return exit;
    }

    /// <summary>关闭位必须正好落在覆盖层下沿之外(高度 + 余量),少一像素就是把页面留在屏幕里。</summary>
    private static int Check(string stage, CompositionVisual visual, Grid overlay)
    {
        var height = overlay.Bounds.Height;
        var y = visual.Translation.Y;
        var want = height + SlideMargin;
        var leak = height - y; // >0 表示还有这么多像素留在屏幕内
        var ok = height > 0 && Math.Abs(y - want) <= 1;

        Console.WriteLine($"[npoverlay] {stage}: 覆盖层高={height:F0} 关闭位Y={y:F1} " +
            $"期望={want:F0} 露出={leak:F1}px → {(ok ? "OK" : "FAIL")}");
        return ok ? 0 : 1;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Dispatcher.UIThread.RunJobs();
    }
}
