using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// page-scroll 预留带不得外溢(--pagescroll)。
///
/// 背景:内容页根 ScrollViewer 加 page-scroll 后,由 Styles/Controls/Scrolling.axaml 把
/// "它自己的纵向滚动条"抬高 PlayerBarReserve(=100),免得滑块/轨道伸到常驻播放条后面。
/// 这条样式必须写成 <c>ScrollViewer.page-scroll /template/ ScrollBar:vertical</c>。
///
/// 若写成后代式 <c>ScrollViewer.page-scroll ScrollBar</c>(空格 = 后代,不区分层级),
/// 会把页内**嵌套滚动容器**的滚动条一并命中:个性推荐里横向卡片区的水平滚动条被套上
/// 底部 100px 边距,直接被顶到容器中间(2026-09-17 实测回归,用户报告)。
///
/// 夹具与断言(自包含:宿主上真正加载 Scrolling.axaml,不依赖 HeadlessApp 的样式表):
///   页根 ScrollViewer(page-scroll) ── 纵向条 → 必须 bottom=100(抬起)
///     ├─ 嵌套横向 ScrollViewer ─────── 水平条 → 必须无外边距(原样)
///     └─ 嵌套纵向 ScrollViewer ─────── 纵向条 → 必须无外边距(原样)
/// </summary>
public static class PageScrollReserveProbe
{
    private const double Reserve = 100;

    public static void Run()
    {
        var host = new ProbeHost { Content = BuildFixture() };
        var win = new Window { Width = 800, Height = 500, Content = host };
        win.Show();
        Drain();

        var pageRoot = host.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(s => s.Classes.Contains("page-scroll"));
        if (pageRoot is null)
        {
            Console.WriteLine("[pagescroll] [FAIL] 夹具里找不到带 page-scroll 的根 ScrollViewer");
            Environment.ExitCode = 1;
            win.Close();
            return;
        }

        var bars = host.GetVisualDescendants().OfType<ScrollBar>().ToList();
        // 每个 ScrollViewer 的模板都会实例化横、纵两个 ScrollBar 部件(靠可见性取舍),
        // 所以夹具 3 个 ScrollViewer → 6 条,不能按"可见的"来数。
        Console.WriteLine($"[pagescroll] 命中滚动条 {bars.Count} 条(夹具 3 个 ScrollViewer × 横纵两个模板部件 = 6)");

        var failed = 0;
        var rootVerticalSeen = false;
        var nestedHorizontalSeen = false;

        foreach (var bar in bars)
        {
            var owner = bar.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            var isPageRoot = ReferenceEquals(owner, pageRoot);
            // 只有"页根模板里的纵向条"该被抬起;嵌套容器的横/纵条一律保持原样
            var wantInset = isPageRoot && bar.Orientation == Orientation.Vertical;
            if (wantInset) rootVerticalSeen = true;
            if (!isPageRoot && bar.Orientation == Orientation.Horizontal) nestedHorizontalSeen = true;

            var m = bar.Margin;
            var wantBottom = wantInset ? Reserve : 0;
            var ok = m.Left == 0 && m.Top == 0 && m.Right == 0 && Math.Abs(m.Bottom - wantBottom) < 0.01;
            if (!ok) failed++;

            var ownerName = isPageRoot ? "页根模板" : "嵌套容器";
            Console.WriteLine($"[pagescroll]   {bar.Orientation,-10} 归属={ownerName} " +
                $"Margin=({m.Left:F0},{m.Top:F0},{m.Right:F0},{m.Bottom:F0}) " +
                $"期望 bottom={wantBottom:F0} {(ok ? "OK" : "不符 <-- 被误伤")}");
        }

        if (!rootVerticalSeen)
        {
            failed++;
            Console.WriteLine("[pagescroll] [FAIL] 页根模板里没有纵向滚动条(夹具或 /template/ 选择器没生效)");
        }
        if (!nestedHorizontalSeen)
        {
            failed++;
            Console.WriteLine("[pagescroll] [FAIL] 夹具里没有嵌套横向滚动条,这条断言等于没测");
        }

        Console.WriteLine(failed == 0
            ? "[pagescroll] [PASS] 页根纵向条已抬起,嵌套容器的滚动条未被波及"
            : $"[pagescroll] [FAIL] {failed} 项不符 —— 样式选择器很可能又写成了后代式");
        Environment.ExitCode = failed == 0 ? 0 : 1;

        win.Close();
    }

    /// <summary>装载真实 Styles/Controls/Scrolling.axaml 的宿主。</summary>
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

    private static Control BuildFixture()
    {
        var pageContent = new StackPanel();

        // 撑高页根,保证页根纵向条真的出现
        pageContent.Children.Add(new Border { Height = 900, Background = Brushes.Gray });

        // 嵌套横向滚动容器(对应个性推荐的横向卡片区):横向溢出 → 出现水平滚动条
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 10; i++)
            row.Children.Add(new Border { Width = 200, Height = 100, Background = Brushes.SteelBlue });
        pageContent.Children.Add(new ScrollViewer
        {
            Height = 140,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = row,
        });

        // 嵌套纵向滚动容器:同样不该被抬起
        pageContent.Children.Add(new ScrollViewer
        {
            Height = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Height = 900, Background = Brushes.SeaGreen },
        });

        return new ScrollViewer
        {
            Classes = { "page-scroll" },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = pageContent,
        };
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
