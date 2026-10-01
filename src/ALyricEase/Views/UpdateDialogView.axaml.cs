using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>应用更新弹窗视图:打开(IsVisible=true)时先置 0 下一帧再置 1 触发淡入
/// (IsVisible 翻转本身不跑过渡)。确认 → 下载 → 待重启三态见 SettingsViewModel。</summary>
public partial class UpdateDialogView : UserControl
{
    public UpdateDialogView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Root.Opacity = 0;
                Dispatcher.UIThread.Post(() => Root.Opacity = 1, DispatcherPriority.Render);
            }
        };
    }
}
