using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>登录对话框视图：打开时淡入，并把焦点放到当前登录方式的首要控件。</summary>
public partial class LoginDialogView : UserControl
{
    public LoginDialogView()
    {
        InitializeComponent();
        // 宽窄屏布局全部由 axaml 容器查询(Root 为容器,max-width:760 切换堆叠)表达,
        // 这里不再做任何响应式切换。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Root.Opacity = 0;
                Dispatcher.UIThread.Post(FocusActiveInput, DispatcherPriority.Render);
            }
        };
    }

    private void FocusActiveInput()
    {
        Root.Opacity = 1;
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
