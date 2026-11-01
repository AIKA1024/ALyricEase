using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class BrowseView : UserControl
{
    public BrowseView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>卡片容器 realized 后延迟提交封面 URL，避免切页动画首帧解码拥塞
    /// （与个性推荐同一套：过渡 300ms 后再亮封面）。</summary>
    private void OnCardContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container.DataContext is BrowseCardViewModel card)
            card.PrepareCoverForTransition();
    }

    /// <summary>货架左右箭头:按视口的 80% 平移,留一点重叠 —— 读者能接上刚才看到的那张卡。
    /// 箭头在区块模板里(拿不到横向 ScrollViewer 的引用),所以向上找**第一个真的包含
    /// ScrollViewer 的祖先**:按 DataContext 找会先撞上按钮那排 StackPanel(它也继承区块 VM)。</summary>
    private void OnShelfScroll(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string direction } button) return;

        var scroller = button.GetVisualAncestors().OfType<Control>()
            .Select(ancestor => ancestor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault())
            .FirstOrDefault(candidate => candidate is not null);
        if (scroller is null) return;

        var max = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        var step = scroller.Viewport.Width * 0.8;
        var target = direction == "next" ? scroller.Offset.X + step : scroller.Offset.X - step;
        scroller.Offset = new Vector(Math.Clamp(target, 0, max), scroller.Offset.Y);
    }
}
