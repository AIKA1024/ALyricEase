using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>底部播放条:尺寸与宽屏一致,narrow 仅精简右侧按钮(隐藏红心/模式/音量/更多,留三键),
/// 响应式类按窗口宽度切换。进度条为共享的 PlayerProgressBar(自带拖动/气泡)；
/// 点空白打开正在播放页，桌面右键/触控长按弹出当前歌曲菜单。</summary>
public partial class PlayerBarView : UserControl
{
    // 原版 LyricEase 内嵌字体的菜单字形。与 TrackRow 的歌手/专辑菜单使用同一字体与码位。
    private static readonly FontFamily MenuIconFont =
        new("avares://ALyricEase/Assets/Fonts/FluentUISystemIcons.ttf#FluentUISystemIcons");

    public PlayerBarView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ResponsiveClasses.ApplyByWindow(this);
        SizeChanged += (_, _) => ResponsiveClasses.ApplyByWindow(this);
    }

    /// <summary>Tap 封面/曲目区 → 打开正在播放页。用 Tap(按下抬起才算)而非 PointerPressed:
    /// 按下即触发会跟拖动/滑进度误触;按钮点击冒泡上来时,按来源过滤掉。</summary>
    private void OnTrackAreaTap(object? sender, TappedEventArgs e)
    {
        // Grid 处理触摸/鼠标事件时,播放按钮的 Tap 会冒泡;但歌曲区域(含封面)仍应打开详情。
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;

        // 无当前曲目时点击不打开正在播放页(占位标题不可点击)
        if (DataContext is not PlayerViewModel { CurrentSong: not null }) return;

        // 用 AppShell 而非 Window 找 MainViewModel:移动端由 Activity/ViewController 承载,可视树里没有 Window
        if (this.FindAncestorOfType<AppShell>()?.DataContext is MainViewModel { ShowNowPlaying: false } vm)
            vm.OpenNowPlayingCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>ContextRequested 在桌面由右键触发，在触控平台由长按触发。
    /// 菜单按当前歌曲即时构造，避免切歌后静态 Flyout 仍持有上一首歌的命令参数。</summary>
    private void OnPlayerContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not PlayerViewModel { CurrentSong: { } song } player) return;

        var menu = CreateSongMenu(song, player.QueueSourceName);
        menu.ShowAt(PlayerRoot, true);
        e.Handled = true;
    }

    internal MenuFlyout CreateSongMenu(Song song, string? queueSourceName)
    {
        var menu = new MenuFlyout { ShowMode = FlyoutShowMode.Transient };
        menu.FlyoutPresenterClasses.Add("player-song-menu");

        menu.Items.Add(new MenuItem
        {
            Header = song.Name,
            IsEnabled = false,
            Focusable = false,
        });

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
            share.Click += async (_, _) => await ShareSongAsync(song, link);
        menu.Items.Add(share);

        var openBrowser = MenuItemWithIcon("在浏览器中打开", "\uE94F");
        openBrowser.IsEnabled = link is not null;
        if (link is not null)
            openBrowser.Click += async (_, _) => await OpenBrowserAsync(link);
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

    private async Task ShareSongAsync(Song song, string link)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var ownerHandle = topLevel?.TryGetPlatformHandle()?.Handle ?? 0;
            var description = string.IsNullOrWhiteSpace(song.Artist)
                ? song.Name
                : $"{song.Name} - {song.Artist}";
            await ServiceLocator.Get<IPlatformShareService>().ShareUriAsync(
                ownerHandle, $"分享歌曲：{song.Name}", description, new Uri(link));
        }
        catch { /* 当前宿主不支持系统分享时静默返回，浏览器入口仍可用。 */ }
    }

    private async Task OpenBrowserAsync(string link)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
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
