using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class PlayerBarView : UserControl
{
    public PlayerBarView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += (_, _) => ResponsiveClasses.Apply(this, Bounds.Width);
    }

    /// <summary>点封面/曲目区 → 打开正在播放页；按钮事件已处理时不重复触发。</summary>
    private void OnTrackAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        // Grid 处理触摸/鼠标事件时，播放按钮会冒泡；但歌曲区域（含封面）仍应打开详情。
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;

        if (this.FindAncestorOfType<Window>()?.DataContext is MainViewModel vm && !vm.ShowNowPlaying)
            vm.OpenNowPlayingCommand.Execute(null);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        => ResponsiveClasses.Apply(this, e.NewSize.Width);

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm) vm.BeginScrub();
    }

    private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm) vm.EndScrub();
    }
}
