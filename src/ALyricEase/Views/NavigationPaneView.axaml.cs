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
/// 歌单子项右键菜单(重命名/复制链接)在 OnNavListContextRequested。</summary>
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

    /// <summary>歌单子项右键/长按菜单:平台歌单=重命名 + 复制链接 + 删除;聚合歌单=重命名 + 删除
    /// (本地实体,无分享链接)。在 code-behind 按命中行现建现用,不用 XAML 静态 ContextMenu——
    /// 它会对所有行共用一份实例(绑定错行),且需按歌单身份裁剪菜单项(红心集合不允许重命名/删除)。
    /// 非歌单行不弹菜单。</summary>
    private void OnNavListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var row = (e.Source as Visual)?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not NavItemViewModel nav) return;

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
        else
        {
            return; // 非歌单行(导航项/分组头)不弹菜单
        }

        menu.ShowAt(row, true);
        e.Handled = true;
    }
}
