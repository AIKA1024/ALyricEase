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
/// SongGridView 底部横向滚动条:既要画到位,又不能被切(sgbottom 探针)。
///
/// 背景:横向滚动条占的是 ScrollViewer 模板的**最底一行**(与内容 overlay,见 Scrolling.axaml 的模板),
/// 它永远钉在 ScrollViewer 盒子的下沿,而 ScrollViewer 自带 ClipToBounds ⇒ 想让它往下走只有一条路:
/// **让盒子本身变高**。踩过的两次:
///   ① 旧写法(至 d6babe1)用 `Border Padding="6 6 6 12"` + 给横向条叠 `TranslateTransform Y=4` 做绘制位移
///      —— 盒子没长高,推出去的 4px 全在裁剪矩形外,滑块底边被切 4px;
///   ② 只把外层 Border 的底部 padding 改成 8(去掉位移)后不再被切,但滚动条整体高了 4px
///      —— 用户反馈"像是把进度条往上拉了"。
/// 定稿:外层 Border padding 6/6/6/8 管"滚动条到下边框的留白",ScrollViewer 自己 `Padding="0,0,0,4"`
/// 管"把滚动条往下推 4px"(模板里 ScrollContentPresenter 的 Margin 绑的就是 Padding ⇒ 盒子长高 4px、
/// 内容区原地不动)。自然高度因此是 6×64+20=404。
///
/// 判定量全在布局空间量,不依赖任何坐标系约定:
///   最内层容器 = ScrollViewer 模板里的 Grid(与 ScrollViewer 同尺寸),滚动条布局下沿本来 = 容器高;
///   cut = 布局下沿 + RenderTransform.Y - 容器高,> 0 即底部被切 cut 像素。
/// 用户眼里的"进度条"其实是 Thumb:主题给横向滑块叠了 scaleY(0.35) ⇒ 8px 布局高只画出 2.8px 细线,
/// 所以另按**滑块自身的绘制范围**判一次(TranslatePoint 会带上自身 RenderTransform)。
/// 同一轮里还会把旧的 +4 位移按代码加回去做 A/B,确认这条线真能测到回归(不是空过)。
/// </summary>
public static class SongGridBottomProbe
{
    private const double OldNudge = 4;
    /// <summary>滑块下沿到 ScrollViewer 盒底的安全边下限:贴着裁剪线画不算过。</summary>
    private const double MinInkGap = 1;
    /// <summary>安全边上限:超过它说明滚动条没画到底(用户会读成"被往上拉了")。</summary>
    private const double MaxInkGap = 5;

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

        // ---- 自然高度场景:真实用法(RecommendView 每日推荐)是 StackPanel 里按内容高度排 ----
        // 与上面"被窗口拉伸"的夹具几何不同(ScrollViewer 的盒子是内容高还是被拉伸),这里单独量。
        var natItems = items;
        var natGrid = new SongGridView { ItemsSource = natItems };
        var natHost = new ProbeHost { Content = new StackPanel { Children = { natGrid } } };
        // 竖排 StackPanel 给子项的始终是"想要的高度" ⇒ 控件保持自然高度 404,不随窗口拉伸
        var natWin = new Window { Width = 900, Height = 460, Content = natHost };
        natWin.Show();
        Drain();
        shots.Add(Shot(natWin, "songgrid-bar-natural"));
        shots.Add(Shot(natWin, "songgrid-bar-natural-3x", 3));

        var natScroller = natGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var natBar = natScroller?.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(b => b.Orientation == Orientation.Horizontal);
        var natBorder = natGrid.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.ClipToBounds && b.Padding.Bottom > 0);
        var natThumb = natBar?.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();

        if (natScroller is null || natBar is null || natBorder is null || natThumb is null)
        {
            failed++;
            Console.WriteLine("[sgbottom] [FAIL] 自然高度夹具没搭起来");
        }
        else
        {
            var barBottomInBorder = natBar.TranslatePoint(new Point(0, natBar.Bounds.Height), natBorder)!.Value.Y;
            var barTopInBorder = natBar.TranslatePoint(new Point(0, 0), natBorder)!.Value.Y;
            var content = natGrid.GetVisualDescendants().OfType<ItemsControl>()
                .FirstOrDefault(c => c.Name == "Columns");
            var box = natScroller.Bounds.Height;

            // 滑块(用户眼里的"进度条")实际画在哪、有没有被切:TranslatePoint 会带上自身 RenderTransform
            // (主题里横向滑块是 scaleY(0.35) translateY(-2px)),所以两个角点就能给出它的绘制范围。
            var inkTop = natThumb.TranslatePoint(new Point(0, 0), natScroller)!.Value.Y;
            var inkBottom = natThumb.TranslatePoint(
                new Point(0, natThumb.Bounds.Height), natScroller)!.Value.Y;
            var inkCut = inkBottom - box;

            Console.WriteLine(
                $"[sgbottom] 自然高度: 控件={natGrid.Bounds.Height:F1} Border={natBorder.Bounds.Height:F1} " +
                $"ScrollViewer={natScroller.Bounds.Height:F1} 内容={content?.Bounds.Height:F1} " +
                $"条高={natBar.Bounds.Height:F1} 条在Border内 top={barTopInBorder:F1}..{barBottomInBorder:F1} " +
                $"条下沿→Border外底={natBorder.Bounds.Height - barBottomInBorder:F1}");
            Console.WriteLine(
                $"[sgbottom] 自然高度·滑块: 画在 {inkTop:F1}..{inkBottom:F1}(高 {inkBottom - inkTop:F2}," +
                $"布局高 {natThumb.Bounds.Height:F1}) 条内偏移 top={natThumb.TranslatePoint(new Point(0, 0), natBar)!.Value.Y:F1} " +
                $"越出 ScrollViewer 盒底={inkCut:F2}px");

            // 不只要「没被切」,还要「够靠下」:只改外层 Border 的 padding 虽然也不会被切,
            // 但会让滑块悬在半空(2026-09-20 用户反馈的就是这种「像把滚动条往上拉了」),所以两头都断言:
            // 安全边 ∈ [MinInkGap, MaxInkGap] —— 小于下限=贴着裁剪线画(危险),大于上限=没画到底。
            var inkGap = -inkCut;
            if (inkCut > 0.5)
            {
                failed++;
                Console.WriteLine($"[sgbottom] [FAIL] 自然高度下滑块被切 {inkCut:F2}px");
            }
            else if (inkGap < MinInkGap)
            {
                failed++;
                Console.WriteLine($"[sgbottom] [FAIL] 滑块下沿离 ScrollViewer 盒底只有 {inkGap:F2}px" +
                    $"(< {MinInkGap})—— 贴着裁剪线画,再偏一点就被切");
            }
            else if (inkGap > MaxInkGap)
            {
                failed++;
                Console.WriteLine($"[sgbottom] [FAIL] 滑块离 ScrollViewer 盒底还有 {inkGap:F2}px" +
                    $"(> {MaxInkGap})—— 没画到底,会像是「把滚动条往上拉了」");
            }
        }

        natWin.Close();

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
    /// 便于放大核对滚动条底边。代码里改的 RenderTransform 会反映到 RenderTargetBitmap 上(A/B 两张图的
    /// 滑块确实差 4px,逐像素比过),但一张图只有几像素的差别,**判据仍然以数字为准**。
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
