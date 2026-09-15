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

    /// <summary>卡片容器 realized 后延迟提交封面 URL，避免切页动画首帧解码拥塞。</summary>
    private void OnItemContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is RecommendCardViewModel card)
        {
            // 真实封面延后到切页过渡结束后再亮出(Background 优先级),避免 30 张位图首帧绘制卡死过渡
            card.PrepareCoverForTransition();
        }
    }
}
