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
/// 歌单名(灰) / 播放 / 收藏 / 创建者:XXX(可跳创建者用户页) / ─ / 分享 / 复制链接。
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
                ServiceLocator.Get<UserProfileViewModel>().PlayPlaylistCommand.Execute(playlist);
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

        return menu;
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
