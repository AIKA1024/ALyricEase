using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ALyricEase.Views;

/// <summary>
/// 桌面歌词窗口:只剩歌词本体,没有按钮行(和 Android 悬浮窗一致)。
/// 显隐/翻译/穿透走快捷键或设置弹窗,字号与外观在设置弹窗里调;拖动直接按在歌词上。
/// </summary>
public sealed partial class DesktopLyricWindow : Window
{
    /// <summary>
    /// 这次关闭是不是用户主动关的(Alt+F4 / 热键 / 设置里关歌词)。应用退出时 Avalonia 会连带
    /// 关闭所有窗口,那种关闭不能当成"用户不要歌词了"。
    /// </summary>
    internal bool ClosedByUser { get; private set; }

    public PixelPoint ExpandedPosition => Position;

    public DesktopLyricWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
        => ClosedByUser = e.CloseReason == WindowCloseReason.WindowClosing;

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
