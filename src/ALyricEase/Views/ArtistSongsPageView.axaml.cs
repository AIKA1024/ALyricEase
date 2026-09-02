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

    public ArtistSongsPageView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        PageScroller.ScrollChanged += OnScrollChanged;
    }

    /// <summary>滚动接近底部(剩余不足 ~500px)时请求下一页;LoadMoreAsync 内部单飞防止重入。</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        var remaining = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (remaining < 500 && DataContext is ArtistSongsPageViewModel vm)
            _ = vm.LoadMoreAsync();
    }

    /// <summary>曲目容器 realized 时懒加载封面/红心。滚动中不立即加载,停止 150ms 后统一加载可见行。</summary>
    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is not SongItemViewModel item) return;
        _coverDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, OnCoverDebounceTick);
        _coverDebounce.Stop();
        _pendingCovers.Add(item);
        _coverDebounce.Start();
    }

    private void OnCoverDebounceTick(object? sender, EventArgs e)
    {
        _coverDebounce?.Stop();
        foreach (var item in _pendingCovers)
        {
            item.EnsureCoverLoaded();
            item.EnsureLikedLoaded();
        }
        _pendingCovers.Clear();
    }
}
