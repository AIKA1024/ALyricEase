using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class PlayerBarView : UserControl
{
    private const double RightVisibleThreshold = 760;

    public PlayerBarView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>点封面/曲目区 → 打开正在播放全屏页。</summary>
    private void OnTrackAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (this.FindAncestorOfType<Window>()?.DataContext is MainViewModel vm && !vm.ShowNowPlaying)
            vm.OpenNowPlayingCommand.Execute(null);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // 窄窗隐藏右侧按钮组(喜欢/播放模式/音量/播放列表),避免挤占中间控制区
        var show = e.NewSize.Width >= RightVisibleThreshold;
        if (RightButtons.IsVisible != show) RightButtons.IsVisible = show;
    }

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm) vm.BeginScrub();
    }

    private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm) vm.EndScrub();
    }
}
