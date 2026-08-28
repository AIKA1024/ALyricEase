using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace ALyricEase.Infrastructure.Behaviors;

/// <summary>
/// 导航行"只允许鼠标左键":挂在侧栏导航 ListBox 上,吞掉落在行上的非主键(右键/中键/X 键)按下。
/// 背景:Avalonia 的 SelectingItemsControl 在指针按下时会对行做选中,右键按下的分支是
/// "未选中则选中"(为右键菜单预选,见 SelectingItemsControl.UpdateSelection 的 rightButton 参数);
/// 侧栏是"选中即导航"(MainViewModel.OnSelectedNavChanged / NavigateCompactCommand),
/// 于是右键也会跳页。行上的按下还会顺带把焦点移到该行,同样不该由右键触发。
/// 只处理鼠标指针:触摸/笔本来就是抬起才选中,且滚动起手需要按下事件,一律放行;
/// 键盘(方向键/空格)不走指针事件,不受影响。
/// 与 NavListTapBehavior 的关系:本行为只做"按键过滤",开合/导航的时机仍由它决定。
/// </summary>
public sealed class NavListPrimaryButtonBehavior : Behavior<ListBox>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        // 隧道:在行(ListBoxItem)和 ListBox 自身的处理器之前跑,才能压下选中与聚焦
        AssociatedObject?.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnDetaching()
    {
        AssociatedObject?.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        base.OnDetaching();
    }

    /// <summary>非主键按在行上 → 标记已处理(不选中、不聚焦、不导航);行外(滚动条/空白)不拦。</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var list = AssociatedObject;
        if (list is null || e.Pointer.Type != PointerType.Mouse) return;

        var props = e.GetCurrentPoint(list).Properties;
        var nonPrimary = props.IsRightButtonPressed
                         || props.IsMiddleButtonPressed
                         || props.IsXButton1Pressed
                         || props.IsXButton2Pressed;
        if (!nonPrimary) return; // 左键走原流程(开合/选中/导航)

        if (IsOnRow(e.Source)) e.Handled = true;
    }

    /// <summary>命中元素在某行容器内返回 true;向上越过 ListBox 还没碰到行则算行外。</summary>
    private static bool IsOnRow(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is ListBoxItem) return true;
            if (v is ListBox) return false;
        }
        return false;
    }
}
