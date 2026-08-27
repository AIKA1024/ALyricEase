using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>侧边栏共享内容(导航列表 + 账号/设置),供宽屏内联栏和中/小屏抽屉复用。
/// 注意:这里不含汉堡按钮——汉堡只在它所属的"当前形态"出现一次
/// (宽屏内联栏顶部 / 中屏图标栏顶部 / 小屏标题栏),抽屉复用时不带它。</summary>
public partial class NavigationPaneView : UserControl
{
    public NavigationPaneView()
    {
        InitializeComponent();
        // 分组头行已启用(IsInteractive)以获得 hover 反馈;这里在隧道阶段拦截其按压,
        // 先于 ListBox/ListBoxItem 的内部处理 → 只做开合,永不进入选中流程
        NavList.AddHandler(PointerPressedEvent, OnNavListPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!ServiceLocator.Get<PlaylistViewModel>().IsLoggedIn)
        {
            // 中/小屏抽屉内点账号且未登录:先收起抽屉再弹登录(登录框是整窗弹层,抽屉留开没意义)
            vm.CloseNavigationDrawerCommand.Execute(null);
            vm.OpenLoginDialogCommand.Execute(null);
            return;
        }
        vm.GoAccountCommand.Execute(null);
    }

    /// <summary>分组头(网易云音乐/QQ 音乐)点按展开/收起其歌单子项:坐标命中可折叠分组头的容器。
    /// 纯标题("发现/我的音乐")行保持禁用,不会命中。只响应主键。
    /// 聚合歌单的"+"按钮区域不触发开合:无论按钮启用与否都放行/吞掉,绝不落到分组开合上
    /// (启用时返回未处理让按钮自己响应命令;若未来禁用,这里吞掉避免误触发选中/开合)。</summary>
    private void OnNavListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var props = e.GetCurrentPoint(NavList).Properties;
        if (!props.IsLeftButtonPressed) return;

        foreach (var container in NavList.GetVisualDescendants().OfType<ListBoxItem>())
        {
            if (container.DataContext is not NavItemViewModel { IsToggleGroup: true } header) continue;
            var p = e.GetPosition(container);
            if (p.X < 0 || p.Y < 0 || p.X >= container.Bounds.Width || p.Y >= container.Bounds.Height) continue;
            if (header.HasAddButton && HitTestAddButton(e, container, out var addBtn))
            {
                if (addBtn is { IsEnabled: false }) e.Handled = true;
                return;
            }
            vm.ToggleNavGroupCommand.Execute(header.Key);
            e.Handled = true;
            return;
        }
    }

    /// <summary>按压点是否落在行内"+"按钮上(坐标命中;禁用按钮同样占位参与命中)。</summary>
    private static bool HitTestAddButton(PointerPressedEventArgs e, ListBoxItem container, out Button? button)
    {
        foreach (var b in container.GetVisualDescendants().OfType<Button>())
        {
            if (!b.IsVisible) continue;
            var bp = e.GetPosition(b);
            if (bp.X >= 0 && bp.Y >= 0 && bp.X <= b.Bounds.Width && bp.Y <= b.Bounds.Height)
            {
                button = b;
                return true;
            }
        }
        button = null;
        return false;
    }
}
