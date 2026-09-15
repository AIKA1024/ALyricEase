using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class SearchView : UserControl
{
    private ScrollViewer? _resultsScroll;

    /// <summary>当前订阅事件的 VM(防 DataContext 变更时重复订阅/泄漏)。</summary>
    private SearchViewModel? _subscribedVm;
    private bool _isVmSubscribed;

    internal bool IsViewModelSubscribed => _isVmSubscribed;

    public SearchView()
    {
        InitializeComponent();
        _resultsScroll = this.FindControl<ScrollViewer>("ResultsScroll");
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UnsubscribeFromViewModel();
        _subscribedVm = DataContext as SearchViewModel;
        if (this.IsAttachedToVisualTree()) SubscribeToViewModel();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ResponsiveClasses.ApplyByWindow(this);
        SubscribeToViewModel();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        UnsubscribeFromViewModel();

    private void SubscribeToViewModel()
    {
        if (_isVmSubscribed || _subscribedVm is not { } vm) return;
        vm.BackToTopRequested += OnBackToTop;
        vm.PropertyChanged += OnVmPropertyChanged;
        _isVmSubscribed = true;
    }

    private void UnsubscribeFromViewModel()
    {
        if (!_isVmSubscribed || _subscribedVm is not { } vm) return;
        vm.BackToTopRequested -= OnBackToTop;
        vm.PropertyChanged -= OnVmPropertyChanged;
        _isVmSubscribed = false;
    }

    private SearchViewModel? Vm => DataContext as SearchViewModel;

    /// <summary>回顶:结果页 ScrollViewer 滚回 0。</summary>
    private void OnBackToTop() => _resultsScroll?.Offset = new Avalonia.Vector(0, 0);

    /// <summary>VM 状态变化:离开结果页或发起新搜索时内容将重建,滚动位置回顶
    /// (否则从其它页返回/二次搜索会停在上次的滚动深度)。</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchViewModel.ShowResults) or nameof(SearchViewModel.IsSearching))
            _resultsScroll?.Offset = new Avalonia.Vector(0, 0);
    }

    /// <summary>结果页滚动:超过 400px 显示回顶按钮(原版为滚过头部后浮现)。</summary>
    private void OnResultsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (Vm is { } vm)
            vm.ShowBackToTop = (_resultsScroll?.Offset.Y ?? 0) > 400;
    }

    // ---- 类型 Tab 溢出(原版 NavigationView 顶栏:放不下时收进"···"按钮的 Flyout) ----

    /// <summary>上次应用的溢出布局签名(溢出标志 + 可见数 + 选中项),相同则跳过避免布局循环。</summary>
    private int _tabOverflowApplied = -1;

    private bool _tabOverflowScheduled;

    /// <summary>各 Tab 最近一次非零实测宽度:隐藏容器不再参与测量(DesiredSize 归 0),
    /// 必须用缓存宽度,否则窗口变宽后隐藏项永远算不回来。</summary>
    private double[] _tabWidthCache = Array.Empty<double>();

    private void OnTabBarHostSizeChanged(object? sender, SizeChangedEventArgs e) => ScheduleTabOverflowUpdate();

    private void OnTabContainerPrepared(object? sender, ContainerPreparedEventArgs e) => ScheduleTabOverflowUpdate();

    private void OnTabItemsLayoutUpdated(object? sender, EventArgs e) => ScheduleTabOverflowUpdate();

    private void ScheduleTabOverflowUpdate()
    {
        if (_tabOverflowScheduled) return;
        _tabOverflowScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _tabOverflowScheduled = false;
            UpdateTabOverflow();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>按容器实测宽度贪心装填:放不下的项隐藏并收进"···"按钮的 Flyout;
    /// 无溢出时整块居中,溢出时列表靠左、"···"靠右(原版 NavigationView 顶栏行为)。</summary>
    private void UpdateTabOverflow()
    {
        var vm = Vm;
        if (TabBarHost is null || TabItems is null || TabOverflowButton is null || vm is null) return;
        var tabs = vm.Tabs;
        var available = TabBarHost.Bounds.Width;
        if (tabs.Count == 0 || available <= 0 || !TabBarHost.IsVisible) return;

        // 隐藏容器 DesiredSize 为 0,用最近一次非零宽度兜底;全部为 0(容器未就绪)时放弃本轮
        if (_tabWidthCache.Length != tabs.Count)
            _tabWidthCache = new double[tabs.Count];
        var widths = new double[tabs.Count];
        var ready = false;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (TabItems.ContainerFromIndex(i) is ListBoxItem container
                && container.DesiredSize.Width > 0)
            {
                _tabWidthCache[i] = container.DesiredSize.Width;
                ready = true;
            }
            widths[i] = _tabWidthCache[i];
        }
        if (!ready) return;

        const double margin = 4;
        var moreWidth = 46.0;
        var budget = available - margin;
        var overflow = widths.Sum() > budget;
        var visible = new bool[tabs.Count];
        var visibleCount = 0;
        double used = 0;
        if (!overflow)
        {
            for (var i = 0; i < tabs.Count; i++) visible[i] = true;
            visibleCount = tabs.Count;
        }
        else
        {
            for (var i = 0; i < tabs.Count && used + widths[i] + moreWidth <= budget; i++)
            {
                visible[i] = true;
                used += widths[i];
                visibleCount++;
            }
            // 选中项被收进溢出时,让出一个可见位把选中项带回可视区(原版行为)
            var sel = vm.SelectedTabIndex;
            if (visibleCount > 0 && sel >= visibleCount && !visible[sel]
                && used - widths[visibleCount - 1] + widths[sel] + moreWidth <= budget)
            {
                visible[visibleCount - 1] = false;
                visible[sel] = true;
            }
        }

        var signature = (overflow ? 1 : 0) * 100000 + visibleCount * 100 + vm.SelectedTabIndex;
        if (signature == _tabOverflowApplied) return;
        _tabOverflowApplied = signature;

        for (var i = 0; i < tabs.Count; i++)
        {
            if (TabItems.ContainerFromIndex(i) is ListBoxItem container)
                container.IsVisible = visible[i];
        }

        TabOverflowButton.IsVisible = overflow;
        TabItems.HorizontalAlignment = overflow ? HorizontalAlignment.Left : HorizontalAlignment.Center;

        if (!overflow) return;
        var flyout = new MenuFlyout { ShowMode = FlyoutShowMode.Transient };
        for (var i = 0; i < tabs.Count; i++)
        {
            if (visible[i]) continue;
            var index = i;
            var item = new MenuItem { Header = tabs[index].Label };
            if (index == vm.SelectedTabIndex)
                item.FontWeight = FontWeight.Bold; // 当前选中项加粗标识
            item.Click += (_, _) => vm.SelectedTabIndex = index;
            flyout.Items.Add(item);
        }
        TabOverflowButton.Flyout = flyout;
    }

    /// <summary>歌曲结果行容器准备完成(DataContext 已赋值)时触发红心加载。注意用 ContainerPrepared 而非 PreparingContainer(后者时序同
    /// PlaylistView 注释:DataContext 未赋值,取不到当前项)。</summary>
    private void OnResultContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel song) song.EnsureLikedLoaded();
    }
}
