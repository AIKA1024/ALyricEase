using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class AlbumView : UserControl
{
    private readonly DetailPageScrollController _scrollController;

    public AlbumView()
    {
        InitializeComponent();
        _scrollController = new DetailPageScrollController(this, PageScroller);
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>容器 realized 时加载红心状态；封面由 Image 按可见性加载。</summary>
    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel song)
            song.EnsureLikedLoaded();
    }
}
