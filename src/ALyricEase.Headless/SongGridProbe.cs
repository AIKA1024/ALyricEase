using System;
using System.Diagnostics;
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
/// SongGridView 实机探针(--sg):小屏(430px,接近手机宽度)真实控件 + 55 个 TrackRow,验证
/// 1) 完整列决定稳定高度:滚到只有一行的末列后高度仍为 6×64+20=404(不能按末列的一行重算);
/// 2) **横向分块虚拟化必须开启**:首帧只实化视口附近的列,滚动过程中按需复用容器;
/// 3) 最后一首可正常滚入视口。
///
/// ⚠ 面板必须保持 VirtualizingStackPanel。曾为"滚动时不建整列容器"改成普通 StackPanel,
///   实测得不偿失:首帧要一次性建满 55 行(布局 566ms → 712ms),而且首页/歌手页每次重建整页的
///   分配量翻倍(返回首页 23.7MB → 43.5MB,工作集从平坦变成爬到 400MB+)。详见 docs/avalonia-tips.md
///   「横向虚拟化面板不能去掉」。
/// </summary>
public static class SongGridProbe
{
    public static int Run()
    {
        const int itemCount = 55; // 9 个完整列 + 只有 1 行的末列
        var items = Enumerable.Range(1, itemCount).Select(i => new SongItemViewModel(
            new Song { Id = i, Name = $"测试歌曲{i}", DurationMs = 180_000 },
            (_, _, _) => Task.FromResult(true))).ToList();

        // 真实手机宽度下，末列进入视口时不能用其一行高度重算整个横向面板。
        var win = new Window
        {
            Width = 430,
            Height = 480,
            Content = new StackPanel { Children = { new SongGridView { ItemsSource = items } } },
        };
        var stopwatch = Stopwatch.StartNew();
        win.Show();
        Drain();
        stopwatch.Stop();

        var grid = win.GetVisualDescendants().OfType<SongGridView>().Single();
        var rows = win.GetVisualDescendants().OfType<TrackRow>().ToList();
        var initialHeight = grid.Bounds.Height;
        var hasVirtualizingPanel = grid.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any();
        Console.WriteLine($"[sg] 小屏首帧: height={initialHeight:F0}, rows={rows.Count}/{itemCount}, " +
                          $"virtualized={hasVirtualizingPanel}, rebuilds={grid.RebuildCount}, " +
                          $"layout={stopwatch.ElapsedMilliseconds}ms");

        // 滚到最右后,末列只有一行:高度必须保持完整列高度(不能被末列的一行重算),
        // 同时虚拟化仍在工作(实化行数远少于总数,容器复用而非整批新建)。
        var scroll = grid.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(scroll.Extent.Width, 0);
        Drain();
        var rowsAtEnd = win.GetVisualDescendants().OfType<TrackRow>().ToList();
        var endHeight = grid.Bounds.Height;
        var lastRealized = rowsAtEnd.Any(row =>
            row.DataContext is SongItemViewModel vm && vm.Song.Id == itemCount);
        var passed = Math.Abs(initialHeight - 404) < 1
                     && Math.Abs(endHeight - initialHeight) < 0.1
                     && hasVirtualizingPanel          // 横向分块虚拟化必须开启
                     && rows.Count < itemCount        // 首帧不得建满全部行
                     && rowsAtEnd.Count < itemCount   // 滚到末列也不得建满
                     && grid.RebuildCount == 1
                     && lastRealized;
        Console.WriteLine($"[sg] 滚到末列: height={endHeight:F0}, rows={rowsAtEnd.Count}, " +
                          $"last={lastRealized}, stable={passed}");
        win.Close();
        return passed ? 0 : 1;
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
