using Avalonia.Controls;

namespace ALyricEase.Views;

/// <summary>清除缓存确认对话框视图；显隐动画由宿主统一协调。
/// 确认后弹窗内切 ProgressRing 等待态，清理完成自动关闭。</summary>
public partial class ClearCacheDialogView : UserControl
{
    public ClearCacheDialogView()
    {
        InitializeComponent();
    }
}
