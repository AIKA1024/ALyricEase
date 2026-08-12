using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class RecommendView : UserControl
{
    public RecommendView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.Apply(this, e.NewSize.Width);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.Apply(this, Bounds.Width);
    }

    /// <summary>容器 realized(虚拟化进入视口)时加载封面(幂等)。卡片和每日歌曲行都走这里。</summary>
    private void OnItemContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        switch (e.Container.DataContext)
        {
            case RecommendCardViewModel card: card.EnsureCoverLoaded(); break;
            case SongItemViewModel song:
                song.EnsureCoverLoaded();
                song.EnsureLikedLoaded();
                break;
        }
    }
}
