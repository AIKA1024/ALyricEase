using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>Fluent 2 平台选择模态层；显隐动画由宿主统一协调，打开后聚焦当前选项。</summary>
public partial class LikeSourceDialogView : UserControl
{
    public LikeSourceDialogView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || !IsVisible) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || !IsEnabled) return;
                var source = (DataContext as MainViewModel)?.LikeSourceDialog.SelectedSource;
                (source == MusicSource.QQ ? QqChoice : NetEaseChoice).Focus();
            }, DispatcherPriority.Loaded);
        };
    }
}
