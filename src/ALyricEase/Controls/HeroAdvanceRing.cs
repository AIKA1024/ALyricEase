using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Controls;

/// <summary>
/// 海报位左下角"还有多久换下一张"的转圈(微软商店轮播同款):
/// 半透明圆盘 + 白色扇形从 12 点方向顺时针长满一圈,长满即触发 <see cref="Completed"/>。
/// 鼠标停在图片上时视图把 <see cref="IsPaused"/> 置 true,进度原地冻住,移开继续;
/// 出现/消失是一次 200ms 的 Opacity 过渡(悬停暂停时圆不消失,只是不走)。
///
/// 为什么不用样式动画:进度必须能**从中途暂停再继续**,声明式动画做不到"停在这一格、之后接着走";
/// DispatcherTimer 每 40ms 只重画这个 30px 的控件(不碰整页),代价远小于给整页挂常驻帧泵。
/// 触发条件收得很紧:只有激活 + 未暂停 + 在树上可见 + 窗口在前台时才走表。
/// </summary>
public sealed class HeroAdvanceRing : Control
{
    /// <summary>要不要显示(多张海报才轮播:只有一张时没有"下一张"可等)。</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<HeroAdvanceRing, bool>(nameof(IsActive));

    /// <summary>悬停暂停:进度保留,恢复后接着走。</summary>
    public static readonly StyledProperty<bool> IsPausedProperty =
        AvaloniaProperty.Register<HeroAdvanceRing, bool>(nameof(IsPaused));

    /// <summary>一圈走完的时长(也就是自动切图的间隔)。</summary>
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<HeroAdvanceRing, TimeSpan>(nameof(Duration), TimeSpan.FromSeconds(6));

    /// <summary>绑 <c>HeroIndex</c>:手动翻页/点圆点后进度从头开始,不然刚切过去半圈就没了。</summary>
    public static readonly StyledProperty<int> RestartOnProperty =
        AvaloniaProperty.Register<HeroAdvanceRing, int>(nameof(RestartOn));

    /// <summary>切图时淡出多久、隔多久淡回来。留出翻页动画(600ms)的时间,圆会在新图落定时重新亮起。</summary>
    public static readonly StyledProperty<TimeSpan> ResumeDelayProperty =
        AvaloniaProperty.Register<HeroAdvanceRing, TimeSpan>(nameof(ResumeDelay), TimeSpan.FromMilliseconds(620));

    // 半透明:圆盘只是"底",扇形也不用纯白 —— 压在海报上不能抢正文
    private static readonly IBrush DiscBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush WedgeBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(160);

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _resumeTimer;
    private long _lastTick;
    private double _elapsedMs;
    private bool _resuming;

    public HeroAdvanceRing()
    {
        IsHitTestVisible = false;
        Opacity = 0;
        Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = FadeDuration },
        };
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, OnTick);
        _timer.Stop();
        // 一次性:切图淡出 → 等翻页动画落定 → 淡回来。**等待期间表也停着**,
        // 否则圆虽然看不见,进度已经悄悄走了十分之一,淡回来那一下像"没停过"。
        _resumeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ResumeDelay };
        _resumeTimer.Tick += (_, _) =>
        {
            _resumeTimer.Stop();
            _resuming = false;
            // 归零放在这里:此时圆还是透明的,看不到"满圈跳空"
            _elapsedMs = 0;
            _lastTick = Stopwatch.GetTimestamp();
            InvalidateVisual();
            UpdateClock(); // 重新起表,并把不透明度淡回 1
        };
        _resumeTimer.Stop();
    }

    /// <summary>一圈走完:视图在这里切下一张。</summary>
    public event EventHandler? Completed;

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsPaused
    {
        get => GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public int RestartOn
    {
        get => GetValue(RestartOnProperty);
        set => SetValue(RestartOnProperty, value);
    }

    public TimeSpan ResumeDelay
    {
        get => GetValue(ResumeDelayProperty);
        set => SetValue(ResumeDelayProperty, value);
    }

    /// <summary>0..1 的当前进度(探针读它判断"在走 / 暂停时没动")。</summary>
    public double Progress
        => Math.Clamp(_elapsedMs / Math.Max(500d, Duration.TotalMilliseconds), 0d, 1d);

    /// <summary>换图后重来一圈。淡出 → 等翻页动画落定 → 淡入,整段等待里**进度表是停的**:
    /// 让表继续走会"淡回来时已经走了 10%",看着像切图期间还在跑;开头就把扇形清零又会在
    /// 淡出的那 160ms 里看见它从满圈"啪"跳到空 —— 所以归零推到淡回来那一刻再做(反正那会儿看不见)。</summary>
    public void Restart()
    {
        if (!IsActive) return;
        _resuming = true;
        _timer.Stop();
        _lastTick = 0;
        Opacity = 0;
        _resumeTimer.Stop();
        _resumeTimer.Interval = ResumeDelay > TimeSpan.Zero ? ResumeDelay : TimeSpan.FromMilliseconds(1);
        _resumeTimer.Start();
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;

        var radius = side / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(DiscBrush, null, center, radius, radius);

        var total = Math.Max(500d, Duration.TotalMilliseconds);
        var progress = Math.Clamp(_elapsedMs / total, 0d, 1d);
        if (progress <= 0.001) return;
        if (progress >= 0.999)
        {
            // 满圈时起点和终点重合,ArcTo 画不出来,直接给整圆
            context.DrawEllipse(WedgeBrush, null, center, radius, radius);
            return;
        }

        // 12 点方向起画,顺时针长肉(-90° 是屏幕坐标里的正上方)
        var startAngle = -Math.PI / 2;
        var sweep = progress * Math.PI * 2;
        var start = new Point(center.X + radius * Math.Cos(startAngle), center.Y + radius * Math.Sin(startAngle));
        var end = new Point(center.X + radius * Math.Cos(startAngle + sweep),
            center.Y + radius * Math.Sin(startAngle + sweep));

        var pie = new StreamGeometry();
        using (var geometry = pie.Open())
        {
            geometry.BeginFigure(center, true);
            geometry.LineTo(start);
            geometry.ArcTo(end, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
            geometry.EndFigure(true);
        }

        context.DrawGeometry(WedgeBrush, null, pie);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateClock();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _resumeTimer.Stop();
        _resuming = false;
        _lastTick = 0;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RestartOnProperty)
        {
            Restart();
            return;
        }

        if (change.Property == IsActiveProperty
            || change.Property == IsPausedProperty
            || change.Property == DurationProperty)
        {
            UpdateClock();
        }
    }

    private void UpdateClock()
    {
        var duration = Duration;
        // 不判窗口是否在前台:headless 里 Window.IsActive 恒为 false,一加这条探针就永远跑不起来。
        // 最小化/被折叠时 Avalonia 本来就不出帧,多出来的只是每 40ms 一次极小的 InvalidateVisual;
        // 真停表靠 tick 里的 IsEffectivelyVisible 复查(祖先 IsVisible=False 不会通知到本控件)。
        var shouldRun = IsActive
                        && !IsPaused
                        && !_resuming // 切图那段等待里表也得停着,不然淡回来时已经走了十分之一
                        && duration > TimeSpan.Zero
                        && VisualRoot is not null
                        && IsEffectivelyVisible;

        if (shouldRun)
        {
            if (!_timer.IsEnabled)
            {
                _lastTick = Stopwatch.GetTimestamp();
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
            _lastTick = 0;
        }

        // _resuming 期间别把不透明度抢回 1:淡出后正等着 ResumeDelay,这时若因为悬停/时长变化走到这里,圆会提前蹦回来
        if (!_resuming) Opacity = IsActive ? 1 : 0;
        if (!IsActive)
        {
            _elapsedMs = 0;
            _resuming = false;
            _resumeTimer.Stop();
            InvalidateVisual();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsEffectivelyVisible)
        {
            _timer.Stop();
            _lastTick = 0;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var delta = _lastTick == 0 ? 0 : (now - _lastTick) * 1000d / Stopwatch.Frequency;
        _lastTick = now;

        var duration = Math.Max(500d, Duration.TotalMilliseconds);
        _elapsedMs += delta;
        if (_elapsedMs >= duration)
        {
            // 停在满圈:退场的那 160ms 里扇形保持长满,不会"啪"地跳回空。
            // 归零交给 Restart → 淡回来那一刻。
            _elapsedMs = duration;
            InvalidateVisual();
            Completed?.Invoke(this, EventArgs.Empty);
            // 没人接手(只有一张图 / 命令没执行)才自己归零,否则会每 40ms 重复触发
            if (!_resuming) _elapsedMs = 0;
            return;
        }

        InvalidateVisual();
    }
}
