using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using ALyricEase.ViewModels;

namespace ALyricEase.Infrastructure.Behaviors;

/// <summary>
/// 侧栏"点击"行为,挂在导航 ListBox 上,把所有行的动作从"按下"统一延迟到"点击"(Tapped):
/// - 可折叠分组头(聚合歌单/网易云/QQ 音乐):点击开合其歌单子项;
/// - 普通导航行(每日推荐/搜索/歌单子项等):点击才选中,选中即导航(见 MainViewModel.OnSelectedNavChanged)。
/// 背景:Avalonia 12 的 ListBox 对鼠标默认"按下即选中"(触摸/笔本来就是抬起选中),所以行内动作
/// 全部提前到了按下;曾用 InputElement.IsHoldWithMouseEnabled 延迟到抬起,因"特定导航顺序下歌单
/// 子项选中样式不刷新"被关闭(见 AppShell.axaml),改由本行为实现。
/// 分工:
/// - PointerPressed(隧道,仅鼠标):吞掉交互行的按压,阻止按下选中/按压态;触摸/笔放行——
///   它们原生就是抬起选中,且滚动起手需要按下事件;
/// - Tapped:命中交互行才动作;行内"+"按钮区域除外(命令由按钮自身响应);
/// - DoubleTapped:双击窗口内的第 2/4/... 次点击,Avalonia 只发 DoubleTapped 不发 Tapped
///   (对齐 UWP,见 Gestures.cs);分组头开合在此补位,否则快速连点会隔次被吞。
/// </summary>
public sealed class NavListTapBehavior : Behavior<ListBox>
{
    protected override void OnAttached()
    {
        base.OnAttached();
        var list = AssociatedObject;
        if (list is null) return;
        list.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.TappedEvent, OnTapped, RoutingStrategies.Bubble);
        list.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
    }

    protected override void OnDetaching()
    {
        var list = AssociatedObject;
        if (list is not null)
        {
            list.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            list.RemoveHandler(InputElement.TappedEvent, OnTapped);
            list.RemoveHandler(InputElement.DoubleTappedEvent, OnDoubleTapped);
        }
        base.OnDetaching();
    }

    /// <summary>交互行按下只吞掉(不开合、不选中),动作留给 Tapped;
    /// 启用的"+"按钮放行,让它自己走按压/Click 流程(禁用时吞掉,防误触选中)。</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var list = AssociatedObject;
        if (list is null || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        if (e.Pointer.Type != PointerType.Mouse) return;

        if (FindRow(e.Source, out var addButton)?.DataContext is not NavItemViewModel item) return;
        if (item.IsToggleGroup)
        {
            if (addButton is { IsEnabled: true }) return;
        }
        else if (!item.IsItem)
        {
            return; // 纯标题行(发现/我的音乐)是禁用态,本就不会命中,防御性放行
        }
        e.Handled = true;
    }

    /// <summary>点击行:分组头开合;普通导航行选中(=导航)。行内"+"按钮区域不参与。</summary>
    private void OnTapped(object? sender, TappedEventArgs e)
    {
        var list = AssociatedObject;
        if (list is null) return;
        if (ToggleGroupAt(list, e.Source))
        {
            e.Handled = true;
            return;
        }
        var row = FindRow(e.Source, out var addButton);
        if (row?.DataContext is not NavItemViewModel item) return;
        if (addButton is not null) return;
        if (list.DataContext is not MainViewModel vm) return;

        if (item.IsItem)
        {
            if (row.Focusable) row.Focus(); // 对齐原生点击聚焦,保住键盘方向键导航
            vm.SelectedNav = item; // 与 NavigateCompact 同路径;绑定会同步 ListBox 选中态
            e.Handled = true;
        }
    }

    /// <summary>双击窗口内的偶数次点击:只补分组头开合(见类注释);导航行不补——选中是幂等的,
    /// 第二次快速点击本就无感知,保持 Tapped 单一路径。</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (AssociatedObject is { } list && ToggleGroupAt(list, e.Source))
            e.Handled = true;
    }

    /// <summary>命中可折叠分组头则开合并返回 true;行内"+"按钮区域不算(由按钮自身响应)。</summary>
    private bool ToggleGroupAt(ListBox list, object? source)
    {
        if (FindRow(source, out var addButton)?.DataContext is not NavItemViewModel item) return false;
        if (addButton is not null || !item.IsToggleGroup) return false;
        if (list.DataContext is not MainViewModel vm) return false;
        NavGroupExpandAnimator.Run(list, item, () => vm.ToggleNavGroupCommand.Execute(item.Key));
        return true;
    }

    /// <summary>从命中元素向上找所在行容器;途中先遇 Button 则记为行内"+"入口。</summary>
    private static ListBoxItem? FindRow(object? source, out Button? addButton)
    {
        addButton = null;
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Button button) addButton = button;
            else if (v is ListBoxItem row) return row;
        }
        return null;
    }
}
