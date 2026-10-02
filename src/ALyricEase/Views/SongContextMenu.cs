using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.IO;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>歌曲右键/长按菜单(播放/下一首播放/添加到歌单/表演者/专辑/分享/浏览器/复制链接/来源/从歌单中移除)。
/// 原本只在 PlayerBarView 里构造;现在播放条与 TrackRow(歌单/专辑/每日/歌手等所有歌曲行)共用:
/// 桌面右键、触控长按(ContextRequested 统一触发)和行内"更多"按钮都弹这一份。
/// 菜单按歌曲即时构造,避免静态 Flyout 持有过期命令参数。anchor 仅用于点击时找 TopLevel(分享/浏览器/剪贴板)。</summary>
internal static class SongContextMenu
{
    // 原版 LyricEase 内嵌字体的菜单字形。与 TrackRow 的歌手/专辑菜单使用同一字体与码位。
    private static readonly FontFamily MenuIconFont =
        new("avares://ALyricEase/Assets/Fonts/FluentUISystemIcons.ttf#FluentUISystemIcons");

    // "从歌单中移除"红(与封面红心同色,仿原版红色危险项)
    private static readonly SolidColorBrush RemoveBrush = new(Color.FromRgb(0xE7, 0x4C, 0x3C));

    /// <summary>构造歌曲菜单。row 传歌曲行的 SongItemViewModel 时(播放条不传),歌名下方多出
    /// "播放 / 下一首播放"两项(行内语境:播放走该行所在队列;下一首播放插到当前曲之后),横线后才是其余项。
    /// 图标用原版字体码位:E912 PlayFilled / E913 PlayNext(docs/icon-fonts.md)。</summary>
    public static MenuFlyout Create(Visual anchor, Song song, string? queueSourceName, SongItemViewModel? row = null)
    {
        var menu = new MenuFlyout { ShowMode = FlyoutShowMode.Transient };
        menu.FlyoutPresenterClasses.Add("player-song-menu");

        menu.Items.Add(new MenuItem
        {
            Header = song.Name,
            IsEnabled = false,
            Focusable = false,
        });

        if (row is not null)
        {
            var play = MenuItemWithIcon("播放", "\uE912");
            play.Click += (_, _) => row.PlayCommand.Execute(null);
            menu.Items.Add(play);

            var playNext = MenuItemWithIcon("下一首播放", "\uE913");
            playNext.Click += (_, _) =>
            {
                try
                {
                    ServiceLocator.Get<PlayerViewModel>().PlaySongNext(song);
                }
                catch { /* 设计器/无头宿主没有应用 DI,仅保留菜单结构。 */ }
            };
            menu.Items.Add(playNext);

            menu.Items.Add(new Separator());
        }

        var addToPlaylist = MenuItemWithIcon("添加到歌单", "\uE92B");
        addToPlaylist.Click += (_, _) =>
        {
            try
            {
                ServiceLocator.Get<MainViewModel>().OpenAddSongToPlaylistDialogCommand.Execute(song);
            }
            catch { /* 设计器/无头宿主没有应用 DI，仅保留菜单结构。 */ }
        };
        menu.Items.Add(addToPlaylist);

        AddArtistItem(menu, song);

        var album = MenuItemWithIcon($"专辑： {song.Album}", "\uE922");
        album.IsEnabled = HasAlbumTarget(song);
        if (album.IsEnabled)
            album.Click += async (_, _) => await OpenAlbumAsync(song);
        menu.Items.Add(album);

        menu.Items.Add(new Separator());

        var link = SongShareLinks.For(song);
        var share = MenuItemWithIcon("分享", "\uE918");
        share.IsEnabled = link is not null;
        if (link is not null)
            share.Click += async (_, _) => await ShareSongAsync(anchor, song, link);
        menu.Items.Add(share);

        var openBrowser = MenuItemWithIcon("在浏览器中打开", "\uE94F");
        openBrowser.IsEnabled = link is not null;
        if (link is not null)
            openBrowser.Click += async (_, _) => await OpenBrowserAsync(anchor, link);
        menu.Items.Add(openBrowser);

        var copy = MenuItemWithIcon("复制链接", "\uE902");
        copy.IsEnabled = link is not null;
        if (link is not null)
            copy.Click += async (_, _) => await ClipboardService.TryCopyTextAsync(link);
        menu.Items.Add(copy);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = $"来源： {SourceText(song, queueSourceName)}",
            Focusable = false,
            IsHitTestVisible = false,
        });

        // 自己的歌单:底部红色"从歌单中移除"(原版 Delete 字形 E932);命令由歌单页注入行 VM
        if (row?.RemoveFromSourceCommand is { } removeCommand)
        {
            menu.Items.Add(new Separator());
            var remove = new MenuItem
            {
                Header = "从歌单中移除",
                Foreground = RemoveBrush,
                Icon = new TextBlock
                {
                    Text = "\uE932",
                    FontFamily = MenuIconFont,
                    FontSize = 14,
                    Foreground = RemoveBrush,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };
            remove.Click += (_, _) => removeCommand.Execute(row);
            menu.Items.Add(remove);
        }

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

    private static void AddArtistItem(MenuFlyout menu, Song song)
    {
        var artists = ArtistTargets(song);
        if (artists.Count <= 1)
        {
            var artist = MenuItemWithIcon($"表演者： {song.Artist}", "\uE921");
            artist.IsEnabled = artists.Count == 1;
            if (artists.Count == 1) artist.Command = artists[0].OpenCommand;
            menu.Items.Add(artist);
            return;
        }

        var parent = MenuItemWithIcon("表演者", "\uE920");
        foreach (var artist in artists)
            parent.Items.Add(new MenuItem { Header = artist.Name, Command = artist.OpenCommand });
        menu.Items.Add(parent);
    }

    private static List<ArtistNavItem> ArtistTargets(Song song)
    {
        var result = new List<ArtistNavItem>();
        var isQq = song.Source == MusicSource.QQ;
        var keyCount = isQq ? song.ArtistMids.Count : song.ArtistIds.Count;
        var count = Math.Min(song.ArtistNames.Count, keyCount);
        for (var i = 0; i < count; i++)
        {
            if (isQq)
            {
                if (!string.IsNullOrEmpty(song.ArtistMids[i]))
                    result.Add(new ArtistNavItem(0, song.ArtistNames[i], song.ArtistMids[i]));
            }
            else if (song.ArtistIds[i] != 0)
            {
                result.Add(new ArtistNavItem(song.ArtistIds[i], song.ArtistNames[i]));
            }
        }

        return result;
    }

    private static bool HasAlbumTarget(Song song) => song.Source == MusicSource.QQ
        ? song.AlbumMid.Length > 0
        : song.AlbumId != 0;

    private static async Task OpenAlbumAsync(Song song)
    {
        try
        {
            var main = ServiceLocator.Get<MainViewModel>();
            if (song.Source == MusicSource.QQ)
                await main.OpenQqAlbumCommand.ExecuteAsync(song.AlbumMid);
            else
                await main.OpenAlbumCommand.ExecuteAsync(song.AlbumId);
        }
        catch { /* 未初始化或导航失败时保持当前页。 */ }
    }

    private static async Task ShareSongAsync(Visual anchor, Song song, string link)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(anchor);
            var ownerHandle = topLevel?.TryGetPlatformHandle()?.Handle ?? 0;
            ShareDiag($"菜单分享点击: song={song.Source}#{song.Id}/{song.Mid} link={link} " +
                $"topLevel={(topLevel is null ? "null(anchor 已脱离)" : "ok")} ownerHandle=0x{ownerHandle:X}");
            var description = string.IsNullOrWhiteSpace(song.Artist)
                ? song.Name
                : $"{song.Name} - {song.Artist}";
            await ServiceLocator.Get<IPlatformShareService>().ShareUriAsync(
                ownerHandle, $"分享歌曲：{song.Name}", description, new Uri(link));
        }
        catch (Exception ex)
        {
            ShareDiag($"菜单分享异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>分享链路诊断:写 %TEMP%\aly-share-diag.log(与 WindowsShareService 同一文件;
    /// ALY_SHARE_DIAG=1 或 Debug 构建开启)。生产路径吞异常,UI 上"点击没反应"时靠它定位。</summary>
    internal static void ShareDiag(string message)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("ALY_SHARE_DIAG");
            var enabled = env == "1" || (env != "0" &&
#if DEBUG
                true
#else
                false
#endif
                );
            if (!enabled) return;
            var text = $"{DateTime.Now:HH:mm:ss.fff} [menu] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "aly-share-diag.log"), text);
        }
        catch
        {
            // 诊断绝不影响分享
        }
    }

    private static async Task OpenBrowserAsync(Visual anchor, string link)
    {
        try
        {
            if (TopLevel.GetTopLevel(anchor)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(link));
        }
        catch { /* 平台没有可处理该链接的应用时静默返回。 */ }
    }

    private static string SourceText(Song song, string? queueSourceName)
    {
        if (!string.IsNullOrWhiteSpace(queueSourceName)) return queueSourceName;
        return song.Source == MusicSource.QQ ? "QQ音乐" : "网易云音乐";
    }
}
