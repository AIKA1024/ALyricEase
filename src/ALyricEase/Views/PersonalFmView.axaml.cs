using Avalonia.Controls;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>私人FM页:绑定 PlayerViewModel(FM 无限流在播放器内实现)。
/// 进入页面时若 FM 未激活则自动拉起(幂等;未登录由 VM 提示)。</summary>
public partial class PersonalFmView : UserControl
{
    public PersonalFmView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is PlayerViewModel vm)
                _ = vm.EnsurePersonalFmStartedAsync();
        };
    }
}
