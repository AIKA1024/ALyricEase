using CommunityToolkit.Mvvm.ComponentModel;

namespace ALyricEase.ViewModels;

/// <summary>尚未实现的导航页面占位模型：标题与说明。
/// 现只剩"未注册的页面键"会落到这里(最后一个真实占位页「浏览」已实现),
/// 所以默认文案必须自带标题,不能等调用方来设置。</summary>
public sealed partial class PlaceholderViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "尚未开放";

    [ObservableProperty] private string _description = "功能开发中";

}
