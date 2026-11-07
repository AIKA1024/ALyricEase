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

/// <summary>浏览页歌单/榜单卡的手势探针(--browse-ops):离线验证封面左下 ▶、右下 •••、
/// 右键 more 菜单只出现在歌单/榜单卡上;封面上不挂音源角标;
/// 两源混排时菜单链接/收藏项按音源分流(以前写死 music.163.com,QQ 歌单会拼出假链接)。</summary>
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

        // —— 视图:歌单卡两颗圆钮、歌曲卡没有 ——
        var vm = ServiceLocator.Get<BrowseViewModel>();
        var opened = 0;
        var playlistCard = new BrowseCardViewModel("深夜电台", "30 首", "", MusicSource.NetEase, 8800,
            () => { opened++; return Task.CompletedTask; }, playlist: nePlaylist);
        var songCard = new BrowseCardViewModel("某首歌", "某歌手", "", MusicSource.QQ, 0);
        vm.RecommendedSection.Items.Add(playlistCard);
        vm.NewSongsSection.Items.Add(songCard);

        var view = new BrowseView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 820, Content = view };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        window.Show();
        await Task.Delay(300);

        var cards = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("browse-card") && button.DataContext is BrowseCardViewModel)
            .ToArray();
        var playlistButton = cards.Single(button => ((BrowseCardViewModel)button.DataContext!).IsPlaylist);
        var songButton = cards.Single(button => !((BrowseCardViewModel)button.DataContext!).IsPlaylist);

        var ops = playlistButton.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("card-op")).ToArray();
        Check(ops.Length == 2 && ops.All(button => button.IsEffectivelyVisible),
            "歌单卡封面浮现 ▶ 与 ••• 两个操作圆钮");
        var songOps = songButton.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("card-op")).ToArray();
        Check(songOps.Length == 2 && songOps.All(button => !button.IsEffectivelyVisible),
            "歌曲卡不显示操作圆钮");

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

        // 目检图:强行点亮两颗圆钮(悬停态才可见)。
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

        // 先测歌曲卡右键:没有歌单数据,不该弹菜单(无头里 Esc 关不掉 Transient 菜单,顺序放前面)。
        var songPoint = songButton.TranslatePoint(new Point(songButton.Bounds.Width / 2, 50), window)!.Value;
        window.MouseMove(songPoint);
        window.MouseDown(songPoint, MouseButton.Right);
        window.MouseUp(songPoint, MouseButton.Right);
        await Task.Delay(150);
        Check(MenuPresenterCount(window) == 0, "歌曲卡右键不弹歌单菜单");

        // 歌单卡右键:弹 more 菜单。
        var morePoint = Center(more, window);
        window.MouseMove(morePoint);
        window.MouseDown(morePoint, MouseButton.Right);
        window.MouseUp(morePoint, MouseButton.Right);
        await Task.Delay(150);
        Check(MenuPresenterCount(window) == 1, "右键弹出歌单菜单");

        window.Close();
        Console.WriteLine("[browse-ops] PASS; 浏览页歌单/榜单卡手势与两源菜单分流全部通过");
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

    /// <summary>⚠ 菜单呈现器挂在窗口视觉树里(OverlayPopupHost),不在 OverlayLayer 上。</summary>
    private static int MenuPresenterCount(Window window)
        => window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Count();

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
