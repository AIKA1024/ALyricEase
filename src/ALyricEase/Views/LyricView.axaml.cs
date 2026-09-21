using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Models;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>歌词视图:当前句变化时把选中项居中滚动(SelectionChanged 驱动)。
/// 先用 ScrollIntoView 物化容器，再按 ScrollViewer.Offset 做逐帧平滑居中。
/// detail 模式(播放详情页歌词面板)映射原版 AbovePresent/BelowPresent 状态，
/// 以方向性透明度、模糊和缩放区分已唱/当前/待唱歌词。</summary>
public partial class LyricView : UserControl
{
    private int _scrollAnimationVersion;

    /// <summary>用户点按一行歌词后，请求播放器跳到该行在实际播放时间轴上的位置。</summary>
    public event EventHandler<LyricSeekRequestedEventArgs>? SeekRequested;

    /// <summary>歌词字号缩放系数(详情页字号档位 60%~150%,1 = 100%)。</summary>
    public static readonly StyledProperty<double> FontScaleProperty =
        AvaloniaProperty.Register<LyricView, double>(nameof(FontScale), 1.0);

    public double FontScale
    {
        get => GetValue(FontScaleProperty);
        set => SetValue(FontScaleProperty, value);
    }

    public LyricView()
    {
        InitializeComponent();
        // 虚拟化回收容器后类会丢,每次 realized 按当前距离重设
        LyricList.ContainerPrepared += OnContainerPrepared;
        // 鼠标按下时 ListBoxItem 会先改选中项并触发居中滚动，导致松开时目标已移位、Tap 被取消。
        // 与侧栏列表保持一致：鼠标选择交给 Tap 完成；触摸/笔仍保留原生按压以支持滚动手势。
        LyricList.AddHandler(
            InputElement.PointerPressedEvent,
            OnLyricListPointerPressed,
            RoutingStrategies.Tunnel);
        // ListBoxItem 会把 Tap 标记为已处理，仍需在同一次 Tap 中执行跳转。
        LyricList.AddHandler(
            InputElement.TappedEvent,
            OnLyricListTapped,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateProgressClasses();

        var index = LyricList.SelectedIndex;
        if (index < 0) return;

        // 先把虚拟化容器带入视口，再在下一次布局后做居中补间。
        LyricList.ScrollIntoView(index);
        var version = ++_scrollAnimationVersion;
        Dispatcher.UIThread.Post(() => AnimateSelectedToCenter(index, version), DispatcherPriority.Loaded);
    }

    private void AnimateSelectedToCenter(int index, int version)
    {
        if (version != _scrollAnimationVersion || LyricList.SelectedIndex != index) return;

        if (LyricList.ContainerFromIndex(index) is not Control container) return;
        if (LyricList.FindDescendantOfType<ScrollViewer>() is not { } scroll) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        var itemHeight = container.Bounds.Height;
        var viewportHeight = scroll.Viewport.Height;
        if (itemHeight <= 0 || viewportHeight <= 0) return; // 布局未就绪

        // 项在内容坐标系中的顶部 = 视口内位置 + 当前偏移
        var pos = container.TranslatePoint(new Point(0, 0), scroll);
        if (pos is null) return;

        var desired = pos.Value.Y + scroll.Offset.Y + itemHeight / 2 - viewportHeight / 2;
        var maxY = Math.Max(0.0, scroll.Extent.Height - viewportHeight);
        var targetY = Math.Clamp(desired, 0, maxY);
        var startY = scroll.Offset.Y;
        if (Math.Abs(targetY - startY) <= 0.5) return;

        TimeSpan? startedAt = null;
        Action<TimeSpan>? tick = null;
        tick = now =>
        {
            if (version != _scrollAnimationVersion || LyricList.SelectedIndex != index) return;
            startedAt ??= now;
            var progress = Math.Clamp((now - startedAt.Value).TotalMilliseconds / 420.0, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3); // 平滑缓出，避免逐句跳屏
            scroll.Offset = scroll.Offset.WithY(startY + (targetY - startY) * eased);
            if (progress < 1)
                topLevel.RequestAnimationFrame(tick!);
        };
        topLevel.RequestAnimationFrame(tick);
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is Control container)
            ApplyProgressClasses(container, e.Index, LyricList.SelectedIndex);
    }

    private void OnLyricListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse ||
            !e.GetCurrentPoint(LyricList).Properties.IsLeftButtonPressed ||
            e.Source is not Visual source)
            return;

        var container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is LyricLine)
            e.Handled = true;
    }

    private void OnLyricListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source) return;

        var container = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (container?.DataContext is not LyricLine line) return;

        var positionMs = DataContext is LyricViewModel vm
            ? vm.GetPlaybackPositionMs(line)
            : Math.Max(0, line.TimeMs);
        SeekRequested?.Invoke(this, new LyricSeekRequestedEventArgs(positionMs));
        e.Handled = true;
    }

    /// <summary>当前句变化后刷新所有已物化容器的方向性进度类。</summary>
    private void UpdateProgressClasses()
    {
        var current = LyricList.SelectedIndex;
        for (var i = 0; i < LyricList.ItemCount; i++)
            if (LyricList.ContainerFromIndex(i) is Control container)
                ApplyProgressClasses(container, i, current);
    }

    /// <summary>映射原版 InteractiveLyricControl2 的 AbovePresent/BelowPresent1/BelowPresent2 状态。
    ///
    /// <para>
    /// <c>far</c> 是给样式用的"更远的行"标记(距当前句 &gt;2 行,已唱与未唱两侧都算)——
    /// 那几行不挂模糊,只靠透明度弱化。它**不改**原版那三个状态的语义(above 仍是"所有已唱行",
    /// 覆盖远近),两者叠加时由 XAML 里的声明顺序决定谁压谁。
    /// </para>
    /// </summary>
    private static void ApplyProgressClasses(Control container, int index, int current)
    {
        container.Classes.Set("above", current >= 0 && index < current);
        container.Classes.Set("below1", current >= 0 && index == current + 1);
        container.Classes.Set("below2", current >= 0 && index == current + 2);
        container.Classes.Set("far", current >= 0 && Math.Abs(index - current) > 2);
    }
}

public sealed class LyricSeekRequestedEventArgs(long positionMs) : EventArgs
{
    public long PositionMs { get; } = positionMs;
}
