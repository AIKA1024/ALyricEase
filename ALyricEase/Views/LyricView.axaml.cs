using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>歌词视图:当前句变化时把选中项居中滚动(SelectionChanged 驱动)。
/// Avalonia 11 无 ScrollIntoView 扩展,手动算 ScrollViewer.Offset 实现居中。</summary>
public partial class LyricView : UserControl
{
    public LyricView()
    {
        InitializeComponent();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = LyricList.SelectedIndex;
        if (index < 0) return;

        if (LyricList.ContainerFromIndex(index) is not Control container) return;
        if (LyricList.FindDescendantOfType<ScrollViewer>() is not { } scroll) return;

        var itemHeight = container.Bounds.Height;
        var viewportHeight = scroll.Viewport.Height;
        if (itemHeight <= 0 || viewportHeight <= 0) return; // 布局未就绪

        // 项在内容坐标系中的顶部 = 视口内位置 + 当前偏移
        var pos = container.TranslatePoint(new Point(0, 0), scroll);
        if (pos is null) return;

        var desired = pos.Value.Y + scroll.Offset.Y + itemHeight / 2 - viewportHeight / 2;
        var maxY = Math.Max(0.0, scroll.Extent.Height - viewportHeight);
        var y = Math.Clamp(desired, 0, maxY);

        if (Math.Abs(y - scroll.Offset.Y) > 0.5)
            scroll.Offset = scroll.Offset.WithY(y);
    }
}
