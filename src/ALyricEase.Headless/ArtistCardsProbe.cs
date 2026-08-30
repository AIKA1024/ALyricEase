using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>
/// 歌手卡片/推荐横向区块虚拟化探针(--acards):真实 ArtistView + RecommendView,验证横向 VirtualizingStackPanel 改造:
/// 1) 初始只实化视口及 CacheLength 缓冲内的卡片(远小于总数 → 封面懒加载生效,进页面不再一次性解码全部封面);
/// 2) 滚到最右:在屏卡片数恒定(离屏回收),内容切到尾部;Extent 含 12px 间距(总宽 = N×212);
/// 3) 卡片节距 212 = 200 卡片宽 + 12 容器右边距(hcards ContentPresenter 样式在虚拟化面板下生效)。
/// 卡片封面 URL 传空串:EnsureCoverLoaded 幂等守卫直接返回,不触网。
/// </summary>
public static class ArtistCardsProbe
{
    private const double CardWidth = 200;
    private const double CardGap = 12;
    private const int Total = 100;

    public static void Run()
    {
        ArtistViewProbe();
        RecommendViewProbe();
    }

    private static void ArtistViewProbe()
    {
        var vm = ServiceLocator.Get<ArtistViewModel>();
        foreach (var i in Enumerable.Range(1, Total))
            vm.Albums.Add(new AlbumCardViewModel(i, $"专辑{i}", ""));
        vm.HasAlbums = true;

        var win = new Window { Width = 1000, Height = 700, Content = new ArtistView { DataContext = vm } };
        win.Show();
        Drain();

        // 注意:歌手页 SongGridView(热门歌曲)内部也有横向 VSP,探针未填数据是空面板;
        // 必须选"子树里挂着专辑卡片"的那个面板,否则会拿到空面板误判 Extent。
        var panel = win.GetVisualDescendants().OfType<VirtualizingStackPanel>()
            .First(p => p.Orientation == Orientation.Horizontal
                && p.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("album-card")));
        var scroll = panel.GetVisualAncestors().OfType<ScrollViewer>().First();
        var cards = CardButtons(win, "album-card");

        Console.WriteLine($"[acards] 歌手页: 总数={Total} 起始实化={cards.Count} (期望 ≈视口212px 节距×(1+2×0.5)≈9) " +
            $"Extent={scroll.Extent.Width:F0} (期望 {Total * (CardWidth + CardGap):F0})");
        DumpPanel(panel, scroll, "[acards] 歌手页");
        CheckGeometry(win, "album-card", "[acards] 歌手页");

        scroll.Offset = new Vector(scroll.Extent.Width, 0);
        Drain();
        cards = CardButtons(win, "album-card");
        var last = cards.LastOrDefault()?.DataContext as AlbumCardViewModel;
        Console.WriteLine($"[acards] 歌手页滚到最右: 实化={cards.Count} (应≈起始值,离屏已回收) " +
            $"末卡片: {last?.Title ?? "(无)"} (期望 专辑{Total})");

        win.Close();
        vm.Albums.Clear();
        vm.HasAlbums = false;

        // 对照组:同样结构(横向 VSP + 12px 容器边距)放进裸窗口(无外层竖向 ScrollViewer、无 Padding),
        // 区分"面板行为"与"外层嵌套/滚动器设置"两个嫌疑。
        var bare = new BareCardsWindow();
        bare.Cards.ItemsSource = Enumerable.Range(1, Total)
            .Select(i => new AlbumCardViewModel(i, $"专辑{i}", "")).ToList();
        bare.Show();
        Drain();
        Console.WriteLine($"[acards] 裸窗口对照: Extent={bare.Scroll.Extent.Width:F0} (期望 21200) " +
            $"panel.Desired={bare.Panel.DesiredSize} viewport={bare.Scroll.Viewport}");
        bare.Close();
    }

    private static void RecommendViewProbe()
    {
        var vm = ServiceLocator.Get<RecommendViewModel>();
        var items = Enumerable.Range(1, Total)
            .Select(i => (object)new RecommendCardViewModel($"歌单{i}", "副标题", ""))
            .ToList();
        vm.Sections.Add(new RecommendSectionViewModel("推荐歌单", items));

        var win = new Window { Width = 1000, Height = 700, Content = new RecommendView { DataContext = vm } };
        win.Show();
        Drain();

        var panel = win.GetVisualDescendants().OfType<VirtualizingStackPanel>()
            .First(p => p.Orientation == Orientation.Horizontal
                && p.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("card")));
        var scroll = panel.GetVisualAncestors().OfType<ScrollViewer>().First();
        var cards = CardButtons(win, "card");

        Console.WriteLine($"[acards] 推荐页横向区块: 总数={Total} 起始实化={cards.Count} Extent={scroll.Extent.Width:F0} " +
            $"(期望 {Total * (CardWidth + CardGap):F0})");
        DumpPanel(panel, scroll, "[acards] 推荐页");
        CheckGeometry(win, "card", "[acards] 推荐页");

        scroll.Offset = new Vector(scroll.Extent.Width, 0);
        Drain();
        cards = CardButtons(win, "card");
        var last = cards.LastOrDefault()?.DataContext as RecommendCardViewModel;
        Console.WriteLine($"[acards] 推荐页滚到最右: 实化={cards.Count} 末卡片: {last?.Title ?? "(无)"} (期望 歌单{Total})");

        win.Close();
        vm.Sections.Clear();
    }

    /// <summary>同屏相邻卡片节距应为 212(卡片 200 + 间距 12):验证 hcards 容器边距真实参与虚拟化布局。</summary>
    private static void CheckGeometry(Visual root, string cardClass, string tag)
    {
        var cards = CardButtons(root, cardClass);
        if (cards.Count < 2) return;
        var delta = cards[1].TranslatePoint(new Point(0, 0), cards[0])!.Value;
        Console.WriteLine($"{tag} 几何: 相邻卡片节距=({delta.X:F1},{delta.Y:F1}) (期望 ({CardWidth + CardGap:F0},0))");
    }

    /// <summary>面板/滚动器内部状态:定位 Extent 不传播的层级。</summary>
    private static void DumpPanel(VirtualizingStackPanel panel, ScrollViewer scroll, string tag)
    {
        var owner = panel.GetVisualAncestors().OfType<ItemsControl>().First();
        Console.WriteLine($"{tag} 诊断: scroll.Bounds={scroll.Bounds.Width:F0}x{scroll.Bounds.Height:F0} " +
            $"viewport={scroll.Viewport} panel.Desired={panel.DesiredSize} panel.Bounds={panel.Bounds.Width:F0}x{panel.Bounds.Height:F0} " +
            $"itemsControl.Desired={owner.DesiredSize} items={owner.ItemsSource switch { System.Collections.ICollection c => c.Count, _ => -1 }}");
    }

    private static System.Collections.Generic.List<Button> CardButtons(Visual root, string cardClass) =>
        root.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains(cardClass) && b.IsVisible)
            .OrderBy(b => b.TranslatePoint(new Point(0, 0), root)?.X ?? 0)
            .ToList();

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
