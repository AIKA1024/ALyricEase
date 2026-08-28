using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// 虚拟化换行网格探针(--vgrid):验证 Avalonia 12.1.1 下
/// 外部 ScrollViewer + ItemsControl(横向 VirtualizingStackPanel,ItemTemplate 为纵向 6 行列)
/// 是否真实虚拟化(滚动后按需 realize、离屏列 recycle),并核对几何(列节距 404=400+2×2、总高 384=6×64)。
/// 结论供 SongGridView"按列分块虚拟化"重构依据。
/// </summary>
public static class VGridProbe
{
    private const double RowWidth = 400;
    private const double RowHeight = 60;
    private const double CellMargin = 2;
    private const int RowsPerColumn = 6;

    public static void Run()
    {
        // 100 项 → 17 列(16 满 + 1 尾),视口 900 宽只能完整放下 ~2 列
        var items = Enumerable.Range(0, 100).Select(i => (object)i).ToList();

        var win = new VGridWindow();
        win.Columns.ItemsSource = Chunk(items, RowsPerColumn).ToList();
        win.Show();
        Drain();

        var outer = win.Columns;
        var panel = outer.GetVisualDescendants().OfType<VirtualizingStackPanel>().Single();
        var scroll = win.Scroll;
        var rowsAtStart = CountRows(win);
        var columnsAtStart = panel.Children.Count;
        var preparedAtStart = win.RowsPrepared;

        Console.WriteLine($"[vgrid] 起始: ScrollViewer.Extent={scroll.Extent} Viewport={scroll.Viewport} " +
            $"面板期望尺寸={panel.DesiredSize}");
        Console.WriteLine($"[vgrid] 起始: 列高={outer.Bounds.Height:F0} (期望 384) " +
            $"realize 列={columnsAtStart}/17 行={rowsAtStart}/102 RowPrepared={preparedAtStart}");

        // 滚到第 9 列附近:验证离屏列 recycle、新列按需 realize(行容器总数不应涨到 102)
        scroll.Offset = new Vector(8 * (RowWidth + 2 * CellMargin), 0);
        Drain();

        Console.WriteLine($"[vgrid] 滚动后(Offset.X={scroll.Offset.X:F0}): " +
            $"realize 列={panel.Children.Count}/17 行={CountRows(win)}/102 RowPrepared={win.RowsPrepared}");

        // 回滚到起点,验证双向都能按需 realize
        scroll.Offset = new Vector(0, 0);
        Drain();
        Console.WriteLine($"[vgrid] 回滚后: realize 列={panel.Children.Count}/17 行={CountRows(win)}/102 " +
            $"RowPrepared={win.RowsPrepared} (应≥起始值+滚动增量)");

        // 几何抽查:同一列内相邻两行的相对位移,行节距应为 (404? 0) 列内为 (0,64),跨列首行应为 (404,0)
        var rows = win.GetVisualDescendants().OfType<Border>().Where(b => b.Width == RowWidth).ToList();
        if (rows.Count >= 7)
        {
            var inColumn = rows[1].TranslatePoint(new Point(0, 0), rows[0])!.Value;
            Console.WriteLine($"[vgrid] 几何: 同列相邻行节距=({inColumn.X:F0},{inColumn.Y:F0}) (期望 (0,64))");
            var nextColumnFirst = rows[6].TranslatePoint(new Point(0, 0), rows[0])!.Value;
            Console.WriteLine($"[vgrid] 几何: 第7行相对第1行=({nextColumnFirst.X:F0},{nextColumnFirst.Y:F0}) (期望 (404,0))");
        }

        win.Close();
    }

    private static int CountRows(Visual root) =>
        root.GetVisualDescendants().OfType<Border>().Count(b => b.Width == RowWidth);

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<List<object>> Chunk(IEnumerable<object> source, int size) =>
        source.Select((item, i) => (item, i))
            .GroupBy(x => x.i / size)
            .Select(g => g.Select(x => x.item).ToList());
}
