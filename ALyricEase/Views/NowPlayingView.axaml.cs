using Avalonia.Controls;
using Avalonia.Input;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>LyricEase 式正在播放页:暗色模糊封面背景 + 居中封面/歌名/歌手 + 滚动歌词。</summary>
public partial class NowPlayingView : UserControl
{
    public NowPlayingView()
    {
        InitializeComponent();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel vm)
            vm.CloseNowPlayingCommand.Execute(null);
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Player.BeginScrub();
    }

    private void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Player.EndScrub();
    }
}
