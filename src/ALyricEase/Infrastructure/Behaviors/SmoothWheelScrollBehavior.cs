using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace ALyricEase.Infrastructure.Behaviors;

/// <summary>
/// 滚轮平滑滚动行为(Avalonia 原生滚轮由模板内 ScrollContentPresenter 冒泡阶段逐格瞬移,
/// 无动画)。本行为在隧道阶段接管滚轮,把每格位移累加进目标偏移,再由 RequestAnimationFrame
/// 每帧向目标指数收敛,得到连续减速的滚动曲线。仅处理纵向;不可竖滚(内容未溢出/纵向
/// Disabled)时不接管,滚轮照常冒泡给外层页面。
/// 接管规则:
/// - Ctrl 按下:让位给局部行为(如横向区块的 Ctrl+滚轮横滚);
/// - 光标下有更内层可竖滚的 ScrollViewer:内层优先(嵌套列表各自滚动);
/// - 动画期间检测到外部改动(拖滚动条/程序化滚动)立即放弃惯性并同步实际值。
/// 用法:<c>&lt;i:Interaction.Behaviors&gt;&lt;behaviors:SmoothWheelScrollBehavior /&gt;&lt;/i:Interaction.Behaviors&gt;</c>
/// </summary>
public sealed class SmoothWheelScrollBehavior : Behavior<ScrollViewer>
{
    /// <summary>每格滚轮的目标位移(px)。原生约 48px/格,略加大配合动画的整体观感。</summary>
    private const double WheelStep = 100;

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
        // Tunneling:先于模板内 ScrollContentPresenter 的冒泡处理,才有机会替换原生跳变;
        // handledEventsToo=false:已被内部控件消费的滚轮(如打开的下拉框)不去抢。
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
        if (sv is null || e.Handled || e.Delta.Y == 0) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) return; // 让位给局部行为(如区块 Ctrl+横滚)
        if (!CanScrollVertically(sv)) return;
        if (!IsInnermostScrollable(sv, e.Source as Visual)) return;

        // 边缘链式语义(对齐 IsScrollChainingEnabled):已到目标方向的尽头时不消费,
        // 让外层可滚容器接手继续平滑滚动
        var maximum = sv.ScrollBarMaximum.Y;
        var offset = sv.Offset.Y;
        if ((e.Delta.Y < 0 && offset >= maximum - SnapEpsilon)
            || (e.Delta.Y > 0 && offset <= SnapEpsilon)) return;

        var topLevel = TopLevel.GetTopLevel(sv);
        if (topLevel is null) return;

        var from = _running ? _target : offset;
        _target = Math.Clamp(from - e.Delta.Y * WheelStep, 0, maximum);

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

        var current = sv.Offset.Y;

        // 外部改动(拖动滚动条/程序化滚动):放弃惯性,同步到实际值
        if (Math.Abs(current - _lastApplied) > SnapEpsilon)
        {
            _running = false;
            return;
        }

        var delta = _target - current;
        if (Math.Abs(delta) <= SnapEpsilon)
        {
            sv.Offset = sv.Offset.WithY(_target);
            _running = false;
            return;
        }

        // 帧率无关的指数收敛:ts 是绝对时间戳,先换算出帧间隔 dt(异常值钳到基准帧长),
        // dt 越长单帧走得越远,不同刷新率下速度一致
        var dtMs = (ts - _lastTs).TotalMilliseconds;
        if (dtMs <= 0 || dtMs > 250) dtMs = FrameBaseMs; // 首帧/长暂停后按基准帧长走一小步
        _lastTs = ts;
        var alpha = 1 - Math.Pow(1 - LerpPerFrame, dtMs / FrameBaseMs);
        var next = Math.Clamp(current + delta * alpha, 0, sv.ScrollBarMaximum.Y);
        sv.Offset = sv.Offset.WithY(next);
        _lastApplied = next;
        ScheduleFrame(sv, topLevel);
    }

    /// <summary>能否竖向滚动(纵向未禁用且内容溢出)。</summary>
    private static bool CanScrollVertically(ScrollViewer sv)
        => sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled
           && sv.Extent.Height > sv.Viewport.Height + 0.5;

    /// <summary>viewer 是否为光标下最内层的可竖滚 ScrollViewer(嵌套时让内层接管)。</summary>
    private static bool IsInnermostScrollable(ScrollViewer viewer, Visual? source)
    {
        if (source is null) return true;
        for (var node = source; node is not null; node = node.GetVisualParent())
        {
            if (ReferenceEquals(node, viewer)) return true;
            if (node is ScrollViewer other && CanScrollVertically(other)) return false;
        }

        return false;
    }
}
