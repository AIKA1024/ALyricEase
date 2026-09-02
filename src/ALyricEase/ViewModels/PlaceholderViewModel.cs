using CommunityToolkit.Mvvm.ComponentModel;

namespace ALyricEase.ViewModels;

/// <summary>尚未实现的导航页面占位模型：标题与说明。</summary>
public sealed partial class PlaceholderViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";

    [ObservableProperty] private string _description = "功能开发中";

}
