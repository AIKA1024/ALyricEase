using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Controls;
using ALyricEase.Models;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

/// <summary>
/// SongGridView 底部横向滚动条不得被切(sgbottom 探针)。
///
/// 背景:横向滚动条占的是 ScrollViewer 模板的**最底一行**,本来就贴着 ScrollViewer 的下边界,
/// 而 ScrollViewer 自带 ClipToBounds —— 于是任何"把它再往下推"的绘制位移(RenderTransform)
/// 都会直接越出裁剪矩形、底部被切掉。旧写法(至 2026-09-20)用 `Padding="6 6 6 12"` 预留 12px
/// 底部空间,再给横向条叠 `TranslateTransform Y=4`:实测底边被切 4px,用户看到滑块"底下一半没了"。
/// 改成底部 padding 8(= 旧的 12 减去 4)后滚动条位置一模一样,但整体落在 ScrollViewer 内。
///
/// 判定量全在布局空间量,不依赖任何坐标系约定:
///   最内层容器 = ScrollViewer 模板里的 Grid(与 ScrollViewer 同尺寸),滚动条布局下沿本来 = 容器高;
///   cut = 布局下沿 + RenderTransform.Y - 容器高,> 0 即底部被切 cut 像素。
/// 同一轮里还会把旧的 +4 位移按代码加回去做 A/B,确认这条线真能测到回归(不是空过)。
/// </summary>
public static class SongGridBottomProbe
{
    private const double OldNudge = 4;

    public static int Run()
    {
        var failed = 0;

        var items = Enumerable.Range(1, 40).Select(i => new SongItemViewModel(
            new Song { Id = i, Name = $"测试歌曲{i}", DurationMs = 180_000 },
            (_, _, _) => Task.FromResult(true))).ToList();

        var grid = new SongGridView { ItemsSource = items };
        var host = new ProbeHost { Content = grid };
        var win = new Window { Width = 900, Height = 460, Content = host };
        win.Show();
        Drain();

        var scroller = grid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var bar = scroller?.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(b => b.Orientation == Orientation.Horizontal);
        var listBorder = grid.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.ClipToBounds && b.Padding.Bottom > 0);

        if (scroller is null || bar is null || listBorder is null)
        {
            Console.WriteLine($"[sgbottom] [FAIL] 夹具没搭起来: scroller={scroller is not null} " +
                $"bar={bar is not null} border={listBorder is not null}");
            win.Close();
            return 1;
        }

        // 前提:ScrollViewer 自带 ClipToBounds 才有"硬裁";它一旦不裁,下面的推算就不成立
        if (!scroller.ClipToBounds)
        {
            failed++;
            Console.WriteLine("[sgbottom] [FAIL] ScrollViewer 没开 ClipToBounds,越界不再被裁(前提变了)");
        }

        // 不能空过:滚动条得真的有高度、有滑块
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
        if (bar.Bounds.Height <= 0 || thumb is null || thumb.Bounds.Height <= 0)
        {
            failed++;
            Console.WriteLine($"[sgbottom] [FAIL] 夹具里滚动条没实化(条高={bar.Bounds.Height:F1}),断言等于没测");
        }

        Console.WriteLine($"[sgbottom] 列表 Border: 高={listBorder.Bounds.Height:F1} Padding={listBorder.Padding} " +
            "(滚动条与下边框的距离 = padding.Bottom + 1px 边框)");

        var shots = new System.Collections.Generic.List<string>();

        // ---- 现状:修复后应当完整落在裁剪矩形内 ----
        var nowCut = Measure(bar, "现状");
        if (nowCut > 0.5)
        {
            failed++;
            Console.WriteLine($"[sgbottom] [FAIL] 横向滚动条下沿越出容器 {nowCut:F1}px —— 底部被切掉");
        }
        shots.Add(Shot(win, "songgrid-bar-fixed"));
        shots.Add(Shot(win, "songgrid-bar-fixed-3x", 3));

        // ---- A/B:把旧的 +4 绘制位移加回来,确认这条线真能测到回归 ----
        bar.RenderTransform = new TranslateTransform(0, OldNudge);
        Drain();
        var oldCut = Measure(bar, "旧写法(+4 位移)");
        if (!(oldCut >= 3.5))
        {
            failed++;
            Console.WriteLine($"[sgbottom] [FAIL] 旧的 +4 位移下越界只有 {oldCut:F1}px,探针没能复现回归(A/B 无效)");
        }
        shots.Add(Shot(win, "songgrid-bar-old-transform"));
        shots.Add(Shot(win, "songgrid-bar-old-transform-3x", 3));

        win.Close();

        Console.WriteLine(failed == 0
            ? "[sgbottom] [PASS] 横向滚动条完整落在裁剪矩形内,且 +4 绘制位移能被测出越界"
            : $"[sgbottom] [FAIL] {failed} 项");
        foreach (var p in shots)
            if (p.Length > 0) Console.WriteLine($"[sgbottom] 截图: {p}");
        return failed == 0 ? 0 : 1;
    }

    private static double Measure(ScrollBar bar, string label)
    {
        // 布局空间:父级就是 ScrollViewer 模板里的 Grid,与 ScrollViewer 同尺寸
        var stack = bar.Parent as Visual;
        var stackHeight = stack?.Bounds.Height ?? double.NaN;
        var dy = (bar.RenderTransform as TranslateTransform)?.Y ?? 0;
        var cut = bar.Bounds.Bottom + dy - stackHeight;
        var clip = bar.GetTransformedBounds()?.Clip;
        Console.WriteLine($"[sgbottom] {label}: 容器高={stackHeight:F1} 条布局下沿={bar.Bounds.Bottom:F1} " +
            $"RenderTransform.Y={dy:F1} 绘制下沿={bar.Bounds.Bottom + dy:F1} → 越界={cut:F1}px; " +
            $"Avalonia Clip 高={clip?.Height:F1}(应=条高 12 才没被切)");
        return cut;
    }

    /// <summary>装载真实 Styles/Controls/Scrolling.axaml 的宿主(滚动条主题与真实应用一致)。</summary>
    private sealed class ProbeHost : UserControl
    {
        public ProbeHost()
        {
            Styles.Add(new StyleInclude(new Uri("avares://ALyricEase/"))
            {
                Source = new Uri("avares://ALyricEase/Styles/Controls/Scrolling.axaml"),
            });
        }
    }

    /// <summary>
    /// 截图。<paramref name="scale"/> &gt; 1 时同时放大像素尺寸与 dpi(逻辑尺寸不变)⇒ 整窗按该倍数渲染,
    /// 便于放大核对滚动条底边。注意:代码里改的 RenderTransform **不会**反映到 RenderTargetBitmap 上
    /// (实测两张图逐字节相同),所以"旧写法"的对照只能靠上面的数字,不能靠图。
    /// </summary>
    private static string Shot(Window window, string name, int scale = 1)
    {
        var w = (int)window.ClientSize.Width;
        var h = (int)window.ClientSize.Height;
        if (w <= 0 || h <= 0) return "";
        using var rtb = new RenderTargetBitmap(
            new PixelSize(w * scale, h * scale), new Vector(96 * scale, 96 * scale));
        rtb.Render(window);
        var path = Path.Combine(Path.GetTempPath(), name + ".png");
        // Save(string) 在 12.1 已过时且是静默空操作(0 字节),必须走流
        using (var fs = File.Create(path))
            rtb.Save(fs);
        return path;
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
