using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class ArtistView : UserControl
{
    public ArtistView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.Apply(this, e.NewSize.Width);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.Apply(this, Bounds.Width);
    }

    /// <summary>热门歌曲容器 realized 时加载封面/红心(幂等)。</summary>
    private void OnTrackContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is SongItemViewModel song)
        {
            song.EnsureCoverLoaded();
            song.EnsureLikedLoaded();
        }
    }

    /// <summary>专辑/单曲卡片容器 realized 时加载封面(幂等)。</summary>
    private void OnAlbumContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is AlbumCardViewModel card) card.EnsureCoverLoaded();
    }
}
