using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

/// <summary>原版风格进度条(细轨道 + 双层圆球,球一半露在播放条外):宽屏与紧凑播放条共用。
/// 进度由 ScrubPositionMs/DurationMs 驱动(播放与拖动都会同步 Scrub 值);拖动直接驱动
/// DataContext 的 PlayerViewModel(BeginScrub/ScrubPositionMs/EndScrub),使用方无需再接线。
/// 控件高度与上边距由使用方设置(宽屏 28/-14,紧凑 20/-10)。ShowBubble 控制拖动/hover 时
/// 是否显示时间气泡(紧凑条不需要)。DataContext 必须是 PlayerViewModel。</summary>
public partial class PlayerProgressBar : UserControl
{
    public static readonly StyledProperty<double> ScrubPositionMsProperty =
        AvaloniaProperty.Register<PlayerProgressBar, double>(nameof(ScrubPositionMs));

    public static readonly StyledProperty<double> DurationMsProperty =
        AvaloniaProperty.Register<PlayerProgressBar, double>(nameof(DurationMs));

    public static readonly StyledProperty<bool> ShowBubbleProperty =
        AvaloniaProperty.Register<PlayerProgressBar, bool>(nameof(ShowBubble));

    private bool _scrubbing;
    private bool _hovered;

    public double ScrubPositionMs
    {
        get => GetValue(ScrubPositionMsProperty);
        set => SetValue(ScrubPositionMsProperty, value);
    }

    public double DurationMs
    {
        get => GetValue(DurationMsProperty);
        set => SetValue(DurationMsProperty, value);
    }

    public bool ShowBubble
    {
        get => GetValue(ShowBubbleProperty);
        set => SetValue(ShowBubbleProperty, value);
    }

    public PlayerProgressBar()
    {
        InitializeComponent();
        Root.SizeChanged += (_, _) => UpdateProgress();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScrubPositionMsProperty || change.Property == DurationMsProperty)
            UpdateProgress();
    }

    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null) return;
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
        _scrubbing = true;
        e.Pointer.Capture(Root); // 捕获指针:拖动期间移出轨道区仍持续收到 PointerMoved,防闪烁
        UpdateBubbleVisibility();
        Vm.BeginScrub();
        SetProgressFromPointer(e);
        e.Handled = true;
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_scrubbing) SetProgressFromPointer(e);
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        e.Pointer.Capture(null);
        _hovered = Thumb.IsPointerOver; // 松开后鼠标若仍在球上,保持气泡(hover)
        UpdateBubbleVisibility();
        Vm?.EndScrub();
        e.Handled = true;
    }

    /// <summary>鼠标停在球上时也显示气泡(显示当前播放位置的时间)。</summary>
    private void OnThumbPointerEntered(object? sender, PointerEventArgs e)
    {
        _hovered = true;
        UpdateBubbleVisibility();
        UpdateProgress(); // 立即把气泡摆到当前进度
    }

    private void OnThumbPointerExited(object? sender, PointerEventArgs e)
    {
        _hovered = false;
        UpdateBubbleVisibility();
    }

    /// <summary>气泡显示状态唯一由 _scrubbing/_hovered 决定(hover 或拖动即显示)。</summary>
    private void UpdateBubbleVisibility() => Bubble.IsVisible = ShowBubble && (_scrubbing || _hovered);

    private void SetProgressFromPointer(PointerEventArgs e)
    {
        if (Vm is null) return;
        var width = Track.Bounds.Width;
        if (width <= 0 || DurationMs <= 0) return;
        var ratio = Math.Clamp(e.GetPosition(Track).X / width, 0, 1);
        Vm.ScrubPositionMs = DurationMs * ratio;
    }

    /// <summary>按 ScrubPositionMs 摆已播放段/球/气泡。</summary>
    private void UpdateProgress()
    {
        if (Track is null) return;
        var width = Track.Bounds.Width;
        if (width <= 0 || DurationMs <= 0)
        {
            // 无有效进度:已播放段清零,球归位到轨道左端,避免换曲时球停在旧位置
            Fill.Width = 0;
            Thumb.Margin = new Thickness(-Thumb.Width / 2, 0, 0, 0);
            return;
        }

        var ratio = Math.Clamp(ScrubPositionMs / DurationMs, 0, 1);
        Fill.Width = width * ratio;
        Thumb.Margin = new Thickness(width * ratio - Thumb.Width / 2, 0, 0, 0);
        UpdateBubblePosition();
    }

    /// <summary>时间气泡:与球同属 Root(同一坐标系),按球的 Margin 对齐球心;
    /// 气泡底边距球上缘 20px(球上缘 = 控件高度居中算出)。</summary>
    private void UpdateBubblePosition()
    {
        if (!Bubble.IsVisible) return;
        var centerX = Thumb.Margin.Left + Thumb.Width / 2;
        var thumbTop = (Bounds.Height - Thumb.Height) / 2;
        const double gap = 20;
        Bubble.Margin = new Thickness(
            centerX - Bubble.Width / 2,
            thumbTop - gap - Bubble.Height,
            0, 0);
    }
}
