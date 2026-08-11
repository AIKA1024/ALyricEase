using Avalonia;
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

    /// <summary>卡片容器 realized(虚拟化进入视口)时加载封面(幂等)。</summary>
    private void OnCardContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is RecommendCardViewModel card)
            card.EnsureCoverLoaded();
    }
}
