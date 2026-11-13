using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

/// <summary>浏览页卡片手势探针(--browse-ops):离线验证封面左下 ▶、右下 •••、右键 more 菜单;
/// 歌单卡、单曲卡、专辑卡三类都有两颗圆钮(歌曲卡点击即播放,专辑卡点击进专辑页);
/// 封面上不挂音源角标;两源混排时菜单链接/收藏项按音源分流
/// (以前写死 music.163.com,QQ 歌单会拼出假链接)。</summary>
internal static class BrowseCardOpsProbe
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aly-browse-ops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        HeadlessApp.ConfigureServices(services =>
        {
            services.AddSingleton(new CookieStore(Path.Combine(root, "cookie.json")));
            services.AddSingleton(new AppStateStore(Path.Combine(root, "state.json")));
            services.AddSingleton(new MusicCacheService(64, Path.Combine(root, "cache"), new HttpClient(new OfflineHandler())));
            services.AddSingleton(sp =>
            {
                var api = new NetEaseApiClient(sp.GetRequiredService<CryptoService>(), sp.GetRequiredService<CnIpPool>(), sp.GetRequiredService<CookieStore>());
                typeof(NetEaseApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(api, new HttpClient(new OfflineHandler()));
                return api;
            });
            services.AddSingleton(sp =>
            {
                var api = new QQMusicApiClient(sp.GetRequiredService<CookieStore>());
                typeof(QQMusicApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(api, new HttpClient(new OfflineHandler()));
                return api;
            });
        });

        // —— 菜单与链接按音源分流(浏览页两个源混排,这几条以前只有网易云世界) ——
        var nePlaylist = new PlaylistItemViewModel(new Playlist
        {
            Id = 6001, Name = "深夜电台", TrackCount = 30, PlayCount = 8800, Source = MusicSource.NetEase,
        })
        {
            CreatorName = "晚风", CreatorId = 7788,
        };
        var qqPlaylist = new PlaylistItemViewModel(new Playlist
        {
            Id = 6002, Name = "通勤电波", Source = MusicSource.QQ, CreatorName = "QQ音乐",
        })
        {
            CreatorName = "QQ音乐",
        };
        var qqToplist = new PlaylistItemViewModel(new Playlist
        {
            Id = 26, TopId = 26, Name = "热歌榜", Source = MusicSource.QQ, CreatorName = "QQ音乐",
        })
        {
            CreatorName = "QQ音乐",
        };
        Check(PlaylistShareLinks.For(nePlaylist.Playlist) == "https://music.163.com/playlist/6001",
            "网易云歌单链接");
        Check(PlaylistShareLinks.For(qqPlaylist.Playlist) == "https://y.qq.com/n/ryqq/playlist/6002",
            "QQ 歌单链接走 y.qq.com");
        Check(PlaylistShareLinks.For(qqToplist.Playlist) == "https://y.qq.com/n/ryqq/toplist/26",
            "QQ 榜单链接走 /toplist/(TopId,不是 dissid)");

        var neMenu = PlaylistContextMenu.Create(new Button(), nePlaylist);
        Check(neMenu.Items.Count == 7 && neMenu.Items[2] is MenuItem { Header: "收藏" },
            "网易云歌单菜单保留「收藏」(共 7 项)");
        var qqMenu = PlaylistContextMenu.Create(new Button(), qqPlaylist);
        Check(qqMenu.Items.Count == 6 && qqMenu.Items.All(item => item is not MenuItem { Header: "收藏" }),
            "QQ 歌单菜单不出现「收藏」(没有收藏他人歌单的写接口)");
        Check(qqMenu.Items[2] is MenuItem { Header: "创建者： QQ音乐", IsEnabled: false }, "QQ 歌单创建者项");

        // —— 视图:歌单卡两颗圆钮;单曲卡、专辑卡同样有(用户要求 hover 对齐推荐歌单) ——
        var vm = ServiceLocator.Get<BrowseViewModel>();
        var opened = 0;
        var playlistCard = new BrowseCardViewModel("深夜电台", "30 首", "", MusicSource.NetEase, 8800,
            () => { opened++; return Task.CompletedTask; }, playlist: nePlaylist);
        var songCard = new BrowseCardViewModel("某首歌", "某歌手", "", MusicSource.QQ, 0,
            play: () => Task.CompletedTask,
            song: new Song { Id = 1, Name = "某首歌", Source = MusicSource.QQ, Album = "某专辑", AlbumMid = "album-mid" });
        var albumLead = new Song
        {
            Id = 2, Name = "专辑第一首", Source = MusicSource.QQ, Album = "某专辑", AlbumMid = "album-mid",
            Artist = "某歌手", ArtistNames = ["某歌手"], ArtistMids = ["singer-mid"],
        };
        var albumSecond = new Song
        {
            Id = 3, Name = "专辑第二首", Source = MusicSource.QQ, Album = "某专辑", AlbumMid = "album-mid",
            Artist = "某歌手", ArtistNames = ["某歌手"], ArtistMids = ["singer-mid"],
        };
        var albumCard = new BrowseCardViewModel("某专辑", "某歌手 · 共 2 首", "", MusicSource.QQ, 0,
            play: () => Task.CompletedTask,
            albumSongs: new List<Song> { albumLead, albumSecond });
        // 三张卡都放进第一块(推荐歌单):后面的区块在 820 高的窗口里可能在视口外,
        // 右键点会落到窗口外,测不到 ContextRequested。
        vm.RecommendedSection.Items.Add(playlistCard);
        vm.RecommendedSection.Items.Add(songCard);
        vm.RecommendedSection.Items.Add(albumCard);

        var view = new BrowseView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 820, Content = view };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        window.Show();
        await Task.Delay(300);

        var cards = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("browse-card") && button.DataContext is BrowseCardViewModel)
            .ToArray();
        var playlistButton = cards.Single(button => ((BrowseCardViewModel)button.DataContext!).IsPlaylist);
        var songButton = cards.Single(button => ((BrowseCardViewModel)button.DataContext!).Song is not null);
        var albumButton = cards.Single(button => ((BrowseCardViewModel)button.DataContext!).IsAlbum);

        var ops = playlistButton.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("card-op")).ToArray();
        Check(ops.Length == 2 && ops.All(button => button.IsEffectivelyVisible),
            "歌单卡封面浮现 ▶ 与 ••• 两个操作圆钮");
        var songOps = songButton.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("card-op")).ToArray();
        Check(songOps.Length == 2 && songOps.All(button => button.IsEffectivelyVisible),
            "单曲卡封面也浮现 ▶ 与 •••(点击卡本身直接播放)");
        var albumOps = albumButton.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("card-op")).ToArray();
        Check(albumOps.Length == 2 && albumOps.All(button => button.IsEffectivelyVisible),
            "专辑卡封面也浮现 ▶ 与 •••(点击卡本身进专辑页)");

        var art = playlistButton.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("card-art") || border.Classes.Contains("browse-art"));
        var artOrigin = art.TranslatePoint(default, playlistButton)!.Value;
        var play = ops.Single(button => button.HorizontalAlignment == HorizontalAlignment.Left);
        var more = ops.Single(button => button.HorizontalAlignment == HorizontalAlignment.Right);
        var playOrigin = play.TranslatePoint(default, playlistButton)!.Value;
        var moreOrigin = more.TranslatePoint(default, playlistButton)!.Value;
        Check(Math.Abs(art.Bounds.Width - 168) < 1, $"封面尺寸={art.Bounds.Width},期望 168");
        Check(Math.Abs(playOrigin.X - (artOrigin.X + 8)) < 1
              && Math.Abs(playOrigin.Y + play.Bounds.Height - (artOrigin.Y + art.Bounds.Height - 8)) < 1,
            "▶ 贴在封面左下角(内缩 8)");
        Check(Math.Abs(moreOrigin.X + more.Bounds.Width - (artOrigin.X + art.Bounds.Width - 8)) < 1
              && Math.Abs(moreOrigin.Y + more.Bounds.Height - (artOrigin.Y + art.Bounds.Height - 8)) < 1,
            "••• 贴在封面右下角(内缩 8)");

        // 封面不再挂音源角标(平台靠页面上方的筛选胶囊区分):卡片里连 source-chip 这个类都不该有。
        Check(!playlistButton.GetVisualDescendants().OfType<Border>()
                .Any(border => border.Classes.Contains("source-chip"))
              && !songButton.GetVisualDescendants().OfType<Border>()
                .Any(border => border.Classes.Contains("source-chip")),
            "封面不显示音源角标");

        // 三个断点都不能让 ▶/••• 撞在一起(小屏卡片只有 148px 宽),播放量徽章始终在最上排。
        var badge = playlistButton.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("play-count-badge"));
        foreach (var width in new[] { 1200.0, 800, 520 })
        {
            window.Width = width;
            await Task.Delay(150);
            var playLeft = play.TranslatePoint(default, playlistButton)!.Value;
            var moreLeft = more.TranslatePoint(default, playlistButton)!.Value;
            var badgeTopLeft = badge.TranslatePoint(default, playlistButton)!.Value;
            Check(playLeft.X + play.Bounds.Width < moreLeft.X,
                $"{width}px ▶ 与 ••• 不重叠");
            Check(badgeTopLeft.Y + badge.Bounds.Height < playLeft.Y,
                $"{width}px 播放量徽章(顶边)与 ▶(底边)不同排");
            Capture(window, root, $"browse-card-{(int)width}");
        }
        window.Width = 1200;
        await Task.Delay(150);

        // 目检图:强行点亮三张卡的圆钮(悬停态才可见),一张图里能对比歌单/单曲/专辑的按钮位置。
        foreach (var op in songOps.Concat(albumOps)) op.Opacity = 1;
        play.Opacity = 1;
        more.Opacity = 1;
        await Task.Delay(60);
        using (var bitmap = new RenderTargetBitmap(
                   new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96)))
        {
            bitmap.Render(window);
            bitmap.Save(Path.Combine(root, "browse-card-ops.png"), PngBitmapEncoderOptions.Default);
        }
        Console.WriteLine($"[browse-ops] 目检图: {Path.Combine(root, "browse-card-ops.png")}");

        // 封面其余区域仍然进歌单页:操作圆钮那层 Panel 不得把整张卡的点击吃掉。
        var artPoint = playlistButton.TranslatePoint(new Point(art.Bounds.Width / 2, 90), window)!.Value;
        window.MouseMove(artPoint);
        window.MouseDown(artPoint, MouseButton.Left);
        window.MouseUp(artPoint, MouseButton.Left);
        await Task.Delay(150);
        Check(opened == 1, $"点封面空处仍打开歌单(实际 {opened} 次)");

        // ▶ 命中:按下落在圆钮上,外层卡片不进入按下态(否则会顺带打开歌单页)。
        // ⚠ 无头里卡片内的嵌套 Button 合成不出 Click(与个性推荐那张卡同一现象),处理函数本体单独调。
        var playPoint = Center(play, window);
        window.MouseMove(playPoint);
        window.MouseDown(playPoint, MouseButton.Left);
        Check(play.IsPressed && !playlistButton.IsPressed, "指针按下命中 ▶ 圆钮,外层卡片不进入按下态");
        window.MouseUp(playPoint, MouseButton.Left);
        await Task.Delay(80);

        Check(((BrowseCardViewModel)play.DataContext!).Playlist?.Id == 6001, "▶ 的 DataContext 指向卡片携带的歌单");
        InvokeHandler(view, "OnCardPlayClick", play, new RoutedEventArgs());
        await Task.Delay(150);
        Check(opened == 1, "点 ▶ 不触发卡片打开行为");

        // 单曲卡菜单结构(不弹出:无头里 Transient 菜单关不掉,会盖住后面的卡片):
        // 歌名(灰) / 添加到歌单 / 下载 / 表演者 / 专辑 / 分享…
        var songMenu = SongContextMenu.Create(new Button(), songCard.Song!);
        Check(songMenu.Items[0] is MenuItem { Header: "某首歌", IsEnabled: false }
              && songMenu.Items[1] is MenuItem { Header: "添加到歌单" },
            "单曲卡菜单 = 歌名 / 添加到歌单 / …");

        // 专辑卡菜单:结构对齐歌单菜单(播放 / 属性 / 分享三段)。
        var albumMenu = AlbumCardContextMenu.Create(
            new Button(), albumCard.Title, albumCard.AlbumSongs!, albumCard.PlayAction);
        Check(albumMenu.Items.Count == 7
              && albumMenu.Items[0] is MenuItem { Header: "某专辑", IsEnabled: false }
              && albumMenu.Items[1] is MenuItem { Header: "播放（2 首）" }
              && albumMenu.Items[2] is MenuItem { Header: "打开专辑页" }
              && albumMenu.Items[3] is MenuItem { Header: "表演者： 某歌手" }
              && albumMenu.Items[4] is Separator
              && albumMenu.Items[5] is MenuItem { Header: "分享" }
              && albumMenu.Items[6] is MenuItem { Header: "复制链接" },
            "专辑卡菜单 = 专辑名 / 播放 / 打开专辑页 / 表演者 / ─ / 分享 / 复制链接");

        // 歌单卡右键:弹 more 菜单。先测它 —— Transient 菜单弹出后会盖住后面的卡片,
        // 专辑卡那条改用 InvokeHandler 走 ContextRequested,不受遮挡影响。
        var morePoint = Center(more, window);
        window.MouseMove(morePoint);
        window.MouseDown(morePoint, MouseButton.Right);
        window.MouseUp(morePoint, MouseButton.Right);
        await Task.Delay(150);
        Check(HasMenu(window, "深夜电台"), "右键弹出歌单菜单");

        // 右键/长按路径:ContextRequested 对专辑卡要真的弹出专辑菜单(桌面上右键、触屏上长按同一入口)。
        InvokeHandler(view, "OnCardContextRequested", albumButton, new ContextRequestedEventArgs());
        await Task.Delay(150);
        Check(HasMenu(window, "某专辑"), "专辑卡右键/长按弹出专辑菜单");

        // 单曲卡同理(新歌速递与关注新歌共用这一条路径)。
        InvokeHandler(view, "OnCardContextRequested", songButton, new ContextRequestedEventArgs());
        await Task.Delay(150);
        Check(HasMenu(window, "某首歌"), "单曲卡右键/长按弹出歌曲菜单");

        // —— 返回浏览页不该回到顶部 ——
        // 内容区按模板每次新建 BrowseView,ScrollViewer 是新的;位置靠 VM 的快照 + DetailPageScrollController 恢复。
        // 这里把页面撑到可滚动,模拟"切走 → 切回"(旧视图离树、新视图挂上),再核对 offset。
        vm.ToplistsSection.Items.Add(new BrowseCardViewModel("占位榜单", "占位", "", MusicSource.NetEase));
        vm.NewSongsSection.Items.Add(new BrowseCardViewModel("占位新歌", "占位", "", MusicSource.NetEase));
        await Task.Delay(200);
        var pageScroller = view.FindControl<ScrollViewer>("PageScroller")!;
        pageScroller.Offset = new Vector(0, 320);
        await Task.Delay(150);
        var savedOffset = pageScroller.Offset.Y;
        Check(savedOffset > 100,
            $"页面能滚动(offset={savedOffset:0} extent={pageScroller.Extent.Height:0})");

        window.Content = new Border();                 // 旧视图离树
        await Task.Delay(80);
        vm.RestoreScrollPosition();                    // = MainViewModel.OnActivePageChanged("Browse")
        var reopened = new BrowseView { DataContext = vm };
        window.Content = reopened;
        for (var index = 0; index < 24; index++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
        var restoredOffset = reopened.FindControl<ScrollViewer>("PageScroller")!.Offset.Y;
        Check(Math.Abs(restoredOffset - savedOffset) < 1,
            $"返回浏览页保持滚动位置(离开 {savedOffset:0} → 回来 {restoredOffset:0})");

        // 调用点也要接上:MainViewModel 切回 Browse 时必须重新把快照标成待恢复(否则视图建好了也没东西可读)。
        var main = ServiceLocator.Get<MainViewModel>();
        main.ActivePage = "Recommend";
        main.ActivePage = "Browse";
        Check(vm.TryGetPendingScrollRestore(0, out _, out var pending)
              && Math.Abs(pending - savedOffset) < 1,
            $"ActivePage 切回 Browse 触发滚动恢复(待恢复 {pending:0})");

        window.Close();
        Console.WriteLine("[browse-ops] PASS; 浏览页卡片手势、两源菜单分流与滚动位置恢复全部通过");
    }

    private static Point Center(Control control, Window window)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Capture(Window window, string root, string state)
    {
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(root, state + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>调视图上的 XAML 事件处理函数(名字/签名由编译期检查,无头里绕开指针手势差异)。</summary>
    private static void InvokeHandler(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    /// <summary>最后一个菜单的首项文字(三类菜单的首项都是不可点的标题:歌名/歌单名/专辑名)。
    /// 无头里 Transient 菜单关不掉,所以用"当前最上层的菜单是什么"来判断类型,而不是数个数。
    /// ⚠ 菜单呈现器挂在窗口视觉树里(OverlayPopupHost),不在 OverlayLayer 上。</summary>
    private static string MenuTitle(Window window)
    {
        var presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().LastOrDefault();
        return presenter?.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault()?.Header as string ?? "";
    }

    /// <summary>窗口里是否已经弹出了首项为 <paramref name="title"/> 的菜单。
    /// 多次弹菜单时 presenter 的先后顺序不稳定,验证"某类菜单出现过"用 Any 更可靠。</summary>
    private static bool HasMenu(Window window, string title)
        => window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Any(presenter =>
            presenter.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault()?.Header as string == title);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("[browse-ops] " + message);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline probe"));
    }
}
