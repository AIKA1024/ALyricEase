using Avalonia.Controls;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class RecommendView : UserControl
{
    public RecommendView()
    {
        InitializeComponent();
    }

    /// <summary>卡片容器 realized 时加载封面(幂等)。</summary>
    private void OnCardContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is RecommendCardViewModel card)
            card.EnsureCoverLoaded();
    }
}
