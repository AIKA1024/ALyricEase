using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ALyricEase.Infrastructure;

namespace ALyricEase.ViewModels;

/// <summary>占位页(推荐/播放记录/收藏歌曲/账号/设置):标题 + 描述。</summary>
public sealed partial class PlaceholderViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";

    [ObservableProperty] private string _description = "功能开发中";

    /// <summary>账号页显示"删除本地 Cookie"按钮(仅账号页为 true)。</summary>
    [ObservableProperty] private bool _showLogout;

    /// <summary>临时调试按钮:删除本地 Cookie,回到未登录状态并返回首页。</summary>
    [RelayCommand]
    private void Logout()
    {
        ServiceLocator.Get<PlaylistViewModel>().Logout();
        ServiceLocator.Get<MainViewModel>().ActivePage = "Recommend";
    }
}
