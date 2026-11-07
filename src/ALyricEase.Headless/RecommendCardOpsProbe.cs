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

/// <summary>个性推荐页歌单卡操作探针(--recommend-ops):离线验证封面上的 ▶/••• 只出现在歌单卡、
/// 位置贴封面左下/右下、点 ▶ 只播歌单不打开详情页、右键弹出歌单 more 菜单。
/// 歌曲卡(热门歌曲/猜你喜欢)不该长按钮 —— 那些卡点击即播放,没有"整张歌单"语义。</summary>
internal static class RecommendCardOpsProbe
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aly-recommend-ops-" + Guid.NewGuid().ToString("N"));
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

        var vm = ServiceLocator.Get<RecommendViewModel>();
        var player = ServiceLocator.Get<PlayerViewModel>();

        // 歌单卡:activate 换成探针自己的计数器(真实实现会去开详情页),以便证明 ▶ 没顺带触发它。
        var opened = 0;
        var playlist = new PlaylistItemViewModel(new Playlist
        {
            Id = 5001, Name = "深夜电台", TrackCount = 42, PlayCount = 1_200_000,
        })
        {
            CreatorName = "晚风", CreatorId = 7788,
        };
        var playlistCard = new RecommendCardViewModel("深夜电台", "根据你的喜好", "", 1_200_000, 5001,
            () => { opened++; return Task.CompletedTask; }, playlist);
        // 歌曲卡:没有歌单数据,不该出现操作圆钮。
        var songCard = new RecommendCardViewModel("某首歌", "某歌手", "", 0, 9001);

        vm.ApplySection("推荐歌单", [playlistCard]);
        vm.ApplySection("热门歌曲", [songCard]);

        // 真实映射(ToPlaylistCard)必须把歌单数据挂到卡片上,否则按钮拿不到东西可播。
        var mapped = (RecommendCardViewModel)vm.ToPlaylistCard(new RecommendItem(
            5002, "通勤电波", "根据你的喜好", "", 88_000, 30, MusicSource.NetEase, "夜航", 4321));
        Check(mapped.IsPlaylist && mapped.Playlist!.Id == 5002 && mapped.Playlist.CreatorId == 4321
              && mapped.Playlist.CreatorName == "夜航", "推荐歌单卡片携带歌单数据(含创建者)");
        Check(!songCard.IsPlaylist, "歌曲卡片不带歌单数据");

        // 菜单结构:与用户页共用同一份,创建者项依赖 personalized/playlist 下发的 creator。
        var menu = PlaylistContextMenu.Create(new Button(), playlist);
        var items = menu.Items.ToList();
        Check(items.Count == 7, $"歌单 more 菜单项数={items.Count},期望 7");
        Check(items[0] is MenuItem { IsEnabled: false, Header: "深夜电台" }, "首项为禁用的歌单标题");
        Check(items[3] is MenuItem { Header: "创建者： 晚风", IsEnabled: true }, "创建者项应为可跳转的「晚风」");
        Check(items[5] is MenuItem { Header: "分享" } && items[6] is MenuItem { Header: "复制链接" },
            "分享/复制链接项齐全");

        var view = new RecommendView { DataContext = vm };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        window.Show();
        await Task.Delay(200);

        var cards = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("card") && button.DataContext is RecommendCardViewModel)
            .ToArray();
        var playlistButton = cards.Single(button => ((RecommendCardViewModel)button.DataContext!).IsPlaylist);
        var songButton = cards.Single(button => !((RecommendCardViewModel)button.DataContext!).IsPlaylist);

        var ops = playlistButton.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("card-op")).ToArray();
        Check(ops.Length == 2 && ops.All(button => button.IsEffectivelyVisible),
            "歌单卡封面浮现 ▶ 与 ••• 两个操作圆钮");
        var songOps = songButton.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("card-op")).ToArray();
        Check(songOps.Length == 2 && songOps.All(button => !button.IsEffectivelyVisible),
            "歌曲卡不显示操作圆钮");

        // 位置:贴封面(而不是整卡)左下/右下,内缩 8 —— 卡片文字行在封面下方,贴整卡底边会压住标题。
        var art = playlistButton.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("card-art"));
        var artOrigin = art.TranslatePoint(default, playlistButton)!.Value;
        var play = ops.Single(button => button.HorizontalAlignment == HorizontalAlignment.Left);
        var more = ops.Single(button => button.HorizontalAlignment == HorizontalAlignment.Right);
        var playOrigin = play.TranslatePoint(default, playlistButton)!.Value;
        var moreOrigin = more.TranslatePoint(default, playlistButton)!.Value;
        Check(Math.Abs(art.Bounds.Width - 200) < 1, $"封面尺寸={art.Bounds.Width},期望 200");
        Check(Math.Abs(playOrigin.X - (artOrigin.X + 8)) < 1
              && Math.Abs(playOrigin.Y + play.Bounds.Height - (artOrigin.Y + art.Bounds.Height - 8)) < 1,
            "▶ 贴在封面左下角(内缩 8)");
        Check(Math.Abs(moreOrigin.X + more.Bounds.Width - (artOrigin.X + art.Bounds.Width - 8)) < 1
              && Math.Abs(moreOrigin.Y + more.Bounds.Height - (artOrigin.Y + art.Bounds.Height - 8)) < 1,
            "••• 贴在封面右下角(内缩 8)");
        Check(play.Opacity == 0 && play.IsEffectivelyVisible,
            "非悬停时操作圆钮透明但不隐藏(不做布局反馈)");

        // 目检图:强行点亮两颗圆钮(悬停态才可见),PNG 落到临时目录供人工看一眼观感。
        play.Opacity = 1;
        more.Opacity = 1;
        await Task.Delay(60);
        using (var bitmap = new RenderTargetBitmap(
                   new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96)))
        {
            bitmap.Render(window);
            bitmap.Save(Path.Combine(root, "card-ops.png"), PngBitmapEncoderOptions.Default);
        }
        Console.WriteLine($"[recommend-ops] 目检图: {Path.Combine(root, "card-ops.png")}");

        // 封面其余区域仍然进详情页:操作圆钮那层 Panel 不得把整张卡的点击吃掉。
        var coverPoint = playlistButton.TranslatePoint(new Point(art.Bounds.Width / 2, 100), window)!.Value;
        window.MouseMove(coverPoint);
        window.MouseDown(coverPoint, MouseButton.Left);
        window.MouseUp(coverPoint, MouseButton.Left);
        await Task.Delay(150);
        Check(opened == 1, $"点封面空处仍打开歌单(实际 {opened} 次)");

        // ▶ 命中:按下必须落在这颗圆钮上(命中错 = 被遮罩/封面图挡住,或落在外层卡片上),
        // 同时外层卡片不得进入按下态 —— 那是"点圆钮顺带开了详情页"的唯一来源。
        // ⚠ 无头里这颗圆钮的 Click 合成不出来(裸按钮、裸嵌套按钮都能点,只有卡片里这颗如此:
        // Button 收到 tap 手势的 PointerCaptureLost 就把 IsPressed 复位),所以处理函数本体单独调。
        var playPoint = Center(play, window);
        window.MouseMove(playPoint);
        window.MouseDown(playPoint, MouseButton.Left);
        Check(play.IsPressed && !playlistButton.IsPressed, "指针按下命中 ▶ 圆钮,外层卡片不进入按下态");
        window.MouseUp(playPoint, MouseButton.Left);
        await Task.Delay(80);

        // 处理函数本体:拿到的确实是这张歌单,且只播歌单、不碰卡片的打开行为。
        Check(((RecommendCardViewModel)play.DataContext!).Playlist?.Id == 5001, "▶ 的 DataContext 指向卡片携带的歌单");
        InvokeHandler(view, "OnCardPlayClick", play, new RoutedEventArgs());
        await Task.Delay(150);
        Check(opened == 1, "点 ▶ 不触发卡片打开行为");

        // 先测歌曲卡右键:没有歌单数据,不该弹菜单(顺序放在歌单卡之前,省掉"先开再关"的步骤 ——
        // 无头里 Esc 关不掉 Transient 菜单,焦点落在弹出层上)。
        var songPoint = songButton.TranslatePoint(new Point(songButton.Bounds.Width / 2, 60), window)!.Value;
        window.MouseMove(songPoint);
        window.MouseDown(songPoint, MouseButton.Right);
        window.MouseUp(songPoint, MouseButton.Right);
        await Task.Delay(150);
        Check(MenuPresenterCount(window) == 0, "歌曲卡右键不弹歌单菜单");

        // 歌单卡右键:弹 more 菜单(与 ••• 同一份)。
        var morePoint = Center(more, window);
        window.MouseMove(morePoint);
        window.MouseDown(morePoint, MouseButton.Right);
        window.MouseUp(morePoint, MouseButton.Right);
        await Task.Delay(150);
        Check(MenuPresenterCount(window) == 1, "右键弹出歌单菜单");

        window.Close();
        Console.WriteLine("[recommend-ops] PASS; 歌单卡 ▶/••• 与右键菜单全部通过");    }

    private static Point Center(Control control, Window window)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>调视图上的 XAML 事件处理函数(名字/签名由编译期检查,无头里绕开指针手势差异)。</summary>
    private static void InvokeHandler(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    /// <summary>⚠ 本例里菜单呈现器挂在窗口视觉树里(OverlayPopupHost),
    /// 按 OverlayLayer.GetVisualDescendants() 数出来是 0(实测)。</summary>
    private static int MenuPresenterCount(Window window)
        => window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Count();

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("[recommend-ops] " + message);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline probe"));
    }
}
