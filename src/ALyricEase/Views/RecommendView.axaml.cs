using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class RecommendView : UserControl
{
    public RecommendView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>容器 realized(虚拟化进入视口)时加载封面(幂等)。卡片和每日歌曲行都走这里。</summary>
    private void OnItemContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        switch (e.Container.DataContext)
        {
            case RecommendCardViewModel card:
                card.EnsureCoverLoaded();
                // 真实封面延后到切页过渡结束后再亮出(Background 优先级),避免 30 张位图首帧绘制卡死过渡
                card.PrepareCoverForTransition();
                break;
            case SongItemViewModel song:
                song.EnsureCoverLoaded();
                song.EnsureLikedLoaded();
                break;
        }
    }
}
