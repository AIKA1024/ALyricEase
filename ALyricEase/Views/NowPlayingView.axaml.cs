using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>LyricEase 式正在播放页；同一视图覆盖桌面、平板与手机窄屏布局。</summary>
public partial class NowPlayingView : UserControl
{
    public NowPlayingView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += (_, _) =>
        {
            ResponsiveClasses.Apply(this, Bounds.Width);
            // Escape 由收起按钮的 HotKeyManager.HotKey 全局处理(XAML),不依赖本视图焦点;
            // 这里延迟聚焦仅为视图内部键盘交互(进度条方向键等)争取焦点,失败不影响 Escape。
            Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);
        };
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        => ResponsiveClasses.Apply(this, e.NewSize.Width);

    /// <summary>顶部拖拽条:按下并拖动时移动窗口(播放详情页盖住标题栏,靠这里拖)。</summary>
    private void OnTitleDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(this) is Window w)
            w.BeginMoveDrag(e);
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
