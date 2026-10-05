using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;

namespace ALyricEase.Infrastructure;

/// <summary>页面滑动与淡入淡出在合成线程执行，避免页面数据更新阻塞 UI 线程时停帧。
/// TransitioningContentControl 仍负责布局后启动、取消旧过渡以及释放旧 presenter。
/// 页面首次建树/布局依然需要 UI 线程，本过渡只消除动画对 UI 帧时钟的依赖。</summary>
public sealed class CompositionPageTransition : IPageTransition
{
    // 与壳层抽屉一致的缓出曲线：起步快，接近目标时逐渐减速。
    private static readonly SplineEasing EaseOut = new(0.215, 0.61, 0.355, 1);
    private readonly Dictionary<CompositionVisual, AnimationLease> _active = new();

    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(300);

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var outgoing = from is null ? null : ElementComposition.GetElementVisual(from);
        var incoming = to is null ? null : ElementComposition.GetElementVisual(to);
        if (Duration <= TimeSpan.Zero || (outgoing is null && incoming is null))
        {
            if (outgoing is not null && _active.TryGetValue(outgoing, out var oldExit)) Release(oldExit);
            if (incoming is not null && _active.TryGetValue(incoming, out var oldEnter)) Release(oldEnter);
            if (from is not null) from.IsVisible = false;
            if (to is not null) to.IsVisible = true;
            return;
        }

        var duration = Duration;
        var parent = (to ?? from)?.GetVisualParent();
        var distance = (float)(parent?.Bounds.Width ?? 0) * (forward ? 1 : -1);
        AnimationLease? exit = null;
        AnimationLease? enter = null;
        try
        {
            if (outgoing is not null)
                exit = Animate(from!, outgoing, Vector3.Zero, new Vector3(-distance, 0, 0), 1, 0, duration);
            if (incoming is not null)
            {
                to!.IsVisible = true;
                enter = Animate(to, incoming, new Vector3(distance, 0, 0), Vector3.Zero, 0, 1, duration);
            }

            // 从合成器收到本批次后计时，避免 UI 布局较忙时提前拆掉退场页。
            await (incoming ?? outgoing)!.Compositor.RequestCommitAsync().WaitAsync(cancellationToken);
            await Task.Delay(duration, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && exit is not null && Owns(exit))
                from!.IsVisible = false;
        }
        finally
        {
            // 两个 presenter 会在快速导航时交换角色。过期任务不能停止新任务的动画，
            // 也不能把正在入场的 presenter 隐藏或恢复为旧的退场状态。
            Release(exit);
            Release(enter);
        }
    }

    private AnimationLease Animate(Visual target, CompositionVisual visual, Vector3 from, Vector3 to,
        float fromOpacity, float toOpacity, TimeSpan duration)
    {
        var lease = _active.TryGetValue(visual, out var previous)
            ? previous with { }
            : new AnimationLease(visual, new Vector3((float)visual.Translation.X,
                (float)visual.Translation.Y, (float)visual.Translation.Z), visual.Opacity,
                target as InputElement, (target as InputElement)?.IsHitTestVisible ?? false);
        _active[visual] = lease;

        // 合成平移不参与 UI 命中测试；过渡期间禁止点到尚未到位或已经退场的页。
        lease.Input?.SetCurrentValue(InputElement.IsHitTestVisibleProperty, false);

        // 基值留在末帧；UI 线程迟迟不能执行完成回调时，退场页也不会闪回。
        visual.Translation = lease.Translation + to;
        visual.Opacity = lease.Opacity * toOpacity;
        var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = duration;
        slide.InsertKeyFrame(0f, lease.Translation + from);
        slide.InsertKeyFrame(1f, lease.Translation + to, EaseOut);
        visual.StartAnimation("Translation", slide);

        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = duration;
        fade.InsertKeyFrame(0f, lease.Opacity * fromOpacity);
        fade.InsertKeyFrame(1f, lease.Opacity * toOpacity, EaseOut);
        visual.StartAnimation("Opacity", fade);
        return lease;
    }

    private bool Owns(AnimationLease lease) =>
        _active.TryGetValue(lease.Visual, out var current) && ReferenceEquals(current, lease);

    private void Release(AnimationLease? lease)
    {
        if (lease is null || !Owns(lease)) return;
        lease.Visual.StopAnimation("Translation");
        lease.Visual.StopAnimation("Opacity");
        lease.Visual.Translation = lease.Translation;
        lease.Visual.Opacity = lease.Opacity;
        lease.Input?.SetCurrentValue(InputElement.IsHitTestVisibleProperty, lease.IsHitTestVisible);
        _active.Remove(lease.Visual);
    }

    private sealed record AnimationLease(CompositionVisual Visual, Vector3 Translation, float Opacity,
        InputElement? Input, bool IsHitTestVisible);
}
