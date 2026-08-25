using System;
using Avalonia;
using Avalonia.Controls;
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
}
