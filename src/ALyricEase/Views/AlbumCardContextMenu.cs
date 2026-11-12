using System.Collections.Generic;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ALyricEase.Views;

/// <summary>浏览页「关注新专」专辑卡的 ••• / 右键 / 长按菜单，结构对齐歌单菜单
/// (原版 UWP 的"播放 / 属性 / 分享"三段式):
/// 专辑名(灰) / 播放（N 首）/ 打开专辑页 / 表演者：XXX / ─ / 分享 / 复制链接。
///
/// <para>歌单菜单里的"创建者"在专辑语境对应"表演者",复用 <see cref="SongContextMenu.AddArtistItem"/>
/// (多歌手自动变子菜单)。N = 卡片实际入队的曲目数(全量优先),所以不写"整张专辑",
/// 以免全量拉取失败、回落采样时说错。没有公开链接(缺 mid/id)时整段分享项省略。</para>
///
/// <para>与 SongContextMenu、PlaylistContextMenu 同款即时构造 + 原版字体码位
/// (E912 PlayFilled / E922 Album / E921 单人 / E920 多人 / E918 Share / E902 CopyLink)。
/// anchor 仅用于 ShowAt 定位和分享时找 TopLevel;点击动作全部走卡片自己的委托。</para>
internal static class AlbumCardContextMenu
{
    private static readonly FontFamily MenuIconFont =
        new("avares://ALyricEase/Assets/Fonts/FluentUISystemIcons.ttf#FluentUISystemIcons");

    public static MenuFlyout Create(Visual anchor, BrowseCardViewModel card, IReadOnlyList<Song> songs)
    {
        var menu = new MenuFlyout { ShowMode = FlyoutShowMode.Transient };
        menu.FlyoutPresenterClasses.Add("player-song-menu");

        var lead = songs[0];

        menu.Items.Add(new MenuItem
        {
            Header = card.Title,
            IsEnabled = false,
            Focusable = false,
        });

        // 曲数 = 卡片实际入队的长度(拿到全量就是专辑真实曲数,没拿到是采样数),
        // 所以只写「播放（N 首）」,不承诺"整张"。
        var play = MenuItemWithIcon($"播放（{songs.Count} 首）", IconGlyphs.Play);
        play.Click += async (_, _) =>
        {
            menu.Hide();
            if (card.PlayAction is not { } action) return;
            try { await action(); }
            catch { /* 播放失败由播放器提示,菜单不额外弹窗。 */ }
        };
        menu.Items.Add(play);

        var open = MenuItemWithIcon("打开专辑页", IconGlyphs.Album);
        open.Click += async (_, _) =>
        {
            menu.Hide();
            try { await ServiceLocator.Get<MainViewModel>().OpenAlbumAutoAsync(lead); }
            catch { /* 未初始化 / 导航失败:停在浏览页。 */ }
        };
        menu.Items.Add(open);

        SongContextMenu.AddArtistItem(menu, lead);

        var link = AlbumShareLinks.For(lead);
        if (link is null) return menu;

        menu.Items.Add(new Separator());

        var share = MenuItemWithIcon("分享", IconGlyphs.Share);
        share.Click += async (_, _) =>
        {
            menu.Hide();
            var description = string.IsNullOrWhiteSpace(lead.Artist)
                ? lead.Album
                : $"{lead.Album} - {lead.Artist}";
            await SongContextMenu.ShareUriAsync(anchor, link, $"分享专辑：{lead.Album}", description);
        };
        menu.Items.Add(share);

        var copy = MenuItemWithIcon("复制链接", IconGlyphs.CopyLink);
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
}
