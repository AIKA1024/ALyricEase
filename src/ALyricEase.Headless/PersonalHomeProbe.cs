using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
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

/// <summary>离线双平台主页回归：隔离凭证/缓存，验证来源、动态更新、导航、键盘筛选和响应式渲染。</summary>
internal static class PersonalHomeProbe
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aly-personal-home-" + Guid.NewGuid().ToString("N"));
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
        var home = ServiceLocator.Get<PersonalHomeViewModel>();
        var library = ServiceLocator.Get<PlaylistViewModel>();
        var account = home.Account;
        await home.EnsureLoadedAsync();
        Check(home.IsEmpty && !home.IsRefreshing, "无帐号时显示可操作空态");

        library.IsLoggedIn = library.IsQqLoggedIn = true;
        library.UserName = "晚风与海";
        library.QqUserName = "Aika";
        account.NetEase.Username = library.UserName;
        account.Qq.Username = library.QqUserName;
        account.NetEase.UserIdText = "10001";
        account.Qq.UserIdText = "20002";
        account.NetEase.MembershipName = "黑胶 VIP";
        account.Qq.MembershipName = "豪华绿钻";
        account.NetEase.AccountLevelText = "LV 10";
        account.Qq.AccountLevelText = "LV 12";
        typeof(NetEaseApiClient).GetField("_likedPlaylistId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ServiceLocator.Get<NetEaseApiClient>(), 10L);
        library.Playlists.Add(Item(10, "我喜欢的音乐", MusicSource.NetEase, 128));
        library.QqPlaylists.Add(Item(10, "我喜欢", MusicSource.QQ, 86, 201));
        library.Playlists.Add(Item(11, "把日子过成一首歌", MusicSource.NetEase, 42));
        library.Playlists.Add(Item(12, "晚风、耳机和回家的路", MusicSource.NetEase, 36));
        library.QqPlaylists.Add(Item(11, "每个晴天都值得收藏", MusicSource.QQ, 58, 1));
        library.QqPlaylists.Add(Item(12, "写给夜晚的温柔旋律", MusicSource.QQ, 27, 2));
        library.NetEaseCollectedPlaylists.Add(Item(13, "华语精选 · 在音乐里相遇", MusicSource.NetEase, 80));
        library.QqPlaylists.Add(Item(13, "轻音乐 · 留一点时间给自己", MusicSource.QQ, 60));
        await Task.Delay(50);
        Check(home.TastePlaylists.Count == 2 && home.CreatedPlaylists.Count == 4 && home.CollectedPlaylists.Count == 2,
            "同 ID 跨平台歌单均保留，并按喜欢、创建、收藏分组");

        var view = new PersonalHomeView { DataContext = home };
        var window = new Window { Width = 1120, Height = 1040, Content = view };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("AppBackground"));
        window.Show();
        await Task.Delay(150);
        Capture(window, root, "dual-light");
        CheckAccounts(window, stacked: false);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Task.Delay(60);
        Capture(window, root, "dual-dark");
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        foreach (var width in new[] { 700, 390 })
        {
            window.Width = width;
            window.Height = 920;
            await Task.Delay(60);
            CheckAccounts(window, stacked: true);
            var scroller = view.FindControl<ScrollViewer>("PageScroller")!;
            Check(scroller.Extent.Width <= scroller.Viewport.Width + 1, $"{width}px 无横向溢出");
            Capture(window, root, "dual-" + width);
        }
        var originalScroller = view.FindControl<ScrollViewer>("PageScroller")!;
        originalScroller.Offset = new Vector(0, 180);
        await Task.Delay(30);
        window.Content = null;
        home.RestoreScrollPosition();
        view = new PersonalHomeView { DataContext = home };
        window.Content = view;
        await Task.Delay(80);
        Check(Math.Abs(view.FindControl<ScrollViewer>("PageScroller")!.Offset.Y - 180) < 1,
            "离开后重建视图可恢复滚动位置");
        var filter = window.GetVisualDescendants().OfType<ListBox>().Single();
        var filterScroller = view.FindControl<ScrollViewer>("PageScroller")!;
        foreach (var index in new[] { 1, 2, 0 })
        {
            var item = (ListBoxItem)filter.Items[index]!;
            var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
            Check(point.Y > 0 && point.Y < window.ClientSize.Height, "筛选按钮位于可见区域");
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            await Task.Delay(60);
            Check(home.SourceIndex == index, $"点击平台筛选 {index} 生效");
            Check(Math.Abs(filterScroller.Offset.Y - 180) < 1, $"点击平台筛选 {index} 保持滚动位置");
        }
        filter.SelectedIndex = 0;
        ((ListBoxItem)filter.Items[0]!).Focus();
        window.KeyPress(Key.Right, RawInputModifiers.None, default, null);
        window.KeyRelease(Key.Right, RawInputModifiers.None, default, null);
        await Task.Delay(40);
        Check(home.SourceIndex == 1 && home.TastePlaylists.Count == 1
              && home.CreatedPlaylists.All(p => p.Playlist.Source == MusicSource.NetEase), "键盘可切换到网易云筛选并保留正确来源");
        Check(Math.Abs(filterScroller.Offset.Y - 180) < 1, "键盘切换筛选保持滚动位置");
        filter.SelectedIndex = 2;
        await Task.Delay(40);
        Check(home.SourceIndex == 2 && home.CreatedPlaylists.Count == 2 && home.CollectedPlaylists.Count == 1
              && home.CollectedPlaylists[0].Playlist.Source == MusicSource.QQ, "QQ 筛选保留正确来源与分类");
        Check(account.NetEase.IsLoggedIn && account.Qq.IsLoggedIn, "筛选不改变帐号状态");
        filter.SelectedIndex = 0;
        library.Playlists[1] = Item(11, "重命名后的歌单", MusicSource.NetEase, 42);
        library.QqPlaylists.RemoveAt(2);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(40);
        Check(home.CreatedPlaylists.Any(p => p.Name == "重命名后的歌单") && home.CreatedPlaylists.Count == 3,
            "歌单重命名与删除实时同步");

        library.QqLibraryError = "QQ 音乐同步失败，请稍后刷新。";
        Check(home.HasError && home.TastePlaylists.Any(p => p.Playlist.Source == MusicSource.NetEase),
            "单个平台失败不隐藏另一平台内容");
        library.QqLibraryError = null;
        var longName = account.NetEase.Username;
        account.NetEase.Username = "这是一个用来验证布局不会溢出的很长很长的音乐帐号昵称";
        window.Width = 320;
        await Task.Delay(50);
        var narrowScroller = view.FindControl<ScrollViewer>("PageScroller")!;
        Check(narrowScroller.Extent.Width <= narrowScroller.Viewport.Width + 1, "320px 长昵称无横向溢出");
        Capture(window, root, "long-name-320");
        account.NetEase.Username = longName;
        library.IsQqLoggedIn = false;
        window.Width = 1000;
        await Task.Delay(50);
        Capture(window, root, "netease-only");
        library.IsQqLoggedIn = true;
        library.LogoutNetEase();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(40);
        Check(home.TastePlaylists.Count == 1 && home.CollectedPlaylists.Count == 1
              && home.CreatedPlaylists.All(p => p.Playlist.Source == MusicSource.QQ), "退出网易云同时清理其创建和收藏歌单");
        window.Width = 1000;
        Capture(window, root, "qq-only");
        library.LogoutQq();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(40);
        Check(home.IsEmpty && !account.HasAnyLogin, "双退出回到登录空态");
        Capture(window, root, "signed-out");

        var main = ServiceLocator.Get<MainViewModel>();
        await main.OpenUserCommand.ExecuteAsync(null);
        Check(main.ActivePage == "PersonalHome" && ReferenceEquals(main.CurrentContent, home), "自己的主页入口导航到跨平台页");
        main.GoAccountCommand.Execute(null);
        main.GoBackCommand.Execute(null);
        Check(main.ActivePage == "PersonalHome", "管理帐号返回跨平台页");
        var loginButtons = window.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, "登录") && b.IsEffectivelyVisible).ToArray();
        Check(loginButtons.Length == 2, "两个未登录帐号均有登录入口");
        loginButtons[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(main.IsLoginDialogOpen && library.IsQQLoginTab, "QQ 登录按钮定位正确平台");
        main.CloseLoginDialogCommand.Execute(null);
        window.Close();

        var summary = new AccountPlatformViewModel("测试");
        summary.SetLoginState(true);
        var pending = new TaskCompletionSource<MusicAccountSummary>();
        var load = summary.LoadAsync(_ => pending.Task, CancellationToken.None);
        summary.SetLoginState(false);
        summary.SetLoginState(true, "新帐号");
        pending.SetResult(new MusicAccountSummary { Nickname = "旧帐号" });
        await load;
        Check(summary.Username == "新帐号", "迟到的帐号资料不会覆盖退出后重新登录的帐号");
        Console.WriteLine($"[personal-home] PASS; previews: {root}");
    }

    private static PlaylistItemViewModel Item(long id, string name, MusicSource source, int count, long dirId = 0)
        => new(new Playlist { Id = id, Name = name, Source = source, TrackCount = count, DirId = dirId });

    private static void CheckAccounts(Window window, bool stacked)
    {
        var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("identity-card") && b.IsEffectivelyVisible).Take(2).ToArray();
        Check(cards.Length == 2, "两个帐号始终可见");
        var a = cards[0].TranslatePoint(default, window)!.Value;
        var b = cards[1].TranslatePoint(default, window)!.Value;
        Check(stacked ? b.Y > a.Y + cards[0].Bounds.Height : Math.Abs(a.Y - b.Y) < 1,
            stacked ? "窄屏帐号卡片纵向排列" : "宽屏帐号卡片并排排列");
    }

    private static void Capture(Window window, string root, string state)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(root, state + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("[personal-home] " + message);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline probe"));
    }
}
