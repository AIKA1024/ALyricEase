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
/// 门控覆盖两类"白跑":①宿主不可显示(最小化/隐藏);②控件自己没被显示(含祖先 IsVisible=false)。
/// 判定口径与 `ProgressRenderAnimator.IsHostPresentable` 保持一致:
/// `TopLevel.IsVisible` 且非 `WindowState.Minimized`
/// (⚠️ 最小化时 `Window.IsVisible` 仍然是 `true`,只看它不够)。
///
/// ⚠ **2026-09-17 更正**(原注释写着"不再判控件自身可见性,实测过,不需要"——那次测量不成立):
/// 不确定进度条的循环动画**一旦跑起来过**,把外层容器 `IsVisible` 置 false **不会停它**。
/// 同进程三态配对实测(歌单页·可见,300 行,每态两个 8s 窗口):
///   隐藏 + 动画开 = **4.83%** 单核(每 8s 分配 7.2MB,与"可见+动画"同款指纹);
///   可见 + 动画开 = 5.61% / 4.25%;
///   可见 + 动画关 = 0.39% / 0.19%;
///   从未实化     = 0.39%。
/// ⇒ "不可见就不产生成本"**只在从未实化时成立**;Avalonia 对隐藏但仍挂在视觉树上的控件
/// 照旧驱动动画时钟(与"最小化时照转"是同一条机理)。这正是用户报的
/// "在歌单页什么都不做也有 CPU":提示条只要出现过一次,之后即使已隐藏也一直空转,
/// 直到页面离开视觉树。所以门控必须把"控件自己是否被有效显示"也计进来。
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

        /// <summary>已订阅 <c>IsVisibleProperty</c> 的自身+祖先(订阅集必须记住才能在脱离时解干净)。</summary>
        public readonly List<Visual> Watched = [];

        /// <summary>祖先链上任意一层 IsVisible 变化 → 重算该不该转。</summary>
        public EventHandler<AvaloniaPropertyChangedEventArgs>? VisibilityHandler;
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
        if (sender is not ProgressBar bar || !States.TryGetValue(bar, out var state)) return;
        if (args.Property != ProgressBar.IsIndeterminateProperty) return;
        if (state.Suppressing) return;
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
        if (state.TopLevel is not null)
        {
            state.HostHandler ??= (_, args) =>
            {
                // 窗口最小化/隐藏是"宿主不可显示"的两种状态变化。
                if (args.Property == Visual.IsVisibleProperty || args.Property == Window.WindowStateProperty)
                    Apply(bar, state);
            };
            state.TopLevel.PropertyChanged -= state.HostHandler;
            state.TopLevel.PropertyChanged += state.HostHandler;
        }

        // 控件自己 + **全部祖先**的 IsVisible:应用里"提示条不显示"是把外层容器绑成 false,
        // 控件自己 IsVisible 仍是 true,而 IsEffectivelyVisible 在 Avalonia 12 没有可订阅的公开属性
        // (只有 getter)⇒ 只能把整条链订阅下来,任何一层变了就重算。
        // 订阅集必须显式记住:DetachedFromVisualTree 之后祖先链已经断了,靠 GetVisualParent
        // 回走是收不回来的 —— 而那些祖先(壳层/窗口)比页面活得久,漏解就是真实泄漏。
        state.VisibilityHandler ??= (_, args) =>
        {
            if (args.Property == Visual.IsVisibleProperty) Apply(bar, state);
        };
        if (state.Watched.Count == 0)
        {
            for (Visual? node = bar; node is not null; node = node.GetVisualParent())
            {
                node.PropertyChanged += state.VisibilityHandler;
                state.Watched.Add(node);
            }
        }
    }

    private static void Unsubscribe(ProgressBar bar, GateState state)
    {
        if (state.TopLevel is not null && state.HostHandler is not null)
            state.TopLevel.PropertyChanged -= state.HostHandler;
        state.TopLevel = null;

        if (state.VisibilityHandler is not null)
            foreach (var node in state.Watched)
                node.PropertyChanged -= state.VisibilityHandler;
        state.Watched.Clear();
    }

    private static void Apply(ProgressBar bar, GateState state)
    {
        var topLevel = state.TopLevel;
        // 与 ProgressRenderAnimator.IsHostPresentable 同一条判据:最小化时 Window.IsVisible 仍为 true。
        var presentable = topLevel is not null
                          && topLevel.IsVisible
                          && (topLevel is not Window window || window.WindowState != WindowState.Minimized);
        // ⚠ 还要看**控件自己有没有被显示**(含祖先)。2026-09-17 实测:一条 3px 不确定进度条
        // 只要动画跑起来过,把外层 IsVisible 置 false **不会停它** —— 隐藏态仍吃 4.83% 单核
        // (与"可见+动画"同量级,每 8s 同样分配 7.2MB),而把它 IsIndeterminate 置 false 后
        // 立刻回到 0.19%~0.39%;从未显示过时是 0.39%。⇒ "不可见就不产生成本"只在**从未实化**时成立。
        var shown = bar.IsEffectivelyVisible;
        SetSuppressed(bar, state, presentable && shown && state.Desired);
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
