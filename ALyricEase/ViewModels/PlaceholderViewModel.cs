using CommunityToolkit.Mvvm.ComponentModel;

namespace ALyricEase.ViewModels;

/// <summary>占位页(推荐/播放记录/收藏歌曲/账号/设置):标题 + 描述。</summary>
public sealed partial class PlaceholderViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";

    [ObservableProperty] private string _description = "功能开发中";
}
