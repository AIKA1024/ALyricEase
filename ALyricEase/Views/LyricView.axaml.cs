using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>歌词视图:当前句变化时把选中项居中滚动(SelectionChanged 驱动)。
/// Avalonia 11 无 ScrollIntoView 扩展,手动算 ScrollViewer.Offset 实现居中。
/// detail 模式(播放详情页歌词面板)额外按与当前句的距离给行容器打 d1/d2 类(距离渐变)。</summary>
public partial class LyricView : UserControl
{
    /// <summary>歌词字号缩放系数(详情页字号档位 60%~150%,1 = 100%)。</summary>
    public static readonly StyledProperty<double> FontScaleProperty =
        AvaloniaProperty.Register<LyricView, double>(nameof(FontScale), 1.0);

    public double FontScale
    {
        get => GetValue(FontScaleProperty);
        set => SetValue(FontScaleProperty, value);
    }

    public LyricView()
    {
        InitializeComponent();
        // 虚拟化回收容器后类会丢,每次 realized 按当前距离重设
        LyricList.ContainerPrepared += OnContainerPrepared;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateDistanceClasses();

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

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is Control container)
            ApplyDistanceClasses(container, e.Index, LyricList.SelectedIndex);
    }

    /// <summary>当前句变化后刷新所有已物化容器的距离类。</summary>
    private void UpdateDistanceClasses()
    {
        var current = LyricList.SelectedIndex;
        for (var i = 0; i < LyricList.ItemCount; i++)
            if (LyricList.ContainerFromIndex(i) is Control container)
                ApplyDistanceClasses(container, i, current);
    }

    /// <summary>按与当前句的行距打类:±1 句 d1、±2 句 d2(仅 detail 模式的样式引用这两个类)。</summary>
    private static void ApplyDistanceClasses(Control container, int index, int current)
    {
        var distance = Math.Abs(index - current);
        container.Classes.Set("d1", distance == 1);
        container.Classes.Set("d2", distance == 2);
    }
}
