using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌手全部专辑页("专辑·查看更多"):滚动近底部流式补页,封面由 AlbumGrid
/// 容器 realized 时懒加载。</summary>
public partial class ArtistAlbumsPageView : UserControl
{
    public ArtistAlbumsPageView()
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
        if (remaining < 500 && DataContext is ArtistAlbumsPageViewModel vm)
            _ = vm.LoadMoreAsync();
    }
}
