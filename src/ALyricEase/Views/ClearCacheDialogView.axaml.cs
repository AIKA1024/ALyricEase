using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>清除缓存确认对话框视图:打开(IsVisible=true)时先置 0 下一帧再置 1 触发淡入
/// (IsVisible 翻转本身不跑过渡)。确认后弹窗内切 ProgressRing 等待态,清理完成自动关闭。</summary>
public partial class ClearCacheDialogView : UserControl
{
    public ClearCacheDialogView()
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
