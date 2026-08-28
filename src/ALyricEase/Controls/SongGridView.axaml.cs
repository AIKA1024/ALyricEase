using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using ALyricEase.ViewModels;

namespace ALyricEase.Controls;

/// <summary>一列歌曲(<= RowsPerColumn 行):横向虚拟化的分块单位。</summary>
public sealed record SongColumn(IReadOnlyList<SongItemViewModel> Rows);

/// <summary>
/// 横向滚动歌曲网格(圆角容器 + 竖向换行,RowsPerColumn 行/列,默认 6):
/// 个性推荐"每日歌曲推荐"与歌手页"热门歌曲"共用的容器控件。
/// 数据按列分块交给横向 VirtualizingStackPanel 虚拟化,行数即分块大小,
/// 卡片高度随行数自然推导(行高 60 + 上下 2px 边距),不依赖固定高度。
/// </summary>
public partial class SongGridView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SongGridView, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<int> RowsPerColumnProperty =
        AvaloniaProperty.Register<SongGridView, int>(nameof(RowsPerColumn), 6);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>每列行数(=可见行数,分块大小)。</summary>
    public int RowsPerColumn
    {
        get => GetValue(RowsPerColumnProperty);
        set => SetValue(RowsPerColumnProperty, value);
    }

    public SongGridView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ItemsSourceProperty || e.Property == RowsPerColumnProperty)
            RebuildColumns();
    }

    /// <summary>ItemsSource/RowsPerColumn 变化后重建列分块(尾部不足一列的也成列)。</summary>
    private void RebuildColumns()
    {
        if (Columns is null) return; // 属性早于 InitializeComponent 设置时面板尚不存在
        var rows = ItemsSource?.OfType<SongItemViewModel>().ToList() ?? [];
        var perColumn = Math.Max(1, RowsPerColumn);
        Columns.ItemsSource = rows.Count == 0
            ? []
            : Enumerable.Range(0, (rows.Count + perColumn - 1) / perColumn)
                .Select(i => new SongColumn(
                    rows.GetRange(i * perColumn, Math.Min(perColumn, rows.Count - i * perColumn))))
                .ToList();
    }

    /// <summary>行容器 realized 时加载封面/红心(幂等),原先分散在两个页面的处理统一到这里。
    /// 横向虚拟化下列会回收再 realize,重触发依赖 EnsureXxx 的幂等性。</summary>
    private void OnRowContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel song)
        {
            song.EnsureCoverLoaded();
            song.EnsureLikedLoaded();
        }
    }
}
