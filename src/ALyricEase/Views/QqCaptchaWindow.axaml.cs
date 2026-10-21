using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>
/// QQ 手机号登录 / 网易云扫码的安全验证窗口。
/// 内嵌原生 WebView 加载风控返回的地址;QQ 侧轮询页面检测"验证成功"后重发验证码,
/// 网易云侧由用户点按钮重新生成二维码。
/// </summary>
public partial class QqCaptchaWindow : Window
{
    /// <summary>网易云验证页只在自家 App 的 WebView 里渲染滑块(页面判断 UA 里的 NeteaseMusic/版本号),
    /// 否则提示"请在网易云音乐app内打开本页面"并跳官网。
    /// 必须用**桌面** UA:Android UA 会让页面的 JSBridge 走 `window.prompt(json)` 通道,
    /// 在 WebView2 里弹出原始 JSON 输入框;桌面分支的桥是空实现,调用被静默丢弃。</summary>
    private const string NetEaseAppUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/125.0.0.0 Safari/537.36 NeteaseMusic/9.1.65";

    private Uri _url;
    private bool _verified;
    private readonly bool _isNetEase;
    private readonly IReadOnlyDictionary<string, string>? _cookies;
    private readonly DispatcherTimer _pollTimer;
    /// <summary>用户完成验证(自动检测命中或手动确认),宿主应重新执行发送验证码。</summary>
    public event EventHandler? Verified;

    /// <summary>XAML 运行时加载器要求公共无参构造;实际创建请使用 <see cref="QqCaptchaWindow(Uri, bool, IReadOnlyDictionary{string, string}?)" />。</summary>
    public QqCaptchaWindow() : this(new Uri("about:blank"))
    {
    }

    public QqCaptchaWindow(Uri url, bool isNetEase = false,
        IReadOnlyDictionary<string, string>? cookies = null)
    {
        InitializeComponent();
        _url = url;
        _isNetEase = isNetEase;
        _cookies = cookies;
        if (isNetEase)
        {
            Title = "网易云音乐安全验证";
            StatusText.Text = "拖动滑块完成验证，然后点击下方按钮重新生成二维码。";
            ResendButton.Content = "完成验证，重新生成二维码";
            WebView.UserAgent = NetEaseAppUserAgent;
        }
        // Source 在适配器就绪前设置可能被忽略,AdapterCreated 后再补一次导航
        WebView.AdapterCreated += (_, _) =>
        {
            // 适配器就绪后再设一次 UA:部分平台的实现只在适配器存在时应用该属性。
            if (_isNetEase) WebView.UserAgent = NetEaseAppUserAgent;
            SeedCookies();
            WebView.Navigate(_url);
        };
        WebView.NavigationCompleted += OnNavigationCompleted;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _pollTimer.Tick += (_, _) => _ = PollSuccessAsync();
    }

    /// <summary>把主客户端的 deviceId/MUSIC_A 塞进 WebView:验证结果按设备身份归属,
    /// 两边不一致时滑块通过了主客户端仍会被 -462。Cookie 管理器在适配器就绪前不可用。</summary>
    private void SeedCookies()
    {
        if (_cookies is not { Count: > 0 } cookies) return;
        try
        {
            if (WebView.TryGetCookieManager() is not { } manager) return;
            foreach (var (name, value) in cookies)
            {
                manager.AddOrUpdateCookie(new Cookie(name, value)
                {
                    Domain = ".music.163.com",
                    Path = "/",
                    Secure = true,
                });
            }
        }
        catch
        {
            // 种 cookie 只是提高成功率,失败不阻断验证页展示。
        }
    }

    /// <summary>重发又触发 20276 时复用已打开的窗口,换新的验证会话地址。</summary>
    public void UpdateCaptchaUrl(Uri url)
    {
        _url = url;
        _verified = false;
        StatusText.Text = "验证会话已更新，请重新完成滑块验证。";
        WebView.Navigate(url);
    }

    private void OnNavigationCompleted(object? sender, EventArgs e)
    {
        if (!_isNetEase) _ = CheckSuccessOnceAsync();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _pollTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        base.OnClosed(e);
    }

    private async Task CheckSuccessOnceAsync()
    {
        // 滑块成功后页面有短暂过渡,稍候先查一次,失败则交给轮询兜底
        await Task.Delay(600);
        if (!_verified)
            await PollSuccessAsync();
    }

    private async Task PollSuccessAsync()
    {
        if (_verified)
            return;
        // 网易云验证页初始文案就含"验证成功后…",文本探测会误判成功;这里只探测"失败":
        // WebView2 里的环境指纹过不了它的校验,会稳定显示"验证失败，环境校验异常"。
        if (_isNetEase)
        {
            await CheckNetEaseEnvFailureAsync();
            return;
        }
        if (!await QqCaptchaProbe.IsSuccessAsync(WebView))
            return;
        _verified = true;
        _pollTimer.Stop();
        StatusText.Text = "检测到验证完成，正在重新发送验证码…";
        Verified?.Invoke(this, EventArgs.Empty);
        await Task.Delay(900); // 给用户一瞬的成功反馈再关闭
        Close();
    }

    /// <summary>网易云验证页对运行环境有指纹校验(是否真在客户端内),WebView2 里滑完也只会
    /// "验证失败，环境校验异常"。识别到就直说,别让用户反复拖滑块。</summary>
    private async Task CheckNetEaseEnvFailureAsync()
    {
        string? text;
        try
        {
            text = await WebView.InvokeScript("document.body.innerText");
        }
        catch
        {
            return;
        }
        if (text is null || (!text.Contains("环境校验异常") && !text.Contains("验证失败")))
            return;

        _pollTimer.Stop();
        StatusText.Text = "该验证只能在网易云官方 App 内完成（环境校验异常）。" +
            "请关闭本窗口，改用「官方客户端代理登录」或更换网络后重试。";
        ResendButton.IsEnabled = false;
    }

    private void OnResendClick(object? sender, RoutedEventArgs e)
    {
        if (_verified)
            return;
        _verified = true;
        _pollTimer.Stop();
        StatusText.Text = _isNetEase ? "正在重新生成二维码…" : "正在重新发送验证码…";
        Verified?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private async void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(_url);
    }
}
