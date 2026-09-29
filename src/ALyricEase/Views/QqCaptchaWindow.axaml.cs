using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>
/// QQ 手机号登录的安全验证(腾讯防水墙)模态窗口。
/// 内嵌原生 WebView 加载风控返回的 securityURL;轮询页面检测"验证成功",
/// 检测到(或用户点主按钮)后通过 <see cref="Verified"/> 通知宿主重新发送验证码。
/// WebView 环境万一被风控拒绝,保留"在系统浏览器中打开"兜底。
/// </summary>
public partial class QqCaptchaWindow : Window
{
    private Uri _url;
    private bool _verified;
    private readonly DispatcherTimer _pollTimer;
    /// <summary>用户完成验证(自动检测命中或手动确认),宿主应重新执行发送验证码。</summary>
    public event EventHandler? Verified;

    /// <summary>XAML 运行时加载器要求公共无参构造;实际创建请使用 <see cref="QqCaptchaWindow(Uri)" />。</summary>
    public QqCaptchaWindow() : this(new Uri("about:blank"))
    {
    }

    public QqCaptchaWindow(Uri url)
    {
        InitializeComponent();
        _url = url;
        // Source 在适配器就绪前设置可能被忽略,AdapterCreated 后再补一次导航
        WebView.AdapterCreated += (_, _) => WebView.Navigate(_url);
        WebView.NavigationCompleted += OnNavigationCompleted;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _pollTimer.Tick += (_, _) => _ = PollSuccessAsync();
    }

    /// <summary>重发又触发 20276 时复用已打开的窗口,换新的验证会话地址。</summary>
    public void UpdateCaptchaUrl(Uri url)
    {
        _url = url;
        _verified = false;
        StatusText.Text = "验证会话已更新，请重新完成滑块验证。";
        WebView.Navigate(url);
    }

    private void OnNavigationCompleted(object? sender, EventArgs e) => _ = CheckSuccessOnceAsync();

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
        if (!await QqCaptchaProbe.IsSuccessAsync(WebView))
            return;
        _verified = true;
        _pollTimer.Stop();
        StatusText.Text = "检测到验证完成，正在重新发送验证码…";
        Verified?.Invoke(this, EventArgs.Empty);
        await Task.Delay(900); // 给用户一瞬的成功反馈再关闭
        Close();
    }

    private void OnResendClick(object? sender, RoutedEventArgs e)
    {
        if (_verified)
            return;
        _verified = true;
        _pollTimer.Stop();
        StatusText.Text = "正在重新发送验证码…";
        Verified?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private async void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(_url);
    }
}
