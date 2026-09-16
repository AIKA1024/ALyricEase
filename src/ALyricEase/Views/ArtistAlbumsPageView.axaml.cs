using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌手全部专辑页("专辑·查看更多"):滚动近底部流式补页,封面由 AlbumGrid
/// 容器 realized 时懒加载。</summary>
public partial class ArtistAlbumsPageView : UserControl
{
    private readonly DetailPageScrollController _scrollController;

    public ArtistAlbumsPageView()
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
        if (DataContext is not ArtistAlbumsPageViewModel vm) return;
        if (!vm.HasMore || vm.IsLoadingMore) return;
        if (sv.Viewport.Height <= 0 || sv.Extent.Height <= sv.Viewport.Height) return;
        var remaining = sv.Extent.Height - sv.Offset.Y - sv.Viewport.Height;
        if (remaining < 500)
            _ = vm.LoadMoreAsync();
    }
}
