using System.Net;
using System.Reflection;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Headless;

/// <summary>隔离账号/缓存的 QQ 用户页失败、返回栈及模态交互回归，不访问真实网络。</summary>
internal static class QqUserFailureProbe
{
    private const string NativeCookie = "uin=123456789; qqmusic_key=probe-key; tmeLoginType=6";

    internal static MainViewModel CreateOfflineMain(string root) => CreateMain(root, new ResponseHandler());

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-qq-user-failure-{Guid.NewGuid():N}");
        var handler = new ResponseHandler();
        var main = CreateMain(root, handler);
        Check(main.ActivePage == "Account" && !main.IsLoginDialogOpen, "无账号启动仅显示账户页");
        var api = ServiceLocator.Get<QQMusicApiClient>();
        api.SetCookie(NativeCookie);
        main.ActivePage = "Settings";
        await main.OpenQqUserCommand.ExecuteAsync(null);
        Check(main.ActivePage == "User", "凭证失效显示提示时仍停留在用户页");
        Check(main.IsErrorDialogOpen && main.ErrorDialogMessage.Contains("重新登录")
              && main.ErrorDialogMessage.Contains("凭证已过期"), "模态窗口显示失效原因");
        Check(!main.UserProfile.IsLoading, "失败页结束加载");

        var dialog = new ErrorDialogView { DataContext = main, IsVisible = false };
        var window = new Window { Width = 820, Height = 640, Content = dialog };
        window.Show();
        ModalVisibilityTransition.SetIsOpen(dialog, true);
        await ModalVisibilityTransition.WaitForTransitionAsync(dialog);
        Dispatcher.UIThread.RunJobs();
        Check(window.FocusManager?.GetFocusedElement() == dialog.FindControl<Button>("ConfirmButton"),
            "打开后聚焦确认按钮");
        for (var i = 0; i < 4; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Check(window.FocusManager?.GetFocusedElement() is Control focused && dialog.IsVisualAncestorOf(focused),
                "Tab 焦点留在模态层");
        }
        Capture(window, dialog, "desktop");
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Capture(window, dialog, "dark");
        window.Width = 360;
        Capture(window, dialog, "narrow");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Check(!main.IsErrorDialogOpen && main.ActivePage == "Settings", "Enter 关闭提示后才返回上一页");
        Check(!main.UserProfile.HasRetainedPageData, "返回后释放失败用户页内容");
        window.Close();

        await main.OpenQqUserCommand.ExecuteAsync(null);
        Check(main.ActivePage == "User" && main.IsErrorDialogOpen, "再次失败仍等待用户关闭提示");
        Check(main.TryHandleBack() && !main.IsErrorDialogOpen && main.ActivePage == "Settings",
            "系统返回关闭提示后只回退一页");
        main.CloseErrorDialogCommand.Execute(null);
        Check(main.ActivePage == "Settings", "重复关闭不会多退一页");
        main.GoBackCommand.Execute(null);
        Check(main.ActivePage == "Account", "失败用户页没有残留在返回栈");

        api.SetCookie(NativeCookie);
        handler.Error = new HttpRequestException("probe network unavailable");
        main.ActivePage = "Settings";
        await main.OpenQqUserCommand.ExecuteAsync(null);
        Check(main.ActivePage == "User" && main.ErrorDialogMessage.Contains("检查网络")
              && main.ErrorDialogMessage.Contains("probe network unavailable"), "网络失败显示具体原因并等待确认");
        main.CloseErrorDialogCommand.Execute(null);
        Check(main.ActivePage == "Settings", "确认关闭网络错误提示后返回上一页");

        api.SetCookie(NativeCookie);
        handler.Error = null;
        handler.Pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = main.OpenQqUserCommand.ExecuteAsync(null);
        Check(main.UserProfile.IsLoading, "重新进入设置加载状态");
        main.ActivePage = "Search";
        handler.Pending.SetResult(ResponseHandler.ExpiredResponse());
        await load;
        Check(main.ActivePage == "Search" && !main.IsErrorDialogOpen, "切走页面后迟到的失败不会弹窗或导航");

        var startupRoot = Path.Combine(root, "startup");
        var startup = CreateMain(startupRoot, new ResponseHandler(), storedCredential: true);
        await startup.PersonalHome.EnsureLoadedAsync();
        Check(startup.ActivePage == "PersonalHome" && !startup.IsErrorDialogOpen && !startup.CanGoBack,
            "启动凭证失效仍停留在跨平台主页，不阻断其他平台");
        Check(!startup.Account.Qq.IsLoggedIn && startup.PersonalHome.HasError,
            "失效平台显示重新登录入口和同步错误");
        Console.WriteLine("[qq-user-failure] PASS");
    }

    private static MainViewModel CreateMain(string root, ResponseHandler handler, bool storedCredential = false)
    {
        var cookie = new CookieStore(Path.Combine(root, "cookie.json"))
        {
            QQCookieRaw = storedCredential ? NativeCookie : null,
        };
        HeadlessApp.ConfigureServices(services =>
        {
            services.AddSingleton(cookie);
            services.AddSingleton(new AppStateStore(Path.Combine(root, "state.json")));
            services.AddSingleton(new MusicCacheService(64, Path.Combine(root, "cache"), new HttpClient(handler)));
            services.AddSingleton(sp =>
            {
                var ne = new NetEaseApiClient(sp.GetRequiredService<CryptoService>(),
                    sp.GetRequiredService<CnIpPool>(), cookie);
                ReplaceField(ne, "_http", new HttpClient(new ResponseHandler()));
                return ne;
            });
            services.AddSingleton(_ =>
            {
                var qq = new QQMusicApiClient(cookie);
                ReplaceField(qq, "_http", new HttpClient(handler));
                ReplaceField(qq, "_qrLogin", new QQMusicQrLoginService(cookie, new HttpClient(handler)));
                return qq;
            });
            services.AddSingleton(sp =>
            {
                var recommend = new RecommendViewModel(sp.GetRequiredService<NetEaseApiClient>(),
                    sp.GetRequiredService<QQMusicApiClient>(), sp.GetRequiredService<DispatcherService>(),
                    sp.GetRequiredService<PlayerViewModel>());
                ReplaceField(recommend, "_loading", true);
                return recommend;
            });
        });
        return ServiceLocator.Get<MainViewModel>();
    }

    private static void ReplaceField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (field.GetValue(target) is IDisposable oldValue) oldValue.Dispose();
        field.SetValue(target, value);
    }

    private static void Capture(Window window, ErrorDialogView dialog, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var card = dialog.FindControl<Border>("DialogCard")!;
        var position = card.TranslatePoint(new Point(), window)!.Value;
        Check(position.X >= 0 && position.Y >= 0 && position.X + card.Bounds.Width <= window.ClientSize.Width,
            $"{name} 卡片在窗口内");
        var dir = Path.Combine(Path.GetTempPath(), "ALyricEase-qq-user-failure-preview");
        Directory.CreateDirectory(dir);
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height), new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(dir, $"{name}.png");
        bitmap.Save(path);
        Console.WriteLine($"[qq-user-failure] preview: {path}");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException($"[qq-user-failure] FAIL {name}");
        Console.WriteLine($"[qq-user-failure] PASS {name}");
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public Exception? Error { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? Pending { get; set; }

        public static HttpResponseMessage ExpiredResponse() => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"code":0,"req_0":{"code":104401}}"""),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Pending?.Task ?? (Error is { } error
                ? Task.FromException<HttpResponseMessage>(error) : Task.FromResult(ExpiredResponse()));
    }
}
