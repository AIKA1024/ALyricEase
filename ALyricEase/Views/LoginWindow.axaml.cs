using System.ComponentModel;
using Avalonia.Controls;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>登录窗口:粘贴 MUSIC_U Cookie 登录(密码/验证码/扫码易被风控,只做 Cookie)。
/// 绑定单例 PlaylistViewModel,登录成功(IsLoggedIn)自动关闭。</summary>
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        DataContext = ServiceLocator.Get<PlaylistViewModel>();
        if (DataContext is PlaylistViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            Closed += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistViewModel.IsLoggedIn) && sender is PlaylistViewModel { IsLoggedIn: true })
            Close();
    }
}
