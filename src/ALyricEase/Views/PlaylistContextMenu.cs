using System.Linq;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>歌单 more 菜单(用户页窄屏行内"•••"弹出;对齐原版 UWP 歌单菜单结构):
/// 歌单名(灰) / 播放 / 收藏 / 创建者:XXX(可跳创建者用户页) / ─ / 分享 / 复制链接,
/// 尾部再按"是否本人歌单"追加 ─ / 重命名 / 删除(见 AppendManageItems)。
/// 与 SongContextMenu 同款即时构造 + 原版字体码位(E912 PlayFilled / E92B Add /
/// E910 Person / E918 Share / E902 CopyLink,docs/icon-fonts.md)。anchor 仅用于点击时找 TopLevel。</summary>
internal static class PlaylistContextMenu
{
    private static readonly FontFamily MenuIconFont =
        new("avares://ALyricEase/Assets/Fonts/FluentUISystemIcons.ttf#FluentUISystemIcons");

    public static MenuFlyout Create(Visual anchor, PlaylistItemViewModel playlist)
    {
        var menu = new MenuFlyout { ShowMode = FlyoutShowMode.Transient };
        menu.FlyoutPresenterClasses.Add("player-song-menu");

        menu.Items.Add(new MenuItem
        {
            Header = playlist.Name,
            IsEnabled = false,
            Focusable = false,
        });

        var play = MenuItemWithIcon("播放", "\uE912");
        play.Click += (_, _) =>
        {
            try
            {
                ServiceLocator.Get<PlayerViewModel>().PlayPlaylistCommand.Execute(playlist);
            }
            catch { /* 设计器/无头宿主没有应用 DI,仅保留菜单结构。 */ }
        };
        menu.Items.Add(play);

        var subscribe = MenuItemWithIcon("收藏", "\uE92B");
        subscribe.Click += async (_, _) => await SubscribeAsync(playlist);
        menu.Items.Add(subscribe);

        var creator = MenuItemWithIcon($"创建者： {playlist.CreatorName ?? "未知"}", "\uE910");
        creator.IsEnabled = playlist.CreatorId > 0;
        if (creator.IsEnabled)
        {
            var creatorId = playlist.CreatorId;
            creator.Click += (_, _) =>
            {
                try
                {
                    ServiceLocator.Get<MainViewModel>().OpenUserCommand.Execute(creatorId);
                }
                catch { /* 设计器/无头宿主没有应用 DI,仅保留菜单结构。 */ }
            };
        }
        menu.Items.Add(creator);

        menu.Items.Add(new Separator());

        var link = PlaylistLink(playlist);
        var share = MenuItemWithIcon("分享", "\uE918");
        share.Click += async (_, _) => await ShareAsync(anchor, playlist, link);
        menu.Items.Add(share);

        var copy = MenuItemWithIcon("复制链接", "\uE902");
        copy.Click += async (_, _) => await ClipboardService.TryCopyTextAsync(link);
        menu.Items.Add(copy);

        AppendManageItems(menu, playlist);

        return menu;
    }

    /// <summary>尾部追加 重命名/删除(仅"侧栏挂着的本人歌单"且非红心):侧栏分组只收本人歌单,
    /// 借此判定归属 —— 他人主页的参与创作/收藏歌单、收藏歌单页的行天然没有管理项;
    /// 红心集合("我喜欢")与侧栏右键同规,不允许重命名/删除。点击复用侧栏右键的
    /// 同一套确认弹窗命令(MainViewModel),确认回调会同步刷新侧栏与本页卡片。</summary>
    private static void AppendManageItems(MenuFlyout menu, PlaylistItemViewModel playlist)
    {
        MainViewModel? main;
        try
        {
            main = ServiceLocator.Get<MainViewModel>();
        }
        catch
        {
            return; // 设计器/无头宿主没有应用 DI,仅保留菜单结构。
        }

        var own = main.ShellNavItems.Any(n => n.Playlist is { } p
            && p.Playlist.Id == playlist.Playlist.Id
            && p.Playlist.Source == playlist.Playlist.Source);
        if (!own) return;

        try
        {
            if (ServiceLocator.Get<PlaylistViewModel>().IsLikedPlaylist(playlist.Playlist)) return;
        }
        catch
        {
            return;
        }

        menu.Items.Add(new Separator());

        var rename = new MenuItem { Header = "重命名歌单" };
        rename.Click += (_, _) => main.OpenRenamePlaylistDialogCommand.Execute(playlist);
        menu.Items.Add(rename);

        // 删除为破坏性操作:红色 + 分隔线隔开,确认弹窗由命令承担
        var delete = new MenuItem { Header = "删除歌单", Foreground = new SolidColorBrush(Color.Parse("#E74C3C")) };
        delete.Click += (_, _) => main.OpenDeletePlaylistDialogCommand.Execute(playlist);
        menu.Items.Add(delete);
    }

    private static MenuItem MenuItemWithIcon(string header, string glyph) => new()
    {
        Header = header,
        Icon = new TextBlock
        {
            Text = glyph,
            FontFamily = MenuIconFont,
            FontSize = 14,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        },
    };

    private static string PlaylistLink(PlaylistItemViewModel playlist)
        => $"https://music.163.com/playlist?id={playlist.Id}";

    private static async Task SubscribeAsync(PlaylistItemViewModel playlist)
    {
        try
        {
            await ServiceLocator.Get<NetEaseApiClient>()
                .SubscribePlaylistAsync(playlist.Id, subscribe: true).ConfigureAwait(false);
        }
        catch { /* 未登录/风控/已收藏:静默(菜单语境无错误呈现面)。 */ }
    }

    private static async Task ShareAsync(Visual anchor, PlaylistItemViewModel playlist, string link)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(anchor);
            var ownerHandle = topLevel?.TryGetPlatformHandle()?.Handle ?? 0;
            await ServiceLocator.Get<IPlatformShareService>().ShareUriAsync(
                ownerHandle, $"分享歌单：{playlist.Name}", playlist.Name, new Uri(link));
        }
        catch { /* 当前宿主不支持系统分享时静默返回,复制链接入口仍可用。 */ }
    }
}
