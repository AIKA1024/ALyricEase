using Avalonia.Controls;

namespace ALyricEase.Views;

/// <summary>应用更新弹窗视图；显隐动画由宿主统一协调。
/// 确认 → 下载 → 待重启三态见 SettingsViewModel。</summary>
public partial class UpdateDialogView : UserControl
{
    public UpdateDialogView()
    {
        InitializeComponent();
    }
}
