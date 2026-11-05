using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

/// <summary>用户页"关注/粉丝"名单回归:假 JSON 走真实客户端解析与分页 → 渲染 → 点行进用户页 →
/// 返回恢复名单(含滚动位置)→ 再返回恢复原用户页;QQ 用户不放出入口。</summary>
internal static class UserRelationsProbe
{
    private const int FollowingTotal = 45;
    private const int FollowerTotal = 8;
    private const string QqOwnerEuin = "qq-owner-euin";
    /// <summary>夹具里的"自己"数字 uin(SetCookie 用同一份):自己页走 uin 而不是 euin。</summary>
    private const string QqSelfUin = "123456789";

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aly-user-relations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // 隔离凭证/状态/缓存:探针不碰真实用户的 cookie 与 AppData
        HeadlessApp.ConfigureServices(services =>
        {
            services.AddSingleton(new CookieStore(Path.Combine(root, "cookie.json")));
            services.AddSingleton(new AppStateStore(Path.Combine(root, "state.json")));
            services.AddSingleton(new MusicCacheService(64, Path.Combine(root, "cache"), new HttpClient(new OfflineHandler())));
            // 用假响应替换真实 HttpClient:同一条"请求 → 反序列化 → 分页"代码路径
            services.AddSingleton(sp =>
            {
                var api = new NetEaseApiClient(
                    sp.GetRequiredService<CryptoService>(),
                    sp.GetRequiredService<CnIpPool>(),
                    sp.GetRequiredService<CookieStore>());
                typeof(NetEaseApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(api, new HttpClient(new FakeHandler()));
                return api;
            });
            // QQ 侧:同一个假响应器(关系名单走 musicu,主页走 fcg)
            services.AddSingleton(sp =>
            {
                var api = new QQMusicApiClient(sp.GetRequiredService<CookieStore>());
                typeof(QQMusicApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(api, new HttpClient(new FakeHandler()));
                return api;
            });
        });

        var main = ServiceLocator.Get<MainViewModel>();
        var profile = main.UserProfile;
        profile.ReleaseCurrentPageData();
        await main.OpenUserCommand.ExecuteAsync(10001L);
        Check(main.ActivePage == "User" && profile.Nickname == "关系测试", "用户页按假数据加载");
        Check(profile.Follows == FollowingTotal && profile.Followeds == FollowerTotal, "关注/粉丝计数解析正确");
        Check(profile.CanOpenRelations, "网易云用户放出名单入口");

        // —— 加载态:数据未到时不能只剩空白(转圈 + "加载中…" 居中于视口) ——
        var pacerBase = UiFramePacer.Demand;
        profile.IsLoading = true;
        var loadingWindow = NewWindow(new UserProfileView { DataContext = profile }, 1000, 700);
        await Task.Delay(120);
        var spinner = loadingWindow.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
            .FirstOrDefault(shape => shape.Classes.Contains("spinner"));
        Check(spinner is { IsEffectivelyVisible: true }, "加载时显示转圈指示");
        Check(loadingWindow.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text == "加载中…" && text.IsEffectivelyVisible), "加载时显示加载中文字");
        Check(!loadingWindow.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text == "音乐品味" && text.IsEffectivelyVisible), "加载层盖住内容区");
        // 卡顿根因:UI 线程静息长睡(~79ms)⇒ 样式动画只有 ~12fps。
        // 现在改用合成动画(渲染线程驱动):既转得顺,又**不该**把 UI 线程从睡眠里拽起来。
        Check(SpinnerRotation.IsSpinning(spinner!), "加载中转圈由合成动画驱动");
        Check(UiFramePacer.Demand == pacerBase, "合成动画不占用帧泵(UI 线程可继续睡)");
        Console.WriteLine("[user-relations] pacer " + UiFramePacer.Describe());
        Capture(loadingWindow, root, "profile-loading");
        // ⚠ 合成动画的结果不会出现在 RenderTargetBitmap 里(两帧逐像素相同),headless 拍不到旋转;
        // "真的在转"由真窗口抓屏探针 --spinner-motion-real 验证。
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Task.Delay(100);
        Capture(loadingWindow, root, "profile-loading-dark");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        await Task.Delay(60);
        // 加载结束后必须真的停:合成动画在隐藏元素上照旧推进时钟
        profile.IsLoading = false;
        await Task.Delay(120);
        Check(!SpinnerRotation.IsSpinning(spinner), "加载结束停止合成动画(不再空转)");
        Check(UiFramePacer.Demand <= pacerBase, "全程没有把 UI 线程拽起来");
        loadingWindow.Close();
        await Task.Delay(60);

        // —— 加载完成的内容淡入(合成层 Opacity):不能只是"啪"地整块出现 ——
        // 三个点必须成立:
        //   ① IsVisible 变 true 的**那一刻**(同步)UI 层 Opacity 就压到 0 —— 渲染读的是它,
        //      排到 Render 优先级再压会先画一帧全亮(实测连闪 2 帧,用户报"还是一下子出来的");
        //   ② 合成层的基值也压到 0(动画从这里起);
        //   ③ 结束写回 1 —— 留着 0 的话以后任何 StopAnimation("Opacity")(页面过渡/模态弹层)
        //      都会把整块拉成透明。
        // ⚠ 屏幕像素是否真的没有闪帧,只能靠 --fade-timing-real 抓屏(本断言读的是属性)。
        var fadeTarget = new StackPanel { IsVisible = false, Width = 200, Height = 80 };
        FadeInWhenShown.SetIsEnabled(fadeTarget, true);
        var fadeWindow = NewWindow(fadeTarget, 420, 300);
        var fadeVisual = ElementComposition.GetElementVisual(fadeTarget)!;
        fadeTarget.IsVisible = true;
        Check(fadeTarget.Opacity < 0.1, "变可见当帧就把 UI 层压到透明(不等 Render 队列)");
        await Task.Delay(20);
        Check(fadeVisual.Opacity < 0.1f, "淡入开始合成基值也是 0");
        await Task.Delay(400);
        Check(fadeTarget.Opacity > 0.99 && fadeVisual.Opacity > 0.99f,
            "淡入结束 UI 层与基值都写回 1(不会让整块以后变透明)");
        fadeWindow.Close();
        await Task.Delay(60);

        // —— 英雄卡:点"关注"进名单页 ——
        var heroView = new UserProfileView { DataContext = profile };
        var window = NewWindow(heroView, 1000, 800);
        var links = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("hero-link")).ToArray();
        Check(links.Length == 2 && links.All(button => button.IsEffectivelyEnabled), "两个计数按钮都可点");
        Capture(window, root, "hero-card");
        Click(window, links[0]);
        await Task.Delay(120);
        Check(main.ActivePage == "UserRelations" && main.UserRelations.Title == "关注", "点关注进入关注名单页");
        Check(main.CurrentPageTitle == "关注", "顶部标题栏跟随名单页标题");
        Check(!profile.HasRetainedPageData, "离开用户页后重数据已释放");
        window.Close();

        // —— 名单页渲染与分页 ——
        var relations = main.UserRelations;
        var view = new UserRelationsPageView { DataContext = relations };
        window = NewWindow(view, 1000, 700);
        await Task.Delay(120);
        Check(relations.Users.Count == 30 && relations.HasMore, "首屏 30 条且还有下一页");
        Check(Rows(window).Length > 0, "名单行已实化");
        var avatars = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("avatar")).ToArray();
        Check(avatars.Length > 0 && avatars.All(border => Math.Abs(border.Bounds.Width - 44) < 1), "头像 44px");
        Check(window.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text is { } t && t.StartsWith("关注 ") && t.Contains(" · 粉丝 ")), "行右侧显示关注/粉丝数");
        Check(window.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Text is { } nick && nick.StartsWith("听众", StringComparison.Ordinal)), "昵称按服务端字段渲染");
        var scroller = view.FindControl<ScrollViewer>("PageScroller")!;
        Check(scroller.Extent.Width <= scroller.Viewport.Width + 1, "宽屏无横向溢出");
        Capture(window, root, "relations-wide");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Task.Delay(100);
        Capture(window, root, "relations-dark");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        await Task.Delay(60);

        scroller.Offset = new Vector(0, scroller.Extent.Height);
        await Task.Delay(200);
        Check(relations.Users.Count == FollowingTotal && !relations.HasMore, "滚到底补到 45 条并停止");

        window.Width = 420;
        await Task.Delay(100);
        Check(Rows(window).Length > 0, "窄屏仍渲染名单行");
        Check(scroller.Extent.Width <= scroller.Viewport.Width + 1, "窄屏无横向溢出");
        scroller.Offset = new Vector(0, 0);
        await Task.Delay(80);
        Capture(window, root, "relations-narrow");
        window.Width = 1000;
        await Task.Delay(100);

        // —— 记录滚动位置后点行 → 对方用户页 ——
        scroller.Offset = new Vector(0, 240);
        await Task.Delay(80);
        var row = Rows(window).First(button => button.TranslatePoint(default, window)!.Value.Y is > 60 and < 620);
        var rowVm = (UserRelationItemViewModel)row.DataContext!;
        Click(window, row);
        await Task.Delay(200);
        Check(main.ActivePage == "User" && profile.Nickname == rowVm.Nickname,
            $"点行进入对方用户页({rowVm.Nickname})");
        window.Close();

        // —— 返回:名单页从快照恢复(不再联网) ——
        main.GoBackCommand.Execute(null);
        await Task.Delay(200);
        Check(main.ActivePage == "UserRelations" && relations.Users.Count == FollowingTotal,
            "返回名单页恢复全部行");
        var restored = new UserRelationsPageView { DataContext = relations };
        window = NewWindow(restored, 1000, 700);
        await Task.Delay(200);
        var restoredScroller = restored.FindControl<ScrollViewer>("PageScroller")!;
        Check(Math.Abs(restoredScroller.Offset.Y - 240) < 1, $"返回后滚动位置恢复({restoredScroller.Offset.Y:F0})");
        window.Close();

        // —— 再返回:原用户页 ——
        main.GoBackCommand.Execute(null);
        await Task.Delay(200);
        Check(main.ActivePage == "User" && profile.Nickname == "关系测试" && profile.Follows == FollowingTotal,
            "再返回恢复原用户页与计数");

        // —— QQ:登录后关注/粉丝也能点开(music.concern.RelationList) ——
        var qqApi = ServiceLocator.Get<QQMusicApiClient>();
        // 离线夹具:跳过真实凭证校验(否则会去打 QQ 的校验接口)
        typeof(QQMusicApiClient).GetField("_credentialValidated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(qqApi, true);
        typeof(QQMusicApiClient).GetField("_nativeCredentialRefreshAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(qqApi, DateTimeOffset.UtcNow.AddHours(1));
        qqApi.SetCookie("uin=" + QqSelfUin + "; qm_keyst=probe-key");

        await profile.LoadQqUserAsync(QqOwnerEuin);
        Check(profile.Nickname == "QQ 关系测试" && profile.Follows == 3 && profile.Followeds == 5,
            "QQ 用户页按假数据加载");
        Check(profile.EncryptUin == QqOwnerEuin, "QQ 他人页记住名单用的 euin");

        // 自己页:计数同样来自主页 fcg nums(曾恒 0 → 用户页显示 0 关注 0 粉丝),
        // 且必须清掉上一个人的 euin,否则点"关注"拉的是别人的名单
        await profile.LoadQqAsync();
        Check(profile.Follows == 3 && profile.Followeds == 5, "QQ 自己页显示真实关注/粉丝(不再 0)");
        Check(profile.EncryptUin.Length == 0, "回自己页清掉上一个人的 euin");
        var qqSelfWindow = NewWindow(new UserProfileView { DataContext = profile }, 1000, 360);
        Capture(qqSelfWindow, root, "hero-qq-self");
        qqSelfWindow.Close();

        await profile.LoadQqUserAsync(QqOwnerEuin);
        Check(profile.CanOpenRelations, "QQ 登录后放出名单入口");

        var qqHeroView = new UserProfileView { DataContext = profile };
        var qqWindow = NewWindow(qqHeroView, 1000, 360);
        var qqLinks = qqWindow.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("hero-link")).ToArray();
        Check(qqLinks.Length == 2 && qqLinks.All(button => button.IsEffectivelyEnabled), "QQ 页两个计数按钮可点");
        Capture(qqWindow, root, "hero-qq");
        Click(qqWindow, qqLinks[0]);
        await Task.Delay(150);
        Check(main.ActivePage == "UserRelations" && main.UserRelations.Title == "关注", "QQ 点关注进入名单页");
        qqWindow.Close();

        var qqRelations = main.UserRelations;
        var qqView = new UserRelationsPageView { DataContext = qqRelations };
        qqWindow = NewWindow(qqView, 1000, 700);
        await Task.Delay(200);
        Check(qqRelations.Users.Count == 3, "QQ 关注把用户与歌手拼成一份(2+1)");
        Check(qqRelations.Users.Count(item => item.IsSinger) == 1, "歌手条目单独标记");
        Check(qqRelations.Users.First(item => item.IsSinger).MetaText.StartsWith("歌手", StringComparison.Ordinal),
            "歌手条目右侧标注歌手");
        Capture(qqWindow, root, "relations-qq");
        var singerRow = Rows(qqWindow).First(button => ((UserRelationItemViewModel)button.DataContext!).IsSinger);
        Click(qqWindow, singerRow);
        await Task.Delay(200);
        Check(main.ActivePage == "Artist", "点歌手行进歌手页");
        qqWindow.Close();
        main.GoBackCommand.Execute(null);
        await Task.Delay(200);

        qqWindow = NewWindow(new UserRelationsPageView { DataContext = qqRelations }, 1000, 700);
        await Task.Delay(150);
        var qqUserRow = Rows(qqWindow).First(button => !((UserRelationItemViewModel)button.DataContext!).IsSinger);
        var qqUserVm = (UserRelationItemViewModel)qqUserRow.DataContext!;
        Click(qqWindow, qqUserRow);
        await Task.Delay(200);
        Check(main.ActivePage == "User" && profile.Nickname == qqUserVm.Nickname,
            $"点 QQ 用户行进对方用户页({qqUserVm.Nickname})");
        qqWindow.Close();

        Console.WriteLine("[user-relations] PASS 关注/粉丝名单:网易云+QQ 解析/分页/点行导航/返回快照");
        Console.WriteLine("[user-relations] capture " + root);
    }

    /// <summary>联网冒烟:用最小分页直接打两个端点,确认字段名与 more 语义没漂移
    /// (匿名可用;只读)。用法:--user-relations-live [uid]</summary>
    public static async Task RunLiveAsync(long uid)
    {
        HeadlessApp.ConfigureServices();
        var api = ServiceLocator.Get<NetEaseApiClient>();
        foreach (var followers in new[] { false, true })
        {
            var (users, more) = await api.GetUserRelationsAsync(uid, followers, 0, 5);
            var first = users.Count > 0 ? users[0].Nickname : "(空)";
            Console.WriteLine($"[user-relations-live] uid={uid} {(followers ? "粉丝" : "关注")} " +
                              $"n={users.Count} more={more} first={first} follows={users.FirstOrDefault()?.Follows}");
            Check(users.Count > 0, $"{(followers ? "粉丝" : "关注")}列表非空");
        }
        Console.WriteLine("[user-relations-live] PASS");

        // QQ:登录态才可读(匿名 code=1000),客户端方法签名与网页版同源
        var qqApi = ServiceLocator.Get<QQMusicApiClient>();
        if (!qqApi.IsLoggedIn)
        {
            Console.WriteLine("[user-relations-live] QQ 未登录,跳过");
            return;
        }
        foreach (var (label, fetch) in new (string, Func<Task<QQMusicApiClient.QQRelationPage>>)[]
                 {
                     ("关注用户", () => qqApi.GetFollowUsersAsync("", 0, 5)),
                     ("关注歌手", () => qqApi.GetFollowSingersAsync("", 0, 5)),
                     ("粉丝", () => qqApi.GetFansAsync("", 0, 5)),
                 })
        {
            var page = await fetch();
            var first = page.Users.Count > 0 ? page.Users[0].Nickname : "(空)";
            Console.WriteLine($"[user-relations-live] QQ {label} n={page.Users.Count} " +
                              $"total={page.Total} more={page.HasMore} first={first} " +
                              $"euin={page.Users.FirstOrDefault()?.EncryptUin}");
            Check(page.Users.Count > 0, $"QQ {label}非空");
        }
        Console.WriteLine("[user-relations-live] QQ PASS");

        // 用户页头部的关注/粉丝:自己页走账号摘要(主页 fcg creator.nums),
        // 他人页走 GetUserHomepageAsync 同源字段 —— 两处都不能是 0
        var summary = await qqApi.GetAccountSummaryAsync();
        Console.WriteLine($"[user-relations-live] QQ 自己页 follows={summary.Follows} " +
                          $"followeds={summary.Followeds}");
        Check(summary.Follows > 0 && summary.Followeds > 0, "QQ 自己页显示真实关注/粉丝数");

        var sample = (await qqApi.GetFollowUsersAsync("", 0, 1)).Users.FirstOrDefault();
        if (sample is { EncryptUin.Length: > 0 })
        {
            var home = await qqApi.GetUserHomepageAsync(sample.EncryptUin);
            Console.WriteLine($"[user-relations-live] QQ 他人页 {home.Nickname} " +
                              $"follows={home.Follows} followeds={home.Followeds}");
            Check(home.Follows > 0 && home.Followeds > 0, "QQ 他人页显示真实关注/粉丝数");
        }
        else
        {
            Console.WriteLine("[user-relations-live] QQ 关注名单为空,跳过他人页计数");
        }
    }

    private static Window NewWindow(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        window.Show();
        for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button[] Rows(Window window) => window.GetVisualDescendants().OfType<Button>()
        .Where(button => button.Classes.Contains("user-row") && button.IsEffectivelyVisible).ToArray();

    private static void Click(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static void Capture(Window window, string root, string state)
    {
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(root, state + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("[user-relations] " + message);
    }

    /// <summary>假响应:网易云用户详情/歌单/关注/粉丝 + QQ 主页/关系名单。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            string? json = null;
            if (path.StartsWith("/api/v1/user/detail/", StringComparison.Ordinal))
            {
                var id = long.Parse(path["/api/v1/user/detail/".Length..]);
                json = id == 10001
                    ? """{"code":200,"level":7,"profile":{"userId":10001,"nickname":"关系测试","avatarUrl":"","signature":"关系签名","follows":45,"followeds":8}}"""
                    : "{\"code\":200,\"level\":3,\"profile\":{\"userId\":" + id
                      + ",\"nickname\":\"听众" + (id - 20000)
                      + "\",\"avatarUrl\":\"\",\"signature\":\"第" + (id - 20000)
                      + "位\",\"follows\":5,\"followeds\":6}}";
            }
            else if (path == "/api/user/playlist")
            {
                json = """{"code":200,"playlist":[]}""";
            }
            else if (path.StartsWith("/api/user/getfollows/", StringComparison.Ordinal))
            {
                json = Page("follow", Query(uri, "offset"), Query(uri, "limit"), FollowingTotal);
            }
            else if (path == "/api/user/getfolloweds")
            {
                json = Page("followeds", Query(uri, "offset"), Query(uri, "limit"), FollowerTotal);
            }
            else if (path == "/cgi-bin/musicu.fcg")
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                json = Musicu(body);
            }
            else if (path == "/rsc/fcgi-bin/fcg_get_profile_homepage.fcg")
            {
                json = QqHomepage(QueryText(uri, "userid"));
            }

            if (json is null)
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private static int Query(Uri uri, string key)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == key && int.TryParse(parts[1], out var value)) return value;
            }
            return 0;
        }

        private static string QueryText(Uri uri, string key)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == key) return Uri.UnescapeDataString(parts[1]);
            }
            return "";
        }

        private static string QqHomepage(string userid)
        {
            var nick = userid switch
            {
                QqOwnerEuin or QqSelfUin => "QQ 关系测试",
                "qq-follow-1" => "QQ关注甲",
                "qq-follow-2" => "QQ关注乙",
                _ => "QQ 听众 " + userid,
            };
            var isSelf = userid is QqOwnerEuin or QqSelfUin;
            return "{\"code\":0,\"data\":{\"creator\":{\"nick\":\"" + nick
                   + "\",\"encrypt_uin\":\"" + userid
                   + "\",\"nums\":{\"fansnum\":" + (isSelf ? 5 : 7)
                   + ",\"follownum\":" + (isSelf ? 3 : 2) + "}},\"mydiss\":{\"list\":[]}}}";
        }

        /// <summary>QQ musicu:关系名单三份(关注用户 2 / 关注歌手 1 / 粉丝 3)。</summary>
        private static string Musicu(string body)
        {
            if (body.Contains("\"GetFollowUserList\"", StringComparison.Ordinal))
                return RelationPage(
                    "{\"EncUin\":\"qq-follow-1\",\"Name\":\"QQ关注甲\",\"FanNum\":11}," +
                    "{\"EncUin\":\"qq-follow-2\",\"Name\":\"QQ关注乙\",\"FanNum\":12}");
            if (body.Contains("\"GetFollowSingerList\"", StringComparison.Ordinal))
                return RelationPage("{\"MID\":\"qq-singer-1\",\"Name\":\"QQ关注歌手\",\"FanNum\":99}");
            if (body.Contains("\"GetFansList\"", StringComparison.Ordinal))
                return RelationPage(
                    "{\"EncUin\":\"qq-fan-1\",\"Name\":\"QQ粉丝甲\",\"FanNum\":21}," +
                    "{\"EncUin\":\"qq-fan-2\",\"Name\":\"QQ粉丝乙\",\"FanNum\":22}," +
                    "{\"EncUin\":\"qq-fan-3\",\"Name\":\"QQ粉丝丙\",\"FanNum\":23}");
            return """{"code":0,"req_0":{"code":0,"data":{}}}""";
        }

        private static string RelationPage(string items)
            => "{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"Total\":3,\"HasMore\":false,\"LockFlag\":0,\"List\":["
               + items + "]}}}";

        private static string Page(string arrayName, int offset, int limit, int total)
        {
            if (limit <= 0) limit = 30;
            var count = Math.Clamp(total - offset, 0, limit);
            var more = offset + count < total;
            var builder = new StringBuilder();
            builder.Append("{\"code\":200,\"more\":").Append(more ? "true" : "false")
                .Append(",\"").Append(arrayName).Append("\":[");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(',');
                var n = offset + i;
                builder.Append("{\"userId\":").Append(20000 + n)
                    .Append(",\"nickname\":\"听众").Append(n)
                    .Append("\",\"avatarUrl\":\"\",\"signature\":\"第").Append(n)
                    .Append("位的签名\",\"follows\":").Append(n % 7)
                    .Append(",\"followeds\":").Append(n % 13).Append('}');
            }
            builder.Append("]}");
            return builder.ToString();
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline probe"));
    }
}
