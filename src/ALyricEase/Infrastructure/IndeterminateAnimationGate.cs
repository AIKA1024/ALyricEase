using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 把"不确定进度条"(`IsIndeterminate=True`)的循环动画绑到宿主可见性上:窗口最小化/隐藏时停掉它。
///
/// **为什么必须自己判**(2026-09-16 实测,探针 `--pl-cpu-real` 的"歌单页·补页提示"场景):
/// Avalonia **在窗口最小化时仍然驱动动画时钟**——一条 3px 高的不确定进度条在最小化下仍要吃掉
/// **2.60% 单核**(合成线程 + UI 线程),而同一页面在它隐藏时是 **0.00%**。
/// 可见时更贵:它强制整个窗口每帧重合成(实测 5.7% ~ 17.7%,波动较大)。
/// ⇒ 歌单页"正在加载更多歌曲…"的提示条只要还挂在视觉树上,窗口看不见也照样转 ——
/// 这正是用户报告的"打开过歌单页之后,最小化也一直有零点几个百分点"的那一类开销。
/// 与 <see cref="Views.ProgressRenderAnimator"/> 的自续订循环是同一类问题(同一个项目里已修过一次),
/// 区别只是那条循环是自己写的、这条是框架主题动画,但**框架同样不会替你看窗口状态**。
///
/// 门控只覆盖"宿主不可显示"(最小化/隐藏)。可见时的动画成本是正常 UI 成本,不在本类职责内。
/// 判定口径与 `ProgressRenderAnimator.IsHostPresentable` 保持一致:
/// `TopLevel.IsVisible` 且非 `WindowState.Minimized`
/// (⚠️ 最小化时 `Window.IsVisible` 仍然是 `true`,只看它不够)。
///
/// 为什么**不**再判"控件自身/祖先是否可见"(实测过,不需要):
/// 应用里"提示条不显示"的真实状态是它自己那层容器被 `IsVisible="{Binding ...}"` 隐藏 —— 实测 0.00%,
/// Avalonia 对自身不可见的控件本就不再产生合成成本;而导航换页时页面是**离开视觉树**
/// (见 <see cref="ReusablePageViewTemplate"/>),不是靠隐藏留下来。
/// ⇒ 只看 TopLevel 已覆盖所有实际会发生的状态,别为一个不存在的场景引入跨层订阅。
///
/// 用法:`&lt;ProgressBar IsIndeterminate="True" infra:IndeterminateAnimationGate.IsActive="True" /&gt;`
/// </summary>
public static class IndeterminateAnimationGate
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<ProgressBar, bool>("IsActive", typeof(IndeterminateAnimationGate));

    static IndeterminateAnimationGate()
    {
        IsActiveProperty.Changed.AddClassHandler<ProgressBar>(OnIsActiveChanged);
    }

    public static bool GetIsActive(ProgressBar bar) => bar.GetValue(IsActiveProperty);

    public static void SetIsActive(ProgressBar bar, bool value) => bar.SetValue(IsActiveProperty, value);

    /// <summary>每个受控进度条的订阅状态。<c>ConditionalWeakTable</c>:控件被回收后条目自动消失,
    /// 不会因为探针/页面反复建视图而留下长期引用。</summary>
    private static readonly ConditionalWeakTable<ProgressBar, GateState> States = new();

    private sealed class GateState
    {
        public TopLevel? TopLevel;

        // ⚠️ 是 AvaloniaObject.PropertyChanged(EventHandler<AvaloniaPropertyChangedEventArgs>),
        // 不是 System.ComponentModel.INotifyPropertyChanged 那个同名事件。
        public EventHandler<AvaloniaPropertyChangedEventArgs>? HostHandler;

        /// <summary>XAML/绑定**想要**的值。可见时还原成它 —— 门控只负责"宿主不可显示时压成 false",
        /// 不能把业务状态一起吃掉(否则 `IsIndeterminate="{Binding IsBusy}"` 这种写法会被强行置 true)。</summary>
        public bool Desired = true;

        /// <summary>正在由本类写 <c>IsIndeterminate</c>:此时收到的变更通知不能当成"用户改了绑定值"。</summary>
        public bool Suppressing;
    }

    private static void OnIsActiveChanged(ProgressBar bar, AvaloniaPropertyChangedEventArgs args)
    {
        // 处理器都是静态方法:挂在控件自己的事件上不产生额外强引用(控件被回收时一起消失)。
        bar.AttachedToVisualTree -= OnBarAttached;
        bar.DetachedFromVisualTree -= OnBarDetached;
        bar.PropertyChanged -= OnBarPropertyChanged;

        if (!args.GetNewValue<bool>())
        {
            if (States.TryGetValue(bar, out var off))
            {
                Unsubscribe(bar, off);
                SetSuppressed(bar, off, off.Desired);   // 撤掉门控时把决定权还给绑定
            }
            return;
        }

        var state = States.GetOrCreateValue(bar);
        state.Desired = bar.IsIndeterminate;            // 以挂上门控那一刻的值作为"想要的值"
        bar.PropertyChanged += OnBarPropertyChanged;
        bar.AttachedToVisualTree += OnBarAttached;
        bar.DetachedFromVisualTree += OnBarDetached;
        if (bar.IsAttachedToVisualTree()) OnBarAttached(bar, null);
    }

    private static void OnBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (sender is not ProgressBar bar || args.Property != ProgressBar.IsIndeterminateProperty) return;
        if (!States.TryGetValue(bar, out var state) || state.Suppressing) return;
        state.Desired = args.GetNewValue<bool>();       // 绑定自己改了值 ⇒ 记下来,等可见时还原
    }

    private static void OnBarAttached(object? sender, VisualTreeAttachmentEventArgs? e)
    {
        if (sender is not ProgressBar bar || !States.TryGetValue(bar, out var state)) return;
        Subscribe(bar, state);
        Apply(bar, state);
    }

    private static void OnBarDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ProgressBar bar || !States.TryGetValue(bar, out var state)) return;
        Unsubscribe(bar, state);
        // 离开视觉树就没有可呈现的宿主了:动画一秒都不该再转(页面被回收/虚拟化时走这条路)。
        // 这里**只压不记** —— Desired 保留着,重新挂回视觉树时由 Apply 还原。
        SetSuppressed(bar, state, false);
    }

    private static void Subscribe(ProgressBar bar, GateState state)
    {
        state.TopLevel = TopLevel.GetTopLevel(bar);
        if (state.TopLevel is null) return;

        state.HostHandler ??= (_, args) =>
        {
            // 只看这两个:窗口最小化/隐藏是唯一会让"动画白跑"的状态变化。
            if (args.Property == Visual.IsVisibleProperty || args.Property == Window.WindowStateProperty)
                Apply(bar, state);
        };
        state.TopLevel.PropertyChanged -= state.HostHandler;
        state.TopLevel.PropertyChanged += state.HostHandler;
    }

    private static void Unsubscribe(ProgressBar bar, GateState state)
    {
        if (state.TopLevel is not null && state.HostHandler is not null)
            state.TopLevel.PropertyChanged -= state.HostHandler;
        state.TopLevel = null;
    }

    private static void Apply(ProgressBar bar, GateState state)
    {
        var topLevel = state.TopLevel;
        // 与 ProgressRenderAnimator.IsHostPresentable 同一条判据:最小化时 Window.IsVisible 仍为 true。
        var presentable = topLevel is not null
                          && topLevel.IsVisible
                          && (topLevel is not Window window || window.WindowState != WindowState.Minimized);
        SetSuppressed(bar, state, presentable && state.Desired);
    }

    /// <summary>本类自己写 <c>IsIndeterminate</c> 时必须打标记,否则会被
    /// <see cref="OnBarPropertyChanged"/> 误当成"绑定改了值"而覆盖掉 <see cref="GateState.Desired"/>。</summary>
    private static void SetSuppressed(ProgressBar bar, GateState state, bool value)
    {
        if (bar.IsIndeterminate == value) return;
        state.Suppressing = true;
        try { bar.IsIndeterminate = value; }
        finally { state.Suppressing = false; }
    }
}
