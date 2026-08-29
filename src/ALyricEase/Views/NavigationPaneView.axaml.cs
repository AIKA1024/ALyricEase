using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>侧边栏共享内容(导航列表 + 账号/设置),供宽屏内联栏和中/小屏抽屉复用。
/// 注意:这里不含汉堡按钮——汉堡只在它所属的"当前形态"出现一次
/// (宽屏内联栏顶部 / 中屏图标栏顶部 / 小屏标题栏),抽屉复用时不带它。
/// 分组头"点击"开合的逻辑在 NavGroupTapToggleBehavior,挂于 XAML 中的 NavList;
/// 歌单子项右键菜单(重命名/复制链接)在 OnNavListContextRequested。</summary>
public partial class NavigationPaneView : UserControl
{
    public NavigationPaneView()
    {
        InitializeComponent();
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

    /// <summary>歌单子项右键/长按菜单:重命名 + 复制链接。在 code-behind 按命中行现建现用,
    /// 不用 XAML 静态 ContextMenu——它会对所有行共用一份实例(绑定错行),且需按歌单身份
    /// 裁剪菜单项(红心集合不允许重命名)。非歌单行不弹菜单。</summary>
    private void OnNavListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var row = (e.Source as Visual)?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not NavItemViewModel { Playlist: { } item }) return;

        var playlist = item.Playlist;
        var menu = new MenuFlyout();

        if (!ServiceLocator.Get<PlaylistViewModel>().IsLikedPlaylist(playlist))
        {
            var rename = new MenuItem { Header = "重命名歌单" };
            rename.Click += (_, _) => vm.OpenRenamePlaylistDialogCommand.Execute(item);
            menu.Items.Add(rename);
        }

        var copy = new MenuItem { Header = "复制链接" };
        copy.Click += (_, _) => _ = ClipboardService.TryCopyTextAsync(PlaylistShareLinks.For(playlist));
        menu.Items.Add(copy);

        menu.ShowAt(row, true);
        e.Handled = true;
    }
}
