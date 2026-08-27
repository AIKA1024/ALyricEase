using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>聚合歌单设置对话框视图:打开(IsVisible=true)时先置 0 下一帧再置 1 触发淡入
 /// (IsVisible 翻转本身不跑过渡)。</summary>
public partial class AggregateSettingsDialogView : UserControl
{
    public AggregateSettingsDialogView()
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
