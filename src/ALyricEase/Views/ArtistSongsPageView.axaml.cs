using Avalonia.Controls;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌手全部歌曲页("热门歌曲·查看更多"):滚动近底部流式补页 + 封面懒加载节流。
/// 交互模式与歌单详情页一致(PlaylistView 同款防抖与单飞补页)。</summary>
public partial class ArtistSongsPageView : UserControl
{
    private DispatcherTimer? _coverDebounce;
    private readonly HashSet<SongItemViewModel> _pendingCovers = new();
    private readonly DetailPageScrollController _scrollController;

    public ArtistSongsPageView()
    {
        InitializeComponent();
        _scrollController = new DetailPageScrollController(this, PageScroller);
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        PageScroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>滚动接近底部(剩余不足 ~500px)时请求下一页;LoadMoreAsync 内部单飞防止重入。
    /// 守卫:内容未溢出视口(初始 0 高度/首页未填满)时不触发,避免布局期连续 ScrollChanged
    /// 把分页链一路跑到底。</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not ArtistSongsPageViewModel vm) return;
        if (!vm.HasMore || vm.IsLoadingMore) return;
        if (sv.Viewport.Height <= 0 || sv.Extent.Height <= sv.Viewport.Height) return;
        var remaining = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (remaining < 500)
            _ = vm.LoadMoreAsync();
    }

    /// <summary>曲目容器 realized 时懒加载封面/红心。滚动中不立即加载,停止 150ms 后统一加载可见行。</summary>
    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is not SongItemViewModel item) return;
        _pendingCovers.Add(item);
        // 计时已在跑则不重置:连续补页时容器密集 realize,反复重置会让封面永远轮不到加载
        if (_coverDebounce is { IsEnabled: true }) return;
        _coverDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, OnCoverDebounceTick);
        _coverDebounce.Start();
    }

    private void OnCoverDebounceTick(object? sender, EventArgs e)
    {
        _coverDebounce?.Stop();
        foreach (var item in _pendingCovers) item.EnsureLikedLoaded();
        _pendingCovers.Clear();
    }
}
