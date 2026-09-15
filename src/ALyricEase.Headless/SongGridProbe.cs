using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Controls;
using ALyricEase.Models;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>
/// SongGridView 实机探针(--sg):真实控件 + TrackRow 行,验证
/// 1) 行数自然推导高度:总高 = RowsPerColumn × (60 行高 + 2×2 边距) + 容器边框内边距 20,不再卡固定高度;
/// 2) 横向虚拟化生效:30 项(6 行/列 → 5 列)只 realize 视口附近列的行;
/// 3) 滚动后按需 realize:在屏行数恒定,内容切到尾部列。
/// </summary>
public static class SongGridProbe
{
    public static void Run()
    {
        var items = Enumerable.Range(1, 30).Select(i => new SongItemViewModel(
            new Song { Id = i, Name = $"测试歌曲{i}", DurationMs = 180_000 },
            (_, _, _) => Task.FromResult(true))).ToList();

        // 真实用法宿主是竖向 StackPanel(歌手页/推荐页),验证自然高度而非拉伸高度
        var win = new Window
        {
            Width = 1000,
            Height = 480,
            Content = new StackPanel { Children = { new SongGridView { ItemsSource = items } } },
        };
        win.Show();
        Drain();

        var grid = win.GetVisualDescendants().OfType<SongGridView>().Single();
        var rows = win.GetVisualDescendants().OfType<TrackRow>().ToList();
        Console.WriteLine($"[sg] 30 项(5 列): SongGridView 总高={grid.Bounds.Height:F0} (期望 6×64+20=404) " +
            $"realize 行={rows.Count}/30 (期望 <30)");
        Console.WriteLine($"[sg] 起始首行: {Describe(rows.FirstOrDefault())} (期望 测试歌曲1)");

        // 滚到最右:在屏行数恒定说明离屏列已回收,行内容切到尾部
        var scroll = grid.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(scroll.Extent.Width, 0);
        Drain();
        var rowsAtEnd = win.GetVisualDescendants().OfType<TrackRow>().ToList();
        // 虚拟化 CacheLength 会保留上一批列，VisualTree 的第一项不等于最右侧首项；
        // 用当前已实化歌曲的最大 id 验证末尾列确实进入视口。
        var lastRealized = rowsAtEnd
            .Select(row => row.DataContext as SongItemViewModel)
            .Where(vm => vm is not null)
            .MaxBy(vm => vm!.Song.Id);
        Console.WriteLine($"[sg] 滚到最右: realize 行={rowsAtEnd.Count}/30 末尾已实化: " +
            $"{lastRealized?.Song.Name ?? "(无)"} (期望 测试歌曲30)");
        win.Close();
    }

    private static string Describe(TrackRow? row) =>
        row?.DataContext is SongItemViewModel vm ? vm.Song.Name ?? "?" : "(无)";

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
