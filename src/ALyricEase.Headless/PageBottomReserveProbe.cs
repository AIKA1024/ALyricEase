using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>
/// 内容页底部"播放条预留"是否还在(--pagereserve)。
/// ⚠ 别和 --pagescroll 混:那个量的是**滚动条**被抬起多少,这个量的是**内容末尾留白**。
///
/// 背景:播放条是 overlay(覆盖在内容区上方、视口是全高),所以每个内容页必须在内容末尾
/// 留出 PlayerBarReserve(=120)的空白,滚到底时最后一行才抬到播放条之上。预留由
/// "内容根 margin bottom"承担(page-scroll 的 ScrollViewer.Padding 方案已废弃:padding 压缩视口,
/// 内容进不了播放条后面的亚克力区域)。
///
/// ⚠ **每个断点都要留**:预留写在内容根的本地样式里,随后被 .compact / .narrow 覆盖 ——
/// 只要某个断点把底边写小/写 0,那个尺寸下滚到底就再也空不出来。这类"漏一个断点"的回归
/// 已经发生过两次(2026-09-25 eec4b92 把各页**基础** margin 提到 120,但 .narrow 覆盖仍是旧值:
/// 推荐页 0、最近播放页 0、搜索结果页 8)。2026-10-03 用户报"个性推荐的自动留白失效了"即此。
///
/// 夹具:真实 View + 真实 ViewModel(塞足够内容让页面能滚),外面套一个与 AppShell 同语义的
/// 播放条 overlay(ZIndex=1、高 100),三个断点各建一次。滚到底后在窗口坐标系量:
///   底部空白 = 视口下沿 − 预留承载元素(内容根)的下沿
/// 它必须恰好等于 PlayerBarReserve.Bottom。
/// </summary>
public static class PageBottomReserveProbe
{
    /// <summary>与 Dimensions.PlayerBarHeight 一致(仅用于夹具里的假播放条)。</summary>
    private const double FakePlayerBarHeight = 100;

    private sealed record PageCase(string Tag, Func<(Control Page, Func<Control, Control?> FindRoot)> Build);

    public static int Run()
    {
        var want = ResolveReserve();
        Console.WriteLine($"[pagereserve] 期望预留 = PlayerBarReserve.Bottom = {want:F0}px(播放条高 {FakePlayerBarHeight:F0})");

        var cases = new[]
        {
            new PageCase("个性推荐", BuildRecommend),
            new PageCase("最近播放", BuildRecentPlayback),
            new PageCase("搜索结果", BuildSearch),
        };
        var breakpoints = new[] { ("宽屏", 1160.0, "wide"), ("中屏", 800.0, "compact"), ("小屏", 600.0, "narrow") };

        var failed = 0;
        foreach (var page in cases)
            foreach (var (bpTag, width, cls) in breakpoints)
                failed += Check(page, bpTag, width, cls, want);

        Console.WriteLine(failed == 0
            ? "[pagereserve] [PASS] 三个页面 × 三个断点滚到底都留满播放条预留"
            : $"[pagereserve] [FAIL] {failed} 处底部预留缺失");
        return failed == 0 ? 0 : 1;
    }

    private static int Check(PageCase page, string bpTag, double width, string cls, double want)
    {
        var (view, findRoot) = page.Build();
        view.Classes.Set(cls, true);

        // 与 AppShell 同语义:播放条覆盖在内容区底部(ZIndex=1、高 100),页面视口是全高
        var shell = new Grid();
        shell.Children.Add(view);
        shell.Children.Add(new Border
        {
            Height = FakePlayerBarHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x10)),
            ZIndex = 1,
        });

        var win = new Window { Width = width, Height = 760, Content = new ProbeHost { Content = shell } };
        win.Show();
        Drain();

        var tag = $"{page.Tag} {bpTag}";
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => s.Classes.Contains("page-scroll"));
        var root = scroller is null ? null : findRoot(view);
        if (scroller is null || root is null)
        {
            Console.WriteLine($"[pagereserve] [{tag}] [FAIL] 找不到页根滚动容器 / 预留承载元素");
            win.Close();
            return 1;
        }

        // 滚到底:再量"预留承载元素下沿 → 视口下沿"的空白(用户滚到底真正看到的留白)
        var maximum = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(0, maximum);
        Drain();

        var viewportBottom = scroller.TranslatePoint(new Point(0, scroller.Bounds.Height), win)!.Value.Y;
        var rootBottom = root.TranslatePoint(new Point(0, root.Bounds.Height), win)!.Value.Y;
        var gap = viewportBottom - rootBottom;

        Console.WriteLine(
            $"[pagereserve] [{tag} {cls}] 内容根 margin bottom={root.Margin.Bottom:F0} " +
            $"Extent={scroller.Extent.Height:F0} Viewport={scroller.Viewport.Height:F0} MaxOffset={maximum:F0} " +
            $"滚到底后底部空白={gap:F0}px(期望 {want:F0})");

        var failed = 0;
        if (maximum < 1)
        {
            failed++;
            Console.WriteLine($"[pagereserve] [{tag}] [FAIL] 内容没撑满视口(夹具内容太少)—— 这轮测不出预留,不能算通过");
        }
        if (Math.Abs(root.Margin.Bottom - want) > 0.5)
        {
            failed++;
            Console.WriteLine($"[pagereserve] [{tag}] [FAIL] 内容根 margin bottom={root.Margin.Bottom:F0},应为 {want:F0}");
        }
        if (gap < want - 0.5)
        {
            failed++;
            var detail = gap < FakePlayerBarHeight
                ? $"连播放条({FakePlayerBarHeight:F0}px)都没让开,滚到底最后一行被遮 {FakePlayerBarHeight - gap:F0}px"
                : $"只让开了一部分,比播放条多出的呼吸空间少了 {want - gap:F0}px";
            Console.WriteLine($"[pagereserve] [{tag}] [FAIL] 滚到底底部空白只有 {gap:F0}px(< {want:F0})—— {detail}");
        }
        else if (gap > want + 0.5)
        {
            failed++;
            Console.WriteLine($"[pagereserve] [{tag}] [FAIL] 滚到底底部空白 {gap:F0}px(> {want:F0})—— 留多了");
        }

        // 每例都出图(滚到底的现场):失败时能直接看到"最后一行压在播放条下",通过时留作人工核对
        var shot = Shot(win, $"pagereserve-{page.Tag}-{cls}");
        if (shot.Length > 0) Console.WriteLine($"[pagereserve] [{tag}] 截图(滚到底): {shot}");

        win.Close();
        return failed;
    }

    // ---- 三个页面夹具 ----

    /// <summary>个性推荐:4 个区块(首个 DailyMix 走 SongGridView,其余横向卡片行)。</summary>
    private static (Control, Func<Control, Control?>) BuildRecommend()
    {
        var vm = ServiceLocator.Get<RecommendViewModel>();
        vm.Sections.Clear();

        var songs = Enumerable.Range(1, 14).Select(i => new Song
        {
            Id = 90_000 + i,
            Name = $"每日歌曲 {i}",
            Artist = "测试歌手",
            Album = "测试专辑",
            DurationMs = 214_000,
        }).ToList();

        vm.Sections.Add(new RecommendSectionViewModel(
            "每日歌曲推荐",
            songs.Select(s => (object)new SongItemViewModel(s, _ => Task.FromResult(true))).ToList(),
            isBordered: true));
        vm.Sections.Add(new RecommendSectionViewModel("推荐歌单",
            Enumerable.Range(1, 6).Select(i => (object)new RecommendCardViewModel($"推荐歌单 {i}", "歌单副标题", playCount: 123_456)).ToList()));
        vm.Sections.Add(new RecommendSectionViewModel("热门歌曲",
            Enumerable.Range(1, 6).Select(i => (object)new RecommendCardViewModel($"热门歌曲 {i}", "测试歌手")).ToList()));
        vm.Sections.Add(new RecommendSectionViewModel("猜你喜欢",
            Enumerable.Range(1, 8).Select(i => (object)new RecommendCardViewModel($"猜你喜欢 {i}", "测试歌手")).ToList()));

        return (new RecommendView { DataContext = vm },
            view => view.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "PageContent"));
    }

    /// <summary>最近播放:歌单详情同款曲目列表(预留承载元素是 Grid#PageLayout)。</summary>
    private static (Control, Func<Control, Control?>) BuildRecentPlayback()
    {
        var vm = ServiceLocator.Get<RecentPlaybackViewModel>();
        vm.Songs.Clear();
        foreach (var row in MakeRows(30, "最近播放")) vm.Songs.Add(row);

        return (new RecentPlaybackView { DataContext = vm },
            view => view.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "PageLayout"));
    }

    /// <summary>搜索结果:结果区各分区(预留承载元素是 StackPanel.results-body)。</summary>
    private static (Control, Func<Control, Control?>) BuildSearch()
    {
        var vm = ServiceLocator.Get<SearchViewModel>();
        vm.Songs.Clear();
        foreach (var row in MakeRows(30, "搜索命中")) vm.Songs.Add(row);
        // 结果区(Grid IsVisible=ShowResults)只在 HasSearched 后才进可视化树 —— 不置位就找不到
        // 页根滚动容器。这里只翻状态位,不触发搜索(加载只在 SelectedTab 变化时发生)。
        vm.HasSearched = true;

        return (new SearchView { DataContext = vm },
            view => view.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Classes.Contains("results-body")));
    }

    private static IEnumerable<SongItemViewModel> MakeRows(int count, string titlePrefix)
        => Enumerable.Range(1, count).Select(i => new SongItemViewModel(
            new Song
            {
                Id = 80_000 + i,
                Name = $"{titlePrefix} {i}",
                Artist = "测试歌手",
                Album = "测试专辑",
                DurationMs = 214_000,
            },
            _ => Task.FromResult(true)));

    private static double ResolveReserve()
    {
        if (Application.Current!.TryFindResource("PlayerBarReserve", out var resource)
            && resource is Thickness thickness)
            return thickness.Bottom;
        Console.WriteLine("[pagereserve] [FAIL] 找不到 PlayerBarReserve 资源");
        return 120;
    }

    /// <summary>装载真实 Styles/Controls/Scrolling.axaml 的宿主(滚动条主题 / page-scroll 与真实应用一致)。</summary>
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

    private static string Shot(Window window, string name)
    {
        var w = (int)window.ClientSize.Width;
        var h = (int)window.ClientSize.Height;
        if (w <= 0 || h <= 0) return "";
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(window);
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name + ".png");
        using (var fs = System.IO.File.Create(path))
            rtb.Save(fs);
        return path;
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
