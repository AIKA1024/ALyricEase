using System;
using System.Diagnostics;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ALyricEase.Views;

/// <summary>
/// 以最多 60 FPS 仅更新进度条的渲染变换。轨道始终留在原布局坐标中，
/// 因而指针到播放位置的换算不会受到动画或视觉插值影响。
///
/// <para>
/// ⚠ **它自己续订 <c>RequestAnimationFrame</c> 是不够的** —— 实测:光靠这个循环,
/// 静息时每秒只真正写入视觉 8.7 次(帧间隔 p90 79ms),原因是 UI 线程在长睡而
/// <c>WM_TIMER</c> 唤不醒它。所以循环活着的时候要一并持有
/// <see cref="UiFramePacer"/>,由它按 ~60Hz 投递把 UI 线程叫醒。
/// 数字、排除过程与"为什么不是别的方向"都写在 <see cref="UiFramePacer"/> 上。
/// </para>
/// </summary>
internal sealed class ProgressRenderAnimator
{
    internal const int TargetFramesPerSecond = 60;

    /// <summary>诊断钩子:每收到一帧 RAF 回调就报一次(参数 = <see cref="Stopwatch.GetTimestamp"/> 时间戳)。
    /// 交付帧率是平台给的、也是这条动画的输入,只能数出来、不能假设 ——
    /// 实测它随"Win32 队列里有没有待处理输入"变化(见 ProgressCadenceProbe)。
    /// 生产里没人订阅时是 null,零开销;只给探针读。</summary>
    internal static Action<ProgressRenderAnimator, long>? FrameDelivered;

    /// <summary>诊断钩子:每真的把预测位置写进视觉(<c>ScaleX</c>/球/气泡)就报一次。
    /// 与 <see cref="FrameDelivered"/> 分开是因为两者可以不等:逻辑时间轴按 60FPS 累加,
    /// 平台给得更快时会出现"收到帧但这一帧不写"的情况。</summary>
    internal static Action<ProgressRenderAnimator, long>? FrameRendered;
    private static readonly long FrameIntervalTicks =
        (long)Math.Ceiling(Stopwatch.Frequency / (double)TargetFramesPerSecond);

    private readonly Control _host;
    private readonly Control _track;
    private readonly Control _thumb;
    private readonly Control? _bubble;
    private readonly ScaleTransform _fillScale = new(0, 1);
    private readonly TranslateTransform _thumbTranslation = new();
    private readonly TranslateTransform? _bubbleTranslation;

    private bool _attached;
    private bool _active = true;
    private bool _isPlaying;
    private bool _isScrubbing;
    private bool _frameRequested;
    private bool _pacerHeld;
    private int _frameGeneration;
    private long _sampleTimestamp = Stopwatch.GetTimestamp();
    private long _nextRenderTimestamp;
    private double _samplePositionMs;
    private double _durationMs;

    // 承载本控件的 TopLevel(桌面即 Window):用来判断进度条所在窗口此刻是否真的在呈现。
    // 不呈现时必须停摆,详见 IsHostPresentable()。
    private TopLevel? _observedTopLevel;
    private Window? _observedWindow;
    private bool _lastPresentable = true;

    public ProgressRenderAnimator(Control host, Control track, Control fill, Control thumb, Control? bubble = null)
    {
        _host = host;
        _track = track;
        _thumb = thumb;
        _bubble = bubble;
        _bubbleTranslation = bubble is null ? null : new TranslateTransform();

        fill.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
        fill.RenderTransform = _fillScale;
        thumb.RenderTransform = _thumbTranslation;
        if (bubble is not null)
            bubble.RenderTransform = _bubbleTranslation;

        _track.SizeChanged += (_, _) => RefreshVisual();
        _host.SizeChanged += (_, _) => RefreshVisual();
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        ObserveTopLevel(TopLevel.GetTopLevel(_host));
        _lastPresentable = IsHostPresentable();
        RestartFrameLoop();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        StopFrameLoop();
        ObserveTopLevel(null);
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active)
            RestartFrameLoop();
        else
            StopFrameLoop();
    }

    /// <summary>接收播放器的权威进度样本，并从该位置开始预测正常播放进度。</summary>
    public void SetPlaybackState(double positionMs, double durationMs, bool isPlaying)
    {
        _samplePositionMs = ClampPosition(positionMs, durationMs);
        _durationMs = Math.Max(0, durationMs);
        _isPlaying = isPlaying;
        _sampleTimestamp = Stopwatch.GetTimestamp();
        RenderPosition(_samplePositionMs);
        _nextRenderTimestamp = _sampleTimestamp + FrameIntervalTicks;
        EnsureFrameRequested();
    }

    /// <summary>拖动期间立即更新视觉，不经过 60 FPS 插值或节流。</summary>
    public void SetScrubPosition(double positionMs, double durationMs)
    {
        _samplePositionMs = ClampPosition(positionMs, durationMs);
        _durationMs = Math.Max(0, durationMs);
        _sampleTimestamp = Stopwatch.GetTimestamp();
        RenderPosition(_samplePositionMs);
    }

    public void SetScrubbing(bool scrubbing)
    {
        if (_isScrubbing == scrubbing) return;
        _isScrubbing = scrubbing;
        _sampleTimestamp = Stopwatch.GetTimestamp();
        if (scrubbing)
            StopFrameLoop();
        else
            RestartFrameLoop();
    }

    /// <summary>布局或气泡可见性改变时，按当前预测值重新提交视觉坐标。</summary>
    public void RefreshVisual() => RenderPosition(GetPredictedPosition());

    /// <summary>释放指针捕获时直接按渲染后的小球矩形判断，避免 IsPointerOver 提交延迟。</summary>
    public bool IsPointOverThumb(Point pointInHost)
    {
        var left = _thumbTranslation.X;
        var thumbWidth = GetWidth(_thumb);
        var thumbHeight = GetHeight(_thumb);
        var top = (_host.Bounds.Height - thumbHeight) / 2;
        return new Rect(left, top, thumbWidth, thumbHeight).Contains(pointInHost);
    }

    internal static double PredictPosition(
        double samplePositionMs,
        double durationMs,
        bool isPlaying,
        bool isScrubbing,
        TimeSpan elapsed)
    {
        var position = isPlaying && !isScrubbing
            ? samplePositionMs + Math.Max(0, elapsed.TotalMilliseconds)
            : samplePositionMs;
        return ClampPosition(position, durationMs);
    }

    private double GetPredictedPosition()
        => PredictPosition(
            _samplePositionMs,
            _durationMs,
            _isPlaying,
            _isScrubbing,
            Stopwatch.GetElapsedTime(_sampleTimestamp));

    private void RenderPosition(double positionMs)
    {
        var width = _track.Bounds.Width;
        var ratio = width > 0 && _durationMs > 0
            ? Math.Clamp(positionMs / _durationMs, 0, 1)
            : 0;
        var centerX = width * ratio;
        var thumbWidth = GetWidth(_thumb);
        var thumbHeight = GetHeight(_thumb);

        _fillScale.ScaleX = ratio;
        _thumbTranslation.X = centerX - thumbWidth / 2;

        if (_bubble is not null && _bubbleTranslation is not null)
        {
            const double gap = 20;
            var thumbTop = (_host.Bounds.Height - thumbHeight) / 2;
            _bubbleTranslation.X = centerX - GetWidth(_bubble) / 2;
            _bubbleTranslation.Y = thumbTop - gap - GetHeight(_bubble);
        }
    }

    private void RestartFrameLoop()
    {
        StopFrameLoop();
        _samplePositionMs = GetPredictedPosition();
        _sampleTimestamp = Stopwatch.GetTimestamp();
        _nextRenderTimestamp = _sampleTimestamp + FrameIntervalTicks;
        EnsureFrameRequested();
    }

    private void StopFrameLoop()
    {
        _frameGeneration++;
        _frameRequested = false;
        _nextRenderTimestamp = 0;
        ReleasePacer();
    }

    private void EnsureFrameRequested()
    {
        if (_frameRequested || !ShouldAnimate()) return;
        if (TopLevel.GetTopLevel(_host) is not { } topLevel) return;
        ObserveTopLevel(topLevel);

        var generation = _frameGeneration;
        _frameRequested = true;
        AcquirePacer();
        topLevel.RequestAnimationFrame(now => OnAnimationFrame(topLevel, generation, now));
    }

    /// <summary>
    /// 帧泵的持有/归还必须成对,且与"循环是否真的在续订"一致 ——
    /// 泵开着时 UI 线程不再长睡,空闲占用会略升,所以循环一停就要立刻还回去。
    /// 归还点有两个:显式 <see cref="StopFrameLoop"/>(停用/拖动/离开视觉树),
    /// 与 <see cref="OnAnimationFrame"/> 里 <see cref="ShouldAnimate"/> 转假(隐式停摆)。
    /// 用标志去重,避免重复归还把别的持有者的份额减掉。
    /// </summary>
    private void AcquirePacer()
    {
        if (_pacerHeld) return;
        _pacerHeld = true;
        UiFramePacer.Acquire();
    }

    private void ReleasePacer()
    {
        if (!_pacerHeld) return;
        _pacerHeld = false;
        UiFramePacer.Release();
    }

    private void OnAnimationFrame(TopLevel topLevel, int generation, TimeSpan frameTimestamp)
    {
        if (generation != _frameGeneration) return;
        FrameDelivered?.Invoke(this, frameTimestamp.Ticks);
        _frameRequested = false;
        if (!ShouldAnimate())
        {
            ReleasePacer();
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (_nextRenderTimestamp == 0)
            _nextRenderTimestamp = timestamp;
        if (timestamp >= _nextRenderTimestamp)
        {
            var position = GetPredictedPosition();
            RenderPosition(position);
            FrameRendered?.Invoke(this, timestamp);
            if (position >= _durationMs) return;

            // 按固定时间轴累加而不是“本次实际帧时间 + 33ms”，在 60/120/144Hz
            // 屏幕上都会长期平均到 60 FPS，而不会因刷新率不能整除而持续偏慢。
            do
            {
                _nextRenderTimestamp += FrameIntervalTicks;
            } while (_nextRenderTimestamp <= timestamp);
        }

        _frameRequested = true;
        topLevel.RequestAnimationFrame(next => OnAnimationFrame(topLevel, generation, next));
    }

    private bool ShouldAnimate()
        => _attached && _active && _isPlaying && !_isScrubbing
           && _durationMs > 0 && _samplePositionMs < _durationMs
           && IsHostPresentable();

    /// <summary>承载窗口此刻是否真的在呈现内容。
    ///
    /// 为什么必须自己判:窗口最小化/隐藏时,Avalonia 仍会把 RequestAnimationFrame 回调送进来
    /// (2026-09-16 真窗口实测:最小化态下填充条 ScaleX 依然按实时速率推进),而这个循环是
    /// **自行续订下一帧**的 —— 只要条件成立就永不主动停。于是一首歌从头到尾都在为一个
    /// 看不见的进度条做每帧写入(改 3 个 Transform + 视觉失效)。真机(Android 后台)上
    /// 这类每帧唤醒的耗电代价更高,所以判定要放在框架层能给出的可见性上。
    ///
    /// 停摆是隐式的:帧回调里 ShouldAnimate() 为 false 就不再续订;恢复由
    /// OnHostPresentationChanged 重新点火。TopLevel 尚未解析出来时不自作主张(返回 true)。</summary>
    private bool IsHostPresentable()
    {
        if (_observedTopLevel is not { } topLevel) return true;
        if (!topLevel.IsVisible) return false;
        return _observedWindow is null || _observedWindow.WindowState != WindowState.Minimized;
    }

    /// <summary>解析(或更换)承载 TopLevel 并订阅可见性/窗口状态变化。幂等,只在 Attach 与首次挂帧时走。</summary>
    private void ObserveTopLevel(TopLevel? topLevel)
    {
        var window = topLevel as Window;
        if (ReferenceEquals(topLevel, _observedTopLevel) && ReferenceEquals(window, _observedWindow)) return;

        UnobserveTopLevel();
        _observedTopLevel = topLevel;
        _observedWindow = window;
        if (topLevel is not null) topLevel.PropertyChanged += OnHostPresentationChanged;
        // 桌面下 TopLevel 就是 Window,同一个对象只订阅一次。
        if (window is not null && !ReferenceEquals(window, topLevel))
            window.PropertyChanged += OnHostPresentationChanged;
    }

    private void UnobserveTopLevel()
    {
        if (_observedTopLevel is not null) _observedTopLevel.PropertyChanged -= OnHostPresentationChanged;
        if (_observedWindow is not null && !ReferenceEquals(_observedWindow, _observedTopLevel))
            _observedWindow.PropertyChanged -= OnHostPresentationChanged;
        _observedTopLevel = null;
        _observedWindow = null;
    }

    private void OnHostPresentationChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty && e.Property != Window.WindowStateProperty) return;
        SyncFrameLoopWithPresentation();
    }

    /// <summary>只在"可呈现性"翻转时动循环 —— 否则每次窗口状态或可见性抖一下都会重置预测基线。</summary>
    private void SyncFrameLoopWithPresentation()
    {
        var presentable = IsHostPresentable();
        if (presentable == _lastPresentable) return;
        _lastPresentable = presentable;

        if (presentable) RestartFrameLoop();
        else StopFrameLoop();
    }

    private static double ClampPosition(double positionMs, double durationMs)
    {
        if (!double.IsFinite(positionMs)) return 0;
        var upperBound = double.IsFinite(durationMs) && durationMs > 0 ? durationMs : 0;
        return Math.Clamp(positionMs, 0, upperBound);
    }

    private static double GetWidth(Control control)
        => control.Bounds.Width > 0
            ? control.Bounds.Width
            : double.IsFinite(control.Width) ? control.Width : 0;

    private static double GetHeight(Control control)
        => control.Bounds.Height > 0
            ? control.Bounds.Height
            : double.IsFinite(control.Height) ? control.Height : 0;
}
