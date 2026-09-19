using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>音频输出设备选择对话框视图:打开(IsVisible=true)时先置 0 下一帧再置 1 触发淡入
/// (IsVisible 翻转本身不跑过渡)。</summary>
public partial class AudioDeviceDialogView : UserControl
{
    public AudioDeviceDialogView()
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
