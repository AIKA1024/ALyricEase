using System;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>登录对话框视图；显隐动画由宿主统一协调，打开后聚焦当前登录方式的首要控件。</summary>
public partial class LoginDialogView : UserControl
{
    private PlaylistViewModel? _subscribedPlaylist;
    private QqCaptchaWindow? _captchaWindow;
    private QqCaptchaOverlay? _captchaOverlay;

    public LoginDialogView()
    {
        InitializeComponent();
        // 宽窄屏布局全部由 axaml 容器查询(Root 为容器,max-width:760 切换堆叠)表达,
        // 这里不再做任何响应式切换。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Dispatcher.UIThread.Post(FocusActiveInput, DispatcherPriority.Loaded);
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeQqCaptcha();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // 绑定推送 DataContext 可能晚于可视树附着,两处都订阅保证不漏
        SubscribeQqCaptcha();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        HideCaptchaOverlay();
        UnsubscribeQqCaptcha();
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeQqCaptcha()
    {
        var playlist = (DataContext as MainViewModel)?.Playlist;
        if (ReferenceEquals(playlist, _subscribedPlaylist))
            return;
        UnsubscribeQqCaptcha();
        if (playlist is null)
            return;
        playlist.QqCaptchaOpenRequested += OnQqCaptchaOpenRequested;
        _subscribedPlaylist = playlist;
    }

    private void UnsubscribeQqCaptcha()
    {
        if (_subscribedPlaylist is null)
            return;
        _subscribedPlaylist.QqCaptchaOpenRequested -= OnQqCaptchaOpenRequested;
        _subscribedPlaylist = null;
    }

    /// <summary>
    /// QQ 音乐风控(20276):桌面弹模态验证窗口;其余情况(安卓/异常宿主)一律内嵌覆盖层。
    /// 浏览器只作为覆盖层里的手动兜底按钮,不再自动打开。
    /// </summary>
    private async void OnQqCaptchaOpenRequested(object? sender, EventArgs e)
    {
        if (sender is not PlaylistViewModel playlist || string.IsNullOrEmpty(playlist.QqCaptchaUrl))
            return;
        var uri = new Uri(playlist.QqCaptchaUrl, UriKind.Absolute);

        if (!OperatingSystem.IsAndroid())
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is Window owner)
            {
                // 发送验证码再次被拦截时,复用已打开的窗口换新会话,避免叠窗
                if (_captchaWindow is { } existing)
                {
                    existing.UpdateCaptchaUrl(uri);
                    return;
                }
                var window = new QqCaptchaWindow(uri);
                window.Verified += (_, _) => playlist.SendQqPhoneCodeCommand.Execute(null);
                window.Closed += (_, _) => _captchaWindow = null;
                _captchaWindow = window;
                await window.ShowDialog(owner);
                return;
            }
        }
        ShowCaptchaOverlay(uri, playlist);
    }

    private void ShowCaptchaOverlay(Uri uri, PlaylistViewModel playlist)
    {
        if (_captchaOverlay is { } existing)
        {
            existing.UpdateCaptchaUrl(uri);
            return;
        }
        var overlay = new QqCaptchaOverlay(uri);
        overlay.Verified += (_, _) => playlist.SendQqPhoneCodeCommand.Execute(null);
        overlay.CloseRequested += (_, _) => HideCaptchaOverlay();
        _captchaOverlay = overlay;
        CaptchaHost.Children.Add(overlay);
        CaptchaHost.IsVisible = true;
    }

    private void HideCaptchaOverlay()
    {
        if (_captchaOverlay is null)
            return;
        _captchaOverlay = null;
        CaptchaHost.Children.Clear();
        CaptchaHost.IsVisible = false;
    }

    private void FocusActiveInput()
    {
        if (!IsVisible || !IsEnabled || !ModalVisibilityTransition.GetIsOpen(this))
            return;

        var vm = DataContext as MainViewModel;
        var playlist = vm?.Playlist;
        var target = playlist switch
        {
            // 网易云代理方式的首要控件是启动按钮;Cookie 方式才是输入框(此时它可见)。
            { IsNetEaseProxyMethod: true } => (Control)NetEaseStartProxyButton,
            { IsQQLoginTab: false } => MusicUBox,
            { IsQqPhoneLoginMethod: true } => QqPhoneBox,
            { IsQqCookieLoginMethod: true } => QQBox,
            _ => QqQrButton,
        };
        target.Focus();
    }

    private async void OnPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox target)
            return;

        // Avalonia 的长按菜单与 Android InputConnection.performContextMenuAction(Paste)
        // 最终都会触发此事件；接管后可在标准读取为空时使用车机兼容后备。
        e.Handled = true;
        await PasteClipboardIntoAsync(target);
    }

    private async Task PasteClipboardIntoAsync(TextBox target)
    {
        // SelectedText 保留 TextBox 自身的选区替换、单行过滤和绑定更新语义。
        var text = await ClipboardService.TryGetTextAsync();
        var playlist = (DataContext as MainViewModel)?.Playlist;
        if (string.IsNullOrEmpty(text))
        {
            if (playlist is not null)
                playlist.Message = "剪贴板中没有可粘贴的文本，请先在车机上复制 Cookie";
            return;
        }

        target.Focus();
        target.SelectedText = text;
        if (playlist is not null)
            playlist.Message = null;
    }
}
