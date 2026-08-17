using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;

namespace ALyricEase.Views;

public partial class PlayerBarView : UserControl
{
    private PlayerViewModel? _vm;
    private bool _scrubbing;
    private bool _hovered;

    public PlayerBarView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += (_, _) => ResponsiveClasses.Apply(this, Bounds.Width);
        DataContextChanged += (_, _) => AttachVm();
    }

    /// <summary>Tap 封面/曲目区 → 打开正在播放页。用 Tap(按下抬起才算)而非 PointerPressed:
    /// 按下即触发会跟拖动/滑进度误触;按钮点击冒泡上来时,按来源过滤掉。</summary>
    private void OnTrackAreaTap(object? sender, TappedEventArgs e)
    {
        // Grid 处理触摸/鼠标事件时,播放按钮的 Tap 会冒泡;但歌曲区域(含封面)仍应打开详情。
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;

        // 无当前曲目时点击不打开正在播放页(占位标题不可点击)
        if (DataContext is not PlayerViewModel { CurrentSong: not null }) return;

        if (this.FindAncestorOfType<Window>()?.DataContext is MainViewModel { ShowNowPlaying: false } vm)
            vm.OpenNowPlayingCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ResponsiveClasses.Apply(this, e.NewSize.Width);
        UpdateProgress();
    }

    private void AttachVm()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as PlayerViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            UpdateProgress();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.PositionMs)
            or nameof(PlayerViewModel.ScrubPositionMs)
            or nameof(PlayerViewModel.DurationMs)
            or nameof(PlayerViewModel.HasProgress))
            UpdateProgress();
    }

    /// <summary>根据进度更新已播放段宽度 + 球位置。</summary>
    private void UpdateProgress()
    {
        if (_vm is null || ProgressTrack is null) return;
        var width = ProgressTrack.Bounds.Width;
        if (width <= 0 || _vm.DurationMs <= 0)
        {
            // 无有效进度:已播放段清零,球归位到轨道左端。
            // 此前这里只清 fill 不动球,换曲/首次播放时球停在旧位置,
            // 待下一次位置轮询才跳到正确位置 → "先靠左再闪到更左"的跳动。
            ProgressFill.Width = 0;
            ProgressThumb.Margin = new Thickness(-ProgressThumb.Width / 2, 0, 0, 0);
            return;
        }
        var ratio = Math.Clamp(_vm.ScrubPositionMs / _vm.DurationMs, 0, 1);
        ProgressFill.Width = width * ratio;
        ProgressThumb.Margin = new Thickness(width * ratio - ProgressThumb.Width / 2, 0, 0, 0);
        UpdateTooltipPosition();
    }

    /// <summary>时间气泡:与球同属 ProgressRoot(同一坐标系),按球的 Margin 对齐球心。
    /// 尺寸固定(不 Measure,避免拖动期间反复测量→布局→Hover 状态抖动);气泡底边距球上缘 20px。</summary>
    private void UpdateTooltipPosition()
    {
        if (ProgressTooltip is null || !ProgressTooltip.IsVisible) return;
        var centerX = ProgressThumb.Margin.Left + ProgressThumb.Width / 2;
        // ProgressRoot 高 28、球 20 居中 → 球上缘 y=4;气泡底边 = 球上缘 - 20px 间距
        const double thumbTop = 4;
        const double gap = 20;
        ProgressTooltip.Margin = new Thickness(
            centerX - ProgressTooltip.Width / 2,
            thumbTop - gap - ProgressTooltip.Height,
            0,
            0);
    }

    private void OnProgressPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm) return;
        if (!e.GetCurrentPoint(ProgressRoot).Properties.IsLeftButtonPressed) return;
        _scrubbing = true;
        e.Pointer.Capture(ProgressRoot); // 捕获指针:拖动期间移出轨道区仍持续收到 PointerMoved,防闪烁
        UpdateTooltipVisibility();
        vm.BeginScrub();
        SetProgressFromPointer(e);
        e.Handled = true;
    }

    private void OnProgressPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_scrubbing) SetProgressFromPointer(e);
    }

    private void OnProgressPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        e.Pointer.Capture(null);
        _hovered = ProgressThumb.IsPointerOver; // 松开后鼠标若仍在球上,保持气泡(hover)
        UpdateTooltipVisibility();
        if (DataContext is PlayerViewModel vm) vm.EndScrub();
        e.Handled = true;
    }

    /// <summary>鼠标停在球上时也显示气泡(显示当前播放位置的时间)。</summary>
    private void OnThumbPointerEntered(object? sender, PointerEventArgs e)
    {
        _hovered = true;
        UpdateTooltipVisibility();
        UpdateProgress(); // 立即把气泡摆到当前进度
    }

    private void OnThumbPointerExited(object? sender, PointerEventArgs e)
    {
        _hovered = false;
        UpdateTooltipVisibility();
    }

    /// <summary>气泡显示状态唯一由 _scrubbing/_hovered 决定(hover 或拖动即显示,避免两套逻辑互相覆盖)。</summary>
    private void UpdateTooltipVisibility() => ProgressTooltip.IsVisible = _scrubbing || _hovered;

    private void SetProgressFromPointer(PointerEventArgs e)
    {
        if (DataContext is not PlayerViewModel vm) return;
        var width = ProgressTrack.Bounds.Width;
        if (width <= 0 || vm.DurationMs <= 0) return;
        var ratio = Math.Clamp(e.GetPosition(ProgressTrack).X / width, 0, 1);
        vm.ScrubPositionMs = vm.DurationMs * ratio;
    }
}
