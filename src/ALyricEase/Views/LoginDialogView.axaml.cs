using System;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>登录对话框视图:打开(IsVisible=true)时先置 0 下一帧再置 1 触发淡入,
 /// 并聚焦当前音源页签的 Cookie 输入框(IsVisible 翻转本身不跑过渡)。</summary>
public partial class LoginDialogView : UserControl
{
    public LoginDialogView()
    {
        InitializeComponent();
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
        var target = (vm?.Playlist.IsQQLoginTab ?? false) ? QQBox : MusicUBox;
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
