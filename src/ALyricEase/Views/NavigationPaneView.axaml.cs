using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>侧边栏共享内容(导航列表 + 账号/设置),供宽屏内联栏和中/小屏抽屉复用。
/// 注意:这里不含汉堡按钮——汉堡只在它所属的"当前形态"出现一次
/// (宽屏内联栏顶部 / 中屏图标栏顶部 / 小屏标题栏),抽屉复用时不带它。
/// 分组头"点击"开合的逻辑在 NavGroupTapToggleBehavior,挂于 XAML 中的 NavList;
/// 歌单子项右键菜单(平台/聚合/本地音乐三类,重命名/复制链接/设置/删除)在 OnNavListContextRequested。</summary>
public partial class NavigationPaneView : UserControl
{
    public NavigationPaneView()
    {
        InitializeComponent();
    }

    /// <summary>点"账号"一律进账号页:未登录的平台在页内以占位卡片呈现,卡片上就有该平台的登录按钮。
    /// (此前未登录时直接弹登录框,结果一个平台登录后进账号页反而没有第二个平台的入口。)</summary>
    private void OnAccountClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.GoAccountCommand.Execute(null);
    }

    /// <summary>歌单子项右键/长按入口:按命中的行取菜单,贴在行上弹出。菜单结构见 BuildContextMenu。</summary>
    private void OnNavListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var row = (e.Source as Visual)?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not NavItemViewModel nav) return;
        var menu = BuildContextMenu(nav, vm);
        if (menu is null) return; // 非歌单行(导航项/分组头)不弹菜单
        menu.ShowAt(row, true);
        e.Handled = true;
    }

    /// <summary>按行身份现建菜单:平台歌单=重命名 + 复制链接 + 删除;聚合歌单=重命名 +
    /// 选择成员歌单 + 删除;本地音乐歌单=重命名 + 歌单设置 + 删除(本地实体,无分享链接可复制)。
    /// 三类首项一律是"重命名歌单",本地歌单的"歌单设置"排第二,避免同一个右键入口
    /// 在不同分组里第一项含义不同。
    /// ⚠ 三类分别读 NavItemViewModel.Playlist / Aggregate / LocalPlaylist —— 漏掉任何一类都会
    /// 返回 null,菜单连实例都不创建(表现为右键毫无反应且无异常)。
    /// 不用 XAML 静态 ContextMenu:它会对所有行共用一份实例(绑定错行),且需按歌单身份裁剪菜单项
    /// (红心集合不允许重命名/删除)。构造与 ShowAt 分离是为了让无头探针能直接验分支与命令接线。</summary>
    internal static MenuFlyout? BuildContextMenu(NavItemViewModel nav, MainViewModel vm)
    {
        var menu = new MenuFlyout { ShowMode = FlyoutShowMode.Transient };

        if (nav.Playlist is { } item)
        {
            var playlist = item.Playlist;

            // 红心集合("我喜欢")不可重命名/删除:只留复制链接
            var isLiked = ServiceLocator.Get<PlaylistViewModel>().IsLikedPlaylist(playlist);

            if (!isLiked)
            {
                var rename = new MenuItem { Header = "重命名歌单" };
                rename.Click += (_, _) => vm.OpenRenamePlaylistDialogCommand.Execute(item);
                menu.Items.Add(rename);
            }

            var copy = new MenuItem { Header = "复制链接" };
            copy.Click += (_, _) => _ = ClipboardService.TryCopyTextAsync(PlaylistShareLinks.For(playlist));
            menu.Items.Add(copy);

            // 删除为破坏性操作:红色 + 分隔线隔开,且必须经确认弹窗;红心集合不出现
            if (!isLiked)
            {
                menu.Items.Add(new Separator());
                var delete = new MenuItem { Header = "删除歌单", Foreground = new SolidColorBrush(Color.Parse("#E74C3C")) };
                delete.Click += (_, _) => vm.OpenDeletePlaylistDialogCommand.Execute(item);
                menu.Items.Add(delete);
            }
        }
        else if (nav.Aggregate is { } aggregate)
        {
            var rename = new MenuItem { Header = "重命名歌单" };
            rename.Click += (_, _) => vm.OpenRenameAggregateDialogCommand.Execute(aggregate);
            menu.Items.Add(rename);

            // 重选聚合的成员歌单(添加弹窗的编辑模式,预勾现有成员)
            var edit = new MenuItem { Header = "选择成员歌单" };
            edit.Click += (_, _) => vm.OpenEditAggregateDialogCommand.Execute(aggregate);
            menu.Items.Add(edit);

            // 删除仅移除聚合入口(成员歌单不受影响),同样红色 + 确认弹窗
            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "删除歌单", Foreground = new SolidColorBrush(Color.Parse("#E74C3C")) };
            delete.Click += (_, _) => vm.OpenDeleteAggregateDialogCommand.Execute(aggregate);
            menu.Items.Add(delete);
        }
        else if (nav.LocalPlaylist is { } local)
        {
            // 本地音乐歌单:纯本地实体,PlaylistShareLinks 只拼两家平台网址 ⇒ 不给"复制链接"。
            // 首项与平台/聚合歌单一样是"重命名歌单";"歌单设置"排第二(复用详情页齿轮的封面弹窗)。
            var rename = new MenuItem { Header = "重命名歌单" };
            rename.Click += (_, _) => vm.OpenRenameLocalPlaylistDialogCommand.Execute(local);
            menu.Items.Add(rename);

            var settings = new MenuItem { Header = "歌单设置" };
            settings.Click += (_, _) => vm.OpenLocalPlaylistSettingsCommand.Execute(local);
            menu.Items.Add(settings);

            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "删除歌单", Foreground = new SolidColorBrush(Color.Parse("#E74C3C")) };
            delete.Click += (_, _) => vm.OpenDeleteLocalPlaylistDialogCommand.Execute(local);
            menu.Items.Add(delete);
        }
        else
        {
            return null; // 非歌单行(导航项/分组头)不弹菜单
        }

        return menu;
    }
}
