using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ALyricEase.Views;

/// <summary>
/// 以最多 60 FPS 仅更新进度条的渲染变换。轨道始终留在原布局坐标中，
/// 因而指针到播放位置的换算不会受到动画或视觉插值影响。
/// </summary>
internal sealed class ProgressRenderAnimator
{
    internal const int TargetFramesPerSecond = 60;
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
    private int _frameGeneration;
    private long _sampleTimestamp = Stopwatch.GetTimestamp();
    private long _nextRenderTimestamp;
    private double _samplePositionMs;
    private double _durationMs;

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
        RestartFrameLoop();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        StopFrameLoop();
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
    }

    private void EnsureFrameRequested()
    {
        if (_frameRequested || !ShouldAnimate()) return;
        if (TopLevel.GetTopLevel(_host) is not { } topLevel) return;

        var generation = _frameGeneration;
        _frameRequested = true;
        topLevel.RequestAnimationFrame(now => OnAnimationFrame(topLevel, generation, now));
    }

    private void OnAnimationFrame(TopLevel topLevel, int generation, TimeSpan _)
    {
        if (generation != _frameGeneration) return;
        _frameRequested = false;
        if (!ShouldAnimate()) return;

        var timestamp = Stopwatch.GetTimestamp();
        if (_nextRenderTimestamp == 0)
            _nextRenderTimestamp = timestamp;
        if (timestamp >= _nextRenderTimestamp)
        {
            var position = GetPredictedPosition();
            RenderPosition(position);
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
           && _durationMs > 0 && _samplePositionMs < _durationMs;

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
