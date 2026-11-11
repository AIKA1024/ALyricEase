using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 为常驻在壳层中的窗口内模态视图协调淡入淡出、卡片缩放与真实可见性。
/// <para>
/// <see cref="IsOpenProperty"/> 是业务期望状态；<see cref="Visual.IsVisible"/> 是呈现状态：
/// 打开时先在动画优先级写入首帧状态，再显示并动画入场；关闭时先禁用输入、播完退场动画，完成后才折叠。
/// 这样既不会先闪出 100% 不透明的首帧，也不会让已关闭的弹窗继续参与布局、快捷键和动画。
/// </para>
/// <para>
/// 动效对齐 Fluent 2 的 ContentDialog(WinUI 3 模板)：淡入淡出 83ms 线性(遮罩与卡片同一时长)、
/// 卡片 <c>scale 1.05 → 1</c> 250ms 减速入场 / <c>1 → 1.05</c> 167ms 加速退场。
/// 缩放只作用在内容卡片上 —— 整层缩放会把窗口边缘透出来。
/// </para>
/// <para>
/// 缩放目标按项目既有约定取名为 <c>DialogCard</c> 的子元素(多数弹窗的卡片就叫这个名字,
/// 其中几个的 code-behind 也在用它算尺寸)。找不到该名称时退化为纯淡入淡出,不报错。
/// </para>
/// </summary>
public static class ModalVisibilityTransition
{
    /// <summary>淡入淡出时长(Fluent 2 的 ContentDialog:83ms 线性)。</summary>
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(83);

    /// <summary>入场缩放:250ms 减速,1.05 → 1。</summary>
    private static readonly TimeSpan EnterScaleDuration = TimeSpan.FromMilliseconds(250);
    private const double EnterScaleFrom = 1.05;

    /// <summary>退场缩放:167ms 加速,1 → 1.05。</summary>
    private static readonly TimeSpan ExitScaleDuration = TimeSpan.FromMilliseconds(167);
    private const double ExitScaleTo = 1.05;

    /// <summary>做缩放的卡片元素名(项目既有约定,见类型注释)。</summary>
    private const string SurfacePartName = "DialogCard";

    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsOpen", typeof(ModalVisibilityTransition));

    public static readonly AttachedProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.RegisterAttached<Control, TimeSpan>(
            "Duration", typeof(ModalVisibilityTransition), DefaultDuration);

    /// <summary>关闭期间保留启用外观；指针命中与键盘输入单独拦截，隐藏后再禁用控件。</summary>
    public static readonly AttachedProperty<bool> PreserveEnabledDuringCloseProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("PreserveEnabledDuringClose", typeof(ModalVisibilityTransition));

    private static readonly ConditionalWeakTable<Control, TransitionState> States = new();

    static ModalVisibilityTransition()
    {
        IsOpenProperty.Changed.AddClassHandler<Control>(OnIsOpenChanged);
    }

    public static bool GetIsOpen(Control control) => control.GetValue(IsOpenProperty);

    public static void SetIsOpen(Control control, bool value) => control.SetValue(IsOpenProperty, value);

    public static TimeSpan GetDuration(Control control) => control.GetValue(DurationProperty);

    public static void SetDuration(Control control, TimeSpan value) => control.SetValue(DurationProperty, value);

    public static bool GetPreserveEnabledDuringClose(Control control) => control.GetValue(PreserveEnabledDuringCloseProperty);

    public static void SetPreserveEnabledDuringClose(Control control, bool value) => control.SetValue(PreserveEnabledDuringCloseProperty, value);

    /// <summary>等待当前显隐动画完成，供宿主在淡出后隐藏窗口或退出应用。</summary>
    public static Task WaitForTransitionAsync(Control control) =>
        States.TryGetValue(control, out var state) ? state.TransitionTask : Task.CompletedTask;

    private static void OnIsOpenChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        var state = States.GetValue(control, static target => new TransitionState(target));
        state.Apply(args.GetNewValue<bool>());
    }

    private sealed class TransitionState
    {
        private readonly Control _target;
        private CancellationTokenSource? _cancellation;
        private long _generation;

        /// <summary>缓存的缩放目标；Detach 时清空(视觉树重建后需重新按名称解析)。</summary>
        private Control? _surface;

        public Task TransitionTask { get; private set; } = Task.CompletedTask;

        public TransitionState(Control target)
        {
            _target = target;
            _target.AttachedToVisualTree += OnAttached;
            _target.DetachedFromVisualTree += OnDetached;
            _target.AddHandler(InputElement.KeyDownEvent, BlockClosingInput, RoutingStrategies.Tunnel);
            _target.AddHandler(InputElement.KeyUpEvent, BlockClosingInput, RoutingStrategies.Tunnel);
            _target.AddHandler(InputElement.TextInputEvent, BlockClosingInput, RoutingStrategies.Tunnel);

            // 壳层在 XAML 中也显式给 IsVisible=False；这里再固定输入与透明度，
            // 让 IsOpen 的初值即使在 attach 前到达，也绝不会产生可见的末帧。
            ApplyClosedState();
        }

        public void Apply(bool isOpen)
        {
            if (!_target.IsAttachedToVisualTree())
            {
                CancelTransition();
                ApplyClosedState();
                return;
            }

            TransitionTask = isOpen ? OpenAsync() : CloseAsync();
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Apply(GetIsOpen(_target));

        private void BlockClosingInput(object? sender, RoutedEventArgs e)
        {
            // IsHitTestVisible 不影响键盘。保持焦点但拦截事件，避免 Enter/Space/Tab
            // 在淡出时激活仍保留启用外观的按钮，或把焦点移到背景页。
            if (GetPreserveEnabledDuringClose(_target) && !GetIsOpen(_target)) e.Handled = true;
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            CancelTransition();
            _surface = null;
            ApplyClosedState();
        }

        private async Task OpenAsync()
        {
            var from = _target.IsVisible ? _target.Opacity : 0d;
            var (generation, token) = BeginTransition();
            // 淡入与缩放都是 UI 线程 styling 动画,每帧推进都要 UI 线程醒着;静息时长睡的线程
            // 只会被外部投递唤醒(实测 WM_TIMER/渲染帧请求都唤不醒),不持帧泵的话
            // 83ms 的过渡会被拉长到秒级,表现为"弹窗定住一秒后瞬间消失"。
            UiFramePacer.Acquire();
            try
            {
                _target.IsEnabled = true;
                _target.IsHitTestVisible = true;

                // 基值先落在终态；显式动画以 Animation 优先级暂时覆盖为 from。
                // RunAsync 会同步发布 0% 关键帧，所以随后打开 IsVisible 时首帧已经是透明态。
                _target.Opacity = 1d;
                var opacityTask = CreateOpacityAnimation(from, 1d, GetDuration(_target)).RunAsync(_target, token);
                var scaleTask = RunScaleAsync(EnterScaleFrom, 1d, EnterScaleDuration, token);
                _target.IsVisible = true;

                await Task.WhenAll(opacityTask, scaleTask);
                if (!IsCurrent(generation, token) || !GetIsOpen(_target))
                {
                    return;
                }

                _target.Opacity = 1d;
                SetSurfaceScale(1d);
            }
            finally
            {
                UiFramePacer.Release();
            }
        }

        private async Task CloseAsync()
        {
            var from = _target.Opacity;

            // 退出动画期间仍可见，但立即退出键盘/指针交互，避免透明弹窗的
            // Escape/Enter 热键或按钮继续响应。
            _target.IsHitTestVisible = false;
            if (!GetPreserveEnabledDuringClose(_target)) _target.IsEnabled = false;

            if (!_target.IsVisible)
            {
                CancelTransition();
                ApplyClosedState();
                return;
            }

            var (generation, token) = BeginTransition();
            // 同 OpenAsync:退场期间必须持帧泵,否则静息时 UI 线程长睡,动画停在
            // 半路不推进,弹窗以完全不透明状态"卡"到下一次唤醒才消失。
            UiFramePacer.Acquire();
            try
            {
                _target.Opacity = 0d;
                var opacityTask = CreateOpacityAnimation(from, 0d, GetDuration(_target)).RunAsync(_target, token);
                // 缩放的 167ms 比淡出的 83ms 长 ⇒ Fluent 的观感是"已经透明还在继续放大",
                // 两者并发、等最长的那个收尾。
                var scaleTask = RunScaleAsync(1d, ExitScaleTo, ExitScaleDuration, token);

                await Task.WhenAll(opacityTask, scaleTask);
                if (!IsCurrent(generation, token) || GetIsOpen(_target))
                {
                    return;
                }

                ApplyClosedState();
            }
            finally
            {
                UiFramePacer.Release();
            }
        }

        private (long Generation, CancellationToken Token) BeginTransition()
        {
            var current = _target.Opacity;
            CancelTransition();
            _target.Opacity = current;

            _cancellation = new CancellationTokenSource();
            return (++_generation, _cancellation.Token);
        }

        private bool IsCurrent(long generation, CancellationToken token) =>
            generation == _generation && !token.IsCancellationRequested;

        private void CancelTransition()
        {
            if (_cancellation is null)
            {
                return;
            }

            _cancellation.Cancel();
            _cancellation.Dispose();
            _cancellation = null;
        }

        private void ApplyClosedState()
        {
            // 先折叠再禁用，避免最后一帧绘制整棵控件树的 :disabled 样式。
            _target.IsVisible = false;
            _target.IsHitTestVisible = false;
            _target.IsEnabled = false;
            _target.Opacity = 0d;
            // 缩放复位成 1:留 1.05 会让下次打开前的那一帧"卡片略大",退场基值也不能带进下一次。
            SetSurfaceScale(1d);
        }

        // ---- 卡片缩放 ----

        /// <summary>按名称解析缩放目标(项目约定 <c>DialogCard</c>);找不到就没有缩放这一步。</summary>
        private Control? Surface
        {
            get
            {
                if (_surface is not null) return _surface;
                // 构造期(尚未 attach)NameScope 还没建立,查到 null 不缓存,attach 后再试。
                _surface = _target.FindControl<Control>(SurfacePartName);
                return _surface;
            }
        }

        /// <summary>
        /// 逐帧推进卡片缩放。
        /// <para>
        /// 为什么不用 <c>Animation</c>:Avalonia 12 里动 <c>RenderTransform</c> 需要
        /// <c>TransformAnimator</c>,而它是 internal(CS0122);把动画目标换成 ScaleTransform
        /// 又会被 TransformAnimator 强转成 Visual 抛 InvalidCastException。
        /// </para>
        /// <para>
        /// 为什么不用 <c>Transitions</c>:它是隐式触发的,拿不到完成时机,而且同一帧内
        /// "先置起点、再置终点"会被合并成一次变化,起点直接丢掉。
        /// </para>
        /// 16ms 一帧、只改 RenderTransform(不触发布局,只重绘),和项目里 HeroAdvanceRing 同路数。
        /// </summary>
        /// <remarks>
        /// ⚠ 曲线统一用减速(<see cref="ExponentialEaseOut"/>),**退场也不照抄 Fluent 的 accelerate**。
        /// 83ms 的淡出会在 accelerate 的"起步极慢"段就结束:实测退场 40ms 处只放大 0.13%
        /// (此时不透明度已剩 43%)、80ms 处 0.58%(已全透明)—— 等于没有缩放。
        /// 减速曲线同样 40ms 能放大 4.3%,才真的看得见"卡片胀开消失"。
        /// </remarks>
        private async Task RunScaleAsync(double from, double to, TimeSpan duration, CancellationToken token)
        {
            var surface = Surface;
            if (surface is null) return;

            var easing = new ExponentialEaseOut();
            surface.RenderTransformOrigin = RelativePoint.Center;
            surface.RenderTransform = ScaleOperations(from);

            var total = duration.TotalMilliseconds;
            var timer = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    var progress = Math.Clamp(timer.Elapsed.TotalMilliseconds / total, 0d, 1d);
                    surface.RenderTransform = ScaleOperations(from + ((to - from) * easing.Ease(progress)));
                    if (progress >= 1d) return;
                    await Task.Delay(16, token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                // 被新的开合切换取消:交给对方收尾,不向上抛(否则 WaitForTransitionAsync 会炸)。
            }
        }

        private void SetSurfaceScale(double value)
        {
            if (Surface is null) return;
            Surface.RenderTransform = ScaleOperations(value);
        }

        /// <summary>把单个缩放包成可插值的 <see cref="TransformOperations"/>。</summary>
        private static TransformOperations ScaleOperations(double value)
        {
            var builder = TransformOperations.CreateBuilder(1);
            builder.AppendScale(value, value);
            return builder.Build();
        }

        private static Animation CreateOpacityAnimation(double from, double to, TimeSpan duration) => new()
        {
            Duration = duration,
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(Visual.OpacityProperty, from) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(Visual.OpacityProperty, to) },
                },
            },
        };
    }
}
