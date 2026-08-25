using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace ALyricEase.Infrastructure.Behaviors;

/// <summary>
/// 横向滚动区的平滑滚轮行为(与 SmoothWheelScrollBehavior 同款 rAF 指数收敛动画):
/// - Ctrl+滚滚轮 → 本 ScrollViewer 水平滚动并拦截(不再冒泡驱动页面纵向滚动);
/// - 触控板横向手势(Delta.X)无需修饰键,同样走平滑动画;
/// - 未按 Ctrl 且无横向分量时不处理,滚轮照常冒泡给外层页面;
/// - 已到目标方向尽头时不消费(边缘放行,对齐 IsScrollChainingEnabled 语义)。
/// 用于纵向禁用、仅横向滚动的区块(个性推荐/歌手页的横向列表)。
/// 用法:<c>&lt;i:Interaction.Behaviors&gt;&lt;behaviors:CtrlHorizontalWheelBehavior /&gt;&lt;/i:Interaction.Behaviors&gt;</c>
/// </summary>
public sealed class CtrlHorizontalWheelBehavior : Behavior<ScrollViewer>
{
    /// <summary>每格滚轮的水平位移(px),约半张卡宽(卡 200 + 列距 12)。</summary>
    private const double WheelStep = 120;

    /// <summary>每帧向目标收敛的比例(60fps 基准):越大跟手越快、尾段越短。</summary>
    private const double LerpPerFrame = 0.26;

    private const double SnapEpsilon = 0.5;
    private const double FrameBaseMs = 1000.0 / 60;

    private bool _running;
    private double _target;
    private double _lastApplied;
    private TimeSpan _lastTs;

    protected override void OnAttached()
    {
        base.OnAttached();
        // 隧道阶段拦截,与纵向平滑行为同层;外层页面行为遇 Ctrl 会自行让位
        AssociatedObject?.AddHandler(InputElement.PointerWheelChangedEvent, OnWheelTunnelled,
            RoutingStrategies.Tunnel);
    }

    protected override void OnDetaching()
    {
        AssociatedObject?.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheelTunnelled);
        _running = false;
        base.OnDetaching();
    }

    private void OnWheelTunnelled(object? sender, PointerWheelEventArgs e)
    {
        var sv = AssociatedObject;
        if (sv is null || e.Handled) return;

        // 生效位移:触控板横向手势免修饰键;滚轮纵向分量需按住 Ctrl
        var delta = e.Delta.X != 0
            ? e.Delta.X
            : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? e.Delta.Y : 0;
        if (delta == 0) return;

        // 边缘放行:已到目标方向尽头时交给外层(通常无人接,事件自然消散)
        var maximum = sv.ScrollBarMaximum.X;
        var offset = sv.Offset.X;
        if ((delta < 0 && offset >= maximum - SnapEpsilon)
            || (delta > 0 && offset <= SnapEpsilon)) return;

        var topLevel = TopLevel.GetTopLevel(sv);
        if (topLevel is null) return;

        var from = _running ? _target : offset;
        _target = Math.Clamp(from - delta * WheelStep, 0, maximum);

        e.Handled = true;
        if (_running) return;

        _running = true;
        _lastApplied = offset;
        _lastTs = TimeSpan.Zero; // 首帧 dt 无意义,Frame 内会钳到基准帧长
        ScheduleFrame(sv, topLevel);
    }

    private void ScheduleFrame(ScrollViewer sv, TopLevel topLevel)
        => topLevel.RequestAnimationFrame(ts => Frame(sv, topLevel, ts));

    private void Frame(ScrollViewer sv, TopLevel topLevel, TimeSpan ts)
    {
        if (!_running || !sv.IsAttachedToVisualTree())
        {
            _running = false;
            return;
        }

        var current = sv.Offset.X;

        // 外部改动(拖动滚动条/程序化滚动):放弃惯性,同步到实际值
        if (Math.Abs(current - _lastApplied) > SnapEpsilon)
        {
            _running = false;
            return;
        }

        var delta = _target - current;
        if (Math.Abs(delta) <= SnapEpsilon)
        {
            sv.Offset = sv.Offset.WithX(_target);
            _running = false;
            return;
        }

        // 帧率无关的指数收敛:ts 是绝对时间戳,换算出真实帧间隔 dt(异常值钳到基准帧长)
        var dtMs = (ts - _lastTs).TotalMilliseconds;
        if (dtMs <= 0 || dtMs > 250) dtMs = FrameBaseMs;
        _lastTs = ts;
        var alpha = 1 - Math.Pow(1 - LerpPerFrame, dtMs / FrameBaseMs);
        var next = Math.Clamp(current + delta * alpha, 0, sv.ScrollBarMaximum.X);
        sv.Offset = sv.Offset.WithX(next);
        _lastApplied = next;
        ScheduleFrame(sv, topLevel);
    }
}
