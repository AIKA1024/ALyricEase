using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 为常驻在壳层中的窗口内模态视图协调淡入淡出与真实可见性。
/// <para>
/// <see cref="IsOpenProperty"/> 是业务期望状态；<see cref="Visual.IsVisible"/> 是呈现状态：
/// 打开时先在动画优先级写入首帧透明度，再显示并淡入；关闭时先禁用输入、淡出，完成后才折叠。
/// 这样既不会先闪出 100% 不透明的首帧，也不会让已关闭的弹窗继续参与布局、快捷键和动画。
/// </para>
/// </summary>
public static class ModalVisibilityTransition
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(150);

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
            ApplyClosedState();
        }

        private async Task OpenAsync()
        {
            var from = _target.IsVisible ? _target.Opacity : 0d;
            var (generation, token) = BeginTransition();
            // 淡入是 UI 线程 styling 动画,每帧推进都要 UI 线程醒着;静息时长睡的线程
            // 只会被外部投递唤醒(实测 WM_TIMER/渲染帧请求都唤不醒),不持帧泵的话
            // 150ms 的过渡会被拉长到秒级,表现为"弹窗定住一秒后瞬间消失"。
            UiFramePacer.Acquire();
            try
            {
                _target.IsEnabled = true;
                _target.IsHitTestVisible = true;

                // 基值先落在终态；显式动画以 Animation 优先级暂时覆盖为 from。
                // RunAsync 会同步发布 0% 关键帧，所以随后打开 IsVisible 时首帧已经是透明态。
                _target.Opacity = 1d;
                var animation = CreateOpacityAnimation(from, 1d, GetDuration(_target));
                var animationTask = animation.RunAsync(_target, token);
                _target.IsVisible = true;

                await animationTask;
                if (!IsCurrent(generation, token) || !GetIsOpen(_target))
                {
                    return;
                }

                _target.Opacity = 1d;
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
            // 同 OpenAsync:淡出期间必须持帧泵,否则静息时 UI 线程长睡,动画停在
            // 半路不推进,弹窗以完全不透明状态"卡"到下一次唤醒才消失。
            UiFramePacer.Acquire();
            try
            {
                _target.Opacity = 0d;
                await CreateOpacityAnimation(from, 0d, GetDuration(_target)).RunAsync(_target, token);
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
