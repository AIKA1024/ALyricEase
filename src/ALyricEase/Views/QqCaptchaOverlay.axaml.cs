using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>
/// QQ 手机号登录的安全验证(腾讯防水墙)覆盖层，用于安卓等无窗口平台。
/// 盖在登录卡片上层，内嵌原生 WebView 加载 securityURL;检测到验证成功(或用户点主按钮)
/// 后通过 <see cref="Verified"/> 通知宿主重新发送验证码。
/// </summary>
public partial class QqCaptchaOverlay : UserControl
{
    private Uri _url;
    private bool _verified;
    private readonly DispatcherTimer _pollTimer;

    /// <summary>用户完成验证(自动检测命中或手动确认)，宿主应重新执行发送验证码。</summary>
    public event EventHandler? Verified;

    /// <summary>用户点 ✕ 或验证流程结束后请求宿主移除覆盖层。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>XAML 运行时加载器要求公共无参构造；实际创建请使用 <see cref="QqCaptchaOverlay(Uri)" />。</summary>
    public QqCaptchaOverlay() : this(new Uri("about:blank"))
    {
    }

    public QqCaptchaOverlay(Uri url)
    {
        InitializeComponent();
        _url = url;
        // Source 在适配器就绪前设置可能被忽略，AdapterCreated 后再补一次导航
        WebView.AdapterCreated += (_, _) => WebView.Navigate(_url);
        WebView.NavigationCompleted += OnNavigationCompleted;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _pollTimer.Tick += (_, _) => _ = PollSuccessAsync();
    }

    /// <summary>重发又触发 20276 时复用已打开的覆盖层，换新的验证会话地址。</summary>
    public void UpdateCaptchaUrl(Uri url)
    {
        _url = url;
        _verified = false;
        StatusText.Text = "验证会话已更新，请重新完成滑块验证。";
        WebView.Navigate(url);
    }

    private void OnNavigationCompleted(object? sender, EventArgs e) => _ = CheckSuccessOnceAsync();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _pollTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pollTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private async Task CheckSuccessOnceAsync()
    {
        // 滑块成功后页面有短暂过渡，稍候先查一次，失败则交给轮询兜底
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
        await Task.Delay(900); // 给用户一瞬的成功反馈再收起
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnResendClick(object? sender, RoutedEventArgs e)
    {
        if (_verified)
            return;
        _verified = true;
        _pollTimer.Stop();
        StatusText.Text = "正在重新发送验证码…";
        Verified?.Invoke(this, EventArgs.Empty);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private async void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(_url);
    }
}
