using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.ViewModels;

namespace ALyricEase.Controls;

/// <summary>一列歌曲（不超过 RowsPerColumn 行）。</summary>
public sealed record SongColumn(IReadOnlyList<SongItemViewModel> Rows);

/// <summary>
/// 横向滚动歌曲网格(圆角容器 + 竖向换行,RowsPerColumn 行/列,默认 6):
/// 个性推荐"每日歌曲推荐"与歌手页"热门歌曲"共用的容器控件。
/// 数据最多 60 首，按列分块后一次构造；所有列参与高度测量，避免末列不足时
/// 横向滚动导致容器高度坍缩，也避免手机滚动过程中集中构造复杂歌曲行。
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

    /// <summary>当前订阅的集合变更源(ItemsSource 实例),卸载时退订。</summary>
    private INotifyCollectionChanged? _observedSource;
    private readonly object _rebuildGate = new();
    private bool _rebuildScheduled;
    private bool _isAttached;
    private int _rebuildGeneration;

    /// <summary>上次分块的输入快照(数据源实例 + 行序列):视图被复用时(ReusablePageViewTemplate)
    /// 重挂树时数据通常没变,此时必须保留已实化的歌曲行,不能整批重建。</summary>
    private IEnumerable? _builtSource;
    private List<SongItemViewModel> _builtRows = [];
    private bool _built;

    /// <summary>性能回归探针使用：实际发生的全量分块次数。</summary>
    internal int RebuildCount { get; private set; }

    public SongGridView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ItemsSourceProperty)
        {
            if (_isAttached)
            {
                ObserveItemsSource();
                RebuildColumns();
            }
            else
            {
                // 离树时只记录属性值；容器尚不可见，提前生成会在挂树时再做一遍。
                UnobserveItemsSource();
            }
        }
        else if (e.Property == RowsPerColumnProperty && _isAttached)
        {
            RebuildColumns();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        // 歌手页/专辑页每次进入都重建视图并立即绑定,重挂时按最新数据重建一次
        ObserveItemsSource();
        RebuildColumns();
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

    /// <summary>订阅 ItemsSource 的集合变更。页级 VM 多为单例且集合实例长期不变(只 Clear+Add),
    /// 只听属性变化的话界面会停在"绑定那一刻"的快照:首位歌手空白,其后一直显示上一位歌手的歌。</summary>
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

    /// <summary>集合内容变化后按一个 Dispatcher 周期合并重建。
    /// API 分页常在一次 UI 更新中逐项 Add；立即重建会把一次追加放大成 O(n²) 枚举与布局。</summary>
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
                // 旧视觉树排队的工作不得在控件重挂后覆盖新数据。
                if (generation != _rebuildGeneration) return;
                _rebuildScheduled = false;
            }

            if (_isAttached)
                RebuildColumns();
        }, DispatcherPriority.Background);
    }

    /// <summary>ItemsSource/RowsPerColumn 变化后重建列分块(尾部不足一列的也成列)。
    /// 数据与上次分块完全一致时直接返回:复用视图重挂树会走到这里,重建等于把整页歌曲行
    /// (首页 30 行 × 每行几十个控件)重新构造一遍,正是视图复用要避免的开销。</summary>
    private void RebuildColumns()
    {
        if (Columns is null) return; // 属性早于 InitializeComponent 设置时面板尚不存在
        var rows = ItemsSource?.OfType<SongItemViewModel>().ToList() ?? [];
        if (_built && ReferenceEquals(ItemsSource, _builtSource) && SameRows(rows, _builtRows))
            return;

        _built = true;
        _builtSource = ItemsSource;
        _builtRows = rows;
        RebuildCount++;
        var perColumn = Math.Max(1, RowsPerColumn);
        Columns.ItemsSource = rows.Count == 0
            ? []
            : Enumerable.Range(0, (rows.Count + perColumn - 1) / perColumn)
                .Select(i => new SongColumn(
                    rows.GetRange(i * perColumn, Math.Min(perColumn, rows.Count - i * perColumn))))
                .ToList();
    }

    /// <summary>两次分块的行是否为同一批对象(同一 VM 实例顺序不变)。</summary>
    private static bool SameRows(List<SongItemViewModel> left, List<SongItemViewModel> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!ReferenceEquals(left[index], right[index])) return false;
        return true;
    }

    /// <summary>行容器 realized 时加载红心状态；封面由 Image 按可见树生命周期管理。</summary>
    private void OnRowContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel song)
            song.EnsureLikedLoaded();
    }
}
