using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Views;

/// <summary>创建歌单对话框视图；显隐动画由宿主统一协调，打开后聚焦名称输入框。</summary>
public partial class CreatePlaylistDialogView : UserControl
{
    public CreatePlaylistDialogView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsVisible && IsEnabled)
                        NameBox.Focus();
                }, DispatcherPriority.Loaded);
            }
        };
    }
}
