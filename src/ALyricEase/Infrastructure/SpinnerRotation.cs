using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.VisualTree;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 让转圈在**合成线程**旋转（而不是 UI 线程的样式动画），并只在实际看得见时转。
///
/// **为什么用合成动画**：`Ellipse.spinner` 原来是 `Style.Animations` 驱动，每帧都要 UI 线程醒着推进；
/// 而 UI 线程静息时长睡 ~79ms（见 <see cref="UiFramePacer"/>）⇒ 实测只有 ~12fps，"转圈卡卡的"。
/// 合成动画由渲染线程按真实帧驱动，**不需要 UI 线程参与**（同 <see cref="CompositionPageTransition"/>、
/// 模态弹层、侧栏抽屉的做法），因此既顺滑又不用把 UI 线程从睡眠里拽起来。
///
/// **仍然要门控**：compositor 对隐藏元素照旧推进动画时钟；而且转圈在加载结束后只是
/// <c>IsVisible=false</c>，不主动停就是长期空转。判定口径与 <see cref="IndeterminateAnimationGate"/> 一致：
/// 自身 + 全部祖先可见、窗口非最小化。
///
/// **兜底**：取不到 <see cref="CompositionVisual"/>（极端平台/尚未建合成树）时退回
/// `Ellipse.spinner.running` 的样式动画 + <see cref="UiFramePacer"/> 租约 —— 慢但能看，
/// 两条路径**互斥**（都开会叠加成双倍转速）。
///
/// 用法：<c>&lt;Ellipse Classes="spinner" infra:SpinnerRotation.IsActive="{Binding IsLoading}" ... /&gt;</c>
/// </summary>
public static class SpinnerRotation
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsActive", typeof(SpinnerRotation));

    static SpinnerRotation()
    {
        IsActiveProperty.Changed.AddClassHandler<Control>(OnActiveChanged);
    }

    public static bool GetIsActive(Control control) => control.GetValue(IsActiveProperty);

    public static void SetIsActive(Control control, bool value) => control.SetValue(IsActiveProperty, value);

    /// <summary>一圈用时。0.9s 与原先样式动画一致。</summary>
    private const double SecondsPerTurn = 0.9;

    /// <summary>兜底路径用的样式类（样式里只有它带动画）。</summary>
    private const string RunningClass = "running";

    private static readonly ConditionalWeakTable<Control, SpinnerState> States = new();

    private sealed class SpinnerState
    {
        /// <summary>正在驱动旋转的合成视觉（null = 走兜底样式动画）。</summary>
        public CompositionVisual? Visual;

        public bool Spinning;

        /// <summary>兜底路径是否持有帧泵租约。</summary>
        public bool PacerHeld;

        public TopLevel? TopLevel;

        public EventHandler<AvaloniaPropertyChangedEventArgs>? HostHandler;

        public EventHandler<AvaloniaPropertyChangedEventArgs>? VisibilityHandler;

        /// <summary>已订阅的节点：脱离视觉树后祖先链已断，靠 <c>GetVisualParent</c> 回走收不回来。</summary>
        public readonly List<Visual> Watched = [];
    }

    /// <summary>探针用：这一帧到底有没有在合成线程转。</summary>
    internal static bool IsSpinning(Control control)
        => States.TryGetValue(control, out var state) && state.Spinning;

    private static void OnActiveChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        control.AttachedToVisualTree -= OnControlAttached;
        control.DetachedFromVisualTree -= OnControlDetached;

        if (!args.GetNewValue<bool>())
        {
            if (States.TryGetValue(control, out var off))
            {
                Unsubscribe(control, off);
                SetSpinning(control, off, false);
            }
            return;
        }

        control.AttachedToVisualTree += OnControlAttached;
        control.DetachedFromVisualTree += OnControlDetached;
        var state = States.GetOrCreateValue(control);
        if (control.IsAttachedToVisualTree()) OnControlAttached(control, null);
    }

    private static void OnControlAttached(object? sender, VisualTreeAttachmentEventArgs? e)
    {
        if (sender is not Control control || !States.TryGetValue(control, out var state)) return;
        Subscribe(control, state);
        Apply(control, state);
    }

    private static void OnControlDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control || !States.TryGetValue(control, out var state)) return;
        Unsubscribe(control, state);
        SetSpinning(control, state, false); // 离树后一秒都不该再转（页面被回收/虚拟化走这条）
    }

    private static void Subscribe(Control control, SpinnerState state)
    {
        state.TopLevel = TopLevel.GetTopLevel(control);
        if (state.TopLevel is not null)
        {
            state.HostHandler ??= (_, args) =>
            {
                // 最小化时 Window.IsVisible 仍是 true，只看它不够。
                if (args.Property == Visual.IsVisibleProperty || args.Property == Window.WindowStateProperty)
                    Apply(control, state);
            };
            state.TopLevel.PropertyChanged -= state.HostHandler;
            state.TopLevel.PropertyChanged += state.HostHandler;
        }

        state.VisibilityHandler ??= (_, args) =>
        {
            if (args.Property == Visual.IsVisibleProperty) Apply(control, state);
        };
        if (state.Watched.Count == 0)
        {
            for (Visual? node = control; node is not null; node = node.GetVisualParent())
            {
                node.PropertyChanged += state.VisibilityHandler;
                state.Watched.Add(node);
            }
        }
    }

    private static void Unsubscribe(Control control, SpinnerState state)
    {
        if (state.TopLevel is not null && state.HostHandler is not null)
            state.TopLevel.PropertyChanged -= state.HostHandler;
        state.TopLevel = null;

        if (state.VisibilityHandler is not null)
            foreach (var node in state.Watched)
                node.PropertyChanged -= state.VisibilityHandler;
        state.Watched.Clear();
    }

    private static void Apply(Control control, SpinnerState state)
    {
        var top = state.TopLevel;
        var presentable = top is not null
                          && top.IsVisible
                          && (top is not Window window || window.WindowState != WindowState.Minimized);
        SetSpinning(control, state, presentable && control.IsEffectivelyVisible && GetIsActive(control));
    }

    private static void SetSpinning(Control control, SpinnerState state, bool spinning)
    {
        if (spinning == state.Spinning) return;
        state.Spinning = spinning;
        if (spinning) Start(control, state);
        else Stop(control, state);
    }

    private static void Start(Control control, SpinnerState state)
    {
        if (TryStartComposition(control, state)) return;

        // 兜底：样式动画 + 帧泵（至少比完全不动强；不要与合成动画同时开）
        control.Classes.Add(RunningClass);
        if (!state.PacerHeld)
        {
            state.PacerHeld = true;
            UiFramePacer.Acquire();
        }
    }

    private static void Stop(Control control, SpinnerState state)
    {
        if (state.Visual is { } visual)
        {
            visual.StopAnimation("RotationAngle");
            visual.RotationAngle = 0;
            state.Visual = null;
        }

        control.Classes.Remove(RunningClass);
        if (state.PacerHeld)
        {
            state.PacerHeld = false;
            UiFramePacer.Release();
        }
    }

    /// <summary>在合成层起一个"无限旋转"：RotationAngle 单位是**弧度**，0 → 2π 正好一圈（端点视觉重合，循环无缝）。</summary>
    private static bool TryStartComposition(Control control, SpinnerState state)
    {
        var visual = ElementComposition.GetElementVisual(control);
        if (visual is null) return false;

        // 尺寸取显式 Width/Height：转圈在 XAML 里都是固定大小的，不必等布局完成。
        var width = double.IsNaN(control.Width) ? control.Bounds.Width : control.Width;
        var height = double.IsNaN(control.Height) ? control.Bounds.Height : control.Height;
        if (width <= 0 || height <= 0) return false;

        // 合成层的原点是左上角，不设 AnchorPoint 会绕角公转而不是自转。
        visual.Size = new Vector(width, height);
        visual.AnchorPoint = new Vector(0.5, 0.5);

        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromSeconds(SecondsPerTurn);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        animation.InsertKeyFrame(0f, 0f);
        animation.InsertKeyFrame(1f, MathF.PI * 2, new LinearEasing());
        visual.StartAnimation("RotationAngle", animation);

        state.Visual = visual;
        return true;
    }
}
