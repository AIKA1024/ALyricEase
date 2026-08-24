using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>侧边栏共享内容(导航列表 + 账号/设置),供宽屏内联栏和中/小屏抽屉复用。
/// 注意:这里不含汉堡按钮——汉堡只在它所属的"当前形态"出现一次
/// (宽屏内联栏顶部 / 中屏图标栏顶部 / 小屏标题栏),抽屉复用时不带它。</summary>
public partial class NavigationPaneView : UserControl
{
    public NavigationPaneView() => InitializeComponent();

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
}
