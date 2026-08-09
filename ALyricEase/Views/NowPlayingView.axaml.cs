using Avalonia.Controls;
using Avalonia.Input;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>LyricEase/Apple Music 式正在播放页。响应式:宽窗封面左歌词右,窄窗封面在上歌词在下。</summary>
public partial class NowPlayingView : UserControl
{
    private const double WideLayoutThreshold = 840;

    public NowPlayingView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < WideLayoutThreshold;
        if (NarrowLayout.IsVisible != narrow) NarrowLayout.IsVisible = narrow;
        if (WideLayout.IsVisible == narrow) WideLayout.IsVisible = !narrow;
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
