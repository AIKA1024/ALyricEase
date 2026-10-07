using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ALyricEase.ViewModels;

namespace ALyricEase.Controls;

/// <summary>一行专辑卡片(<= ColumnsPerRow 张):纵向虚拟化的分块单位。</summary>
public sealed record AlbumRow(IReadOnlyList<AlbumCardViewModel> Cards);

/// <summary>
/// 纵向滚动专辑网格("查看更多"专辑页用):卡片按 ColumnsPerRow 切成行,
/// 纵向 VirtualizingStackPanel 只实例化视口附近的行;列数随控件宽度自适应
/// (212px/卡:200 卡片 + 12 右边距,至少 1 列)。分块机制与 SongGridView 同源。
/// </summary>
public partial class AlbumGrid : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AlbumGrid, IEnumerable?>(nameof(ItemsSource));

    /// <summary>当前订阅的集合变更源(ItemsSource 实例),卸载时退订。</summary>
    private INotifyCollectionChanged? _observedSource;
    private readonly object _rebuildGate = new();
    private bool _rebuildScheduled;
    private bool _isAttached;
    private int _rebuildGeneration;
    private int _columnsPerRow = 5;

    /// <summary>性能回归探针使用：实际发生的全量分块次数。</summary>
    internal int RebuildCount { get; private set; }

    public AlbumGrid()
    {
        InitializeComponent();
        SizeChanged += (_, e) => UpdateColumnsPerRow(e.NewSize.Width);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ItemsSourceProperty)
        {
            if (_isAttached) ObserveItemsSource();
            else UnobserveItemsSource();
            RebuildRows();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        // 页面单例 VM 复用,重挂时按最新数据重建一次(与 SongGridView 同理)
        UpdateColumnsPerRow(Bounds.Width);
        ObserveItemsSource();
        RebuildRows();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        lock (_rebuildGate)
        {
            _rebuildGeneration++;
            _rebuildScheduled = false;
        }
        UnobserveItemsSource();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>列数随宽度自适应;变化才重建,避免每次尺寸微调全量重排。</summary>
    private void UpdateColumnsPerRow(double width)
    {
        var per = Math.Max((int)(width / 212), 1);
        if (per == _columnsPerRow) return;
        _columnsPerRow = per;
        ScheduleRebuild();
    }

    private void ObserveItemsSource()
    {
        UnobserveItemsSource();
        if (ItemsSource is INotifyCollectionChanged ncc)
        {
            _observedSource = ncc;
            ncc.CollectionChanged += OnItemsSourceCollectionChanged;
        }
    }

    private void UnobserveItemsSource()
    {
        if (_observedSource is null) return;
        _observedSource.CollectionChanged -= OnItemsSourceCollectionChanged;
        _observedSource = null;
    }

    /// <summary>集合内容变化后按一个 Dispatcher 周期合并重建，避免逐项追加触发 O(n²) 全量枚举。</summary>
    private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        int generation;
        lock (_rebuildGate)
        {
            if (_rebuildScheduled) return;
            _rebuildScheduled = true;
            generation = _rebuildGeneration;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_rebuildGate)
            {
                if (generation != _rebuildGeneration) return;
                _rebuildScheduled = false;
            }

            if (_isAttached)
                RebuildRows();
        }, DispatcherPriority.Background);
    }

    /// <summary>ItemsSource/列数变化后重建行分块(尾部不足一行的也成行)。</summary>
    private void RebuildRows()
    {
        if (Rows is null) return; // 属性早于 InitializeComponent 设置时面板尚不存在
        RebuildCount++;
        var cards = ItemsSource?.OfType<AlbumCardViewModel>().ToList() ?? [];
        var perRow = Math.Max(1, _columnsPerRow);
        Rows.ItemsSource = cards.Count == 0
            ? []
            : Enumerable.Range(0, (cards.Count + perRow - 1) / perRow)
                .Select(i => new AlbumRow(
                    cards.GetRange(i * perRow, Math.Min(perRow, cards.Count - i * perRow))))
                .ToList();
    }
}
