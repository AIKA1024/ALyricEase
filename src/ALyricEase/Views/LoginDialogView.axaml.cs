using System;
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
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Root.Opacity = 0;
                Dispatcher.UIThread.Post(FocusActiveInput, DispatcherPriority.Render);
            }
        };
    }

    private void ApplyResponsiveLayout(Size size)
    {
        var compact = size.Width < 760;
        Root.Classes.Set("compact", compact);
        DialogCard.Width = Math.Max(320, Math.Min(760, size.Width - 32));
        DialogCard.MaxHeight = Math.Max(440, Math.Min(680, size.Height - 32));
        QqLayout.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "178,*");
        QqLayout.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*");
        NetEaseFields.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "1.15*,0.85*");
        NetEaseFields.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto" : "*");
    }

    private void FocusActiveInput()
    {
        Root.Opacity = 1;
        var vm = DataContext as MainViewModel;
        var playlist = vm?.Playlist;
        var target = playlist switch
        {
            { IsQQLoginTab: false } => (Control)MusicUBox,
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
