using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class ArtistView : UserControl
{
    public ArtistView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>专辑/单曲卡片容器 realized 时加载封面(幂等)。</summary>
    private void OnAlbumContainerPreparing(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container?.DataContext is AlbumCardViewModel card) card.EnsureCoverLoaded();
    }
}
