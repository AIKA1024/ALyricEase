using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>
/// 专辑卡片几何探针(--albumgrid):渲染"全部专辑"页(ArtistAlbumsPageView)与歌手页专辑横滑卡片,
/// 打印每张卡片内"封面 Border"与"标题 TextBlock"相对卡片根的实际矩形,判断二者是否重叠/间距多少。
/// 目的:排查全部专辑页封面与专辑名重叠的成因(Grid 内 Stretch+固定尺寸是否居中)。
/// </summary>
public static class AlbumGridProbe
{
    public static void Run()
    {
        var vm = ServiceLocator.Get<ArtistAlbumsPageViewModel>();
        foreach (var i in Enumerable.Range(1, 12))
            vm.Albums.Add(new AlbumCardViewModel(i, $"专辑{i}", ""));
        vm.Name = "测试歌手";

        var win = new Window { Width = 1000, Height = 700, Content = new ArtistAlbumsPageView { DataContext = vm } };
        win.Show();
        Drain();

        Dump(win, "[albumgrid] 全部专辑页");

        win.Close();
        vm.Albums.Clear();

        // 对照组:歌手页专辑横滑卡片(同为 200 宽 Grid + 封面/标题,标题 margin 220)
        var avm = ServiceLocator.Get<ArtistViewModel>();
        foreach (var i in Enumerable.Range(1, 6))
            avm.Albums.Add(new AlbumCardViewModel(i, $"专辑{i}", ""));
        avm.HasAlbums = true;
        var win2 = new Window { Width = 1000, Height = 700, Content = new ArtistView { DataContext = avm } };
        win2.Show();
        Drain();
        Dump(win2, "[albumgrid] 歌手页");
        win2.Close();
        avm.Albums.Clear();
        avm.HasAlbums = false;

        // 基准:个性推荐卡片(200 封面 + 标题/副标题),全部专辑页的间隔应对齐它
        var rvm = ServiceLocator.Get<RecommendViewModel>();
        rvm.Sections.Add(new RecommendSectionViewModel("推荐歌单",
            Enumerable.Range(1, 6).Select(i => (object)new RecommendCardViewModel($"歌单{i}", "副标题", "")).ToList()));
        var win3 = new Window { Width = 1000, Height = 700, Content = new RecommendView { DataContext = rvm } };
        win3.Show();
        Drain();
        DumpRec(win3);
        win3.Close();
        rvm.Sections.Clear();
    }

    /// <summary>个性推荐卡片:封面 Border(200 高)与标题 StackPanel 的相对位置。</summary>
    private static void DumpRec(Window win)
    {
        var cards = win.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("card") && b.IsVisible)
            .ToList();
        Console.WriteLine($"[albumgrid] 个性推荐页(基准): 卡片数={cards.Count}");
        foreach (var card in cards.Take(2))
        {
            var art = card.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("card-art"));
            // 标题=单行 14 号 SemiBold;播放量角标里的数字也是 SemiBold 但只有 11 号,别选错
            var title = card.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.FontWeight == Avalonia.Media.FontWeight.SemiBold && t.FontSize > 13);
            if (art is null || title is null) continue;
            var artY = art.TranslatePoint(new Point(0, 0), card)!.Value.Y;
            var titleY = title.TranslatePoint(new Point(0, 0), card)!.Value.Y;
            Console.WriteLine($"  card={card.Bounds.Width:F1}x{card.Bounds.Height:F1} " +
                $"art@y={artY:F1} h={art.Bounds.Height:F0} | title@y={titleY:F1} | " +
                $"art底→title顶 间距={titleY - (artY + art.Bounds.Height):F1}");
        }
    }

    private static void Dump(Window win, string tag)
    {
        var cards = win.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("album-card") && b.IsVisible)
            .ToList();
        Console.WriteLine($"{tag}: 卡片数={cards.Count}");
        foreach (var card in cards.Take(3))
        {
            var art = card.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name is null && b.Height == 200 && b.Width == 200);
            var title = card.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            var cardBox = card.Bounds;
            var artBox = art is null ? default : art.TranslatePoint(new Point(0, 0), card);
            var titleBox = title is null ? default : title.TranslatePoint(new Point(0, 0), card);
            var gap = art is not null && title is not null
                ? titleBox!.Value.Y - (artBox!.Value.Y + art.Bounds.Height)
                : double.NaN;
            var artH = art?.Bounds.Height ?? double.NaN;
            var titleH = title?.Bounds.Height ?? double.NaN;
            Console.WriteLine($"  card={cardBox.Width:F1}x{cardBox.Height:F1} " +
                $"art@y={artBox?.Y:F1} h={artH:F1} | title@y={titleBox?.Y:F1} h={titleH:F1} | " +
                $"art底→title顶 间距={gap:F1} (负=重叠)");
        }
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
