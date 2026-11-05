using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 元素**从隐藏变可见**时，在合成层跑一次快速淡入（默认 180ms，<c>SineEaseOut</c>）。
///
/// 场景：整页加载态切内容时只切 `IsVisible`，内容会"啪"地整个出现（用户原话：突然进页面有点突兀）。
/// 想要透明度过渡，但**不能用 XAML <c>Transitions</c>**：它是 UI 线程逐帧推进的，
/// 静息时长睡的 UI 线程会把 150ms 拖成 ~1s（见 <see cref="ModalVisibilityTransition"/> 的实测），
/// 还得为此持有 <see cref="UiFramePacer"/>。合成动画由渲染线程驱动，不占 UI 线程，时长稳定。
///
/// ⚠ 两处顺序都不能错（都实测踩过，真窗口抓屏序列见 docs/user-page.md §3）：
/// <list type="number">
/// <item>压透明要在 <c>IsVisible</c> 变 true 的**属性回调里同步**做 <c>control.Opacity = 0</c>。
///   渲染读的就是 UI 层这个值；只在 <see cref="DispatcherPriority.Render"/> 的作业里压合成基值，
///   作业一旦晚于那次渲染就会先画一帧**全亮**再跳回透明（实测闪 2 帧）。</item>
/// <item>收尾时先抬 UI 层 <c>Opacity</c>、再 <c>StopAnimation</c>，反了会有一帧回落到 0。
///   基值必须写回 1：留着 0，之后任何 <c>StopAnimation("Opacity")</c>（页面过渡、模态弹层都会调）
///   会把整块拉成透明。</item>
/// </list>
///
/// 用法：<c>&lt;StackPanel infra:FadeInWhenShown.IsEnabled="True" IsVisible="{Binding !IsLoading}"&gt;</c>
/// </summary>
public static class FadeInWhenShown
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(FadeInWhenShown));

    /// <summary>淡入时长，默认 180ms（"快速"档：看得出柔和，又不拖节奏）。</summary>
    public static readonly AttachedProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.RegisterAttached<Control, TimeSpan>(
            "Duration", typeof(FadeInWhenShown), TimeSpan.FromMilliseconds(180));

    static FadeInWhenShown()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnEnabledChanged);
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    public static TimeSpan GetDuration(Control control) => control.GetValue(DurationProperty);

    public static void SetDuration(Control control, TimeSpan value) => control.SetValue(DurationProperty, value);

    private static readonly ConditionalWeakTable<Control, FadeState> States = new();

    private sealed class FadeState
    {
        public EventHandler<AvaloniaPropertyChangedEventArgs>? Watcher;

        /// <summary>代际号：快速反复显隐时，只有最后一次的"恢复基值"能落地。</summary>
        public int Generation;
    }

    private static void OnEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        var enabled = args.GetNewValue<bool>();
        var state = States.GetOrCreateValue(control);

        state.Watcher ??= (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty) return;
            if (!e.GetNewValue<bool>())
            {
                // 又隐藏了:作废在途的恢复任务,并把 UI 层不透明度留在 0(下次显示直接从这里淡入)
                state.Generation++;
                return;
            }

            // ⚠ 这一步必须**同步**做,不能像以前那样排到 Render 优先级再压基值。
            // 渲染读的是 UI 层的 Opacity;变可见后下一次渲染随时可能发生,
            // 排队的作业若晚于它,就会先画出**全亮**的一帧再跳回透明(实测连闪 2 帧,
            // 用户原话"还是一下子出来的,有过渡但很奇怪")。
            control.Opacity = 0d;
            // 合成视觉要等控件重新进渲染队列才有,排到 Render 优先级只用来起动画。
            Dispatcher.UIThread.Post(() => _ = PlayAsync(control, state), DispatcherPriority.Render);
        };

        control.PropertyChanged -= state.Watcher;
        if (!enabled)
        {
            state.Generation++; // 作废在途的恢复任务
            control.Opacity = 1d; // 关掉附加属性时别把控件留在透明态
            return;
        }

        control.PropertyChanged += state.Watcher;
    }

    private static async Task PlayAsync(Control control, FadeState state)
    {
        var generation = ++state.Generation;
        CompositionVisual? visual;
        try
        {
            visual = ElementComposition.GetElementVisual(control);
        }
        catch (ObjectDisposedException)
        {
            visual = null;
        }
        if (visual is null)
        {
            // 没有合成视觉(离树/被回收):不能把内容留在透明态 —— 直接显示。
            if (generation == state.Generation) control.Opacity = 1d;
            return;
        }

        var duration = control.GetValue(DurationProperty);
        try
        {
            // 基值仍压 0(UI 层已同步压过,这里同步合成层),动画就跑 0→1
            visual.Opacity = 0f;
            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = duration;
            animation.InsertKeyFrame(0f, 0f);
            // 曲线用 SineEaseOut 而不是 CubicEaseOut:Cubic 在 1/4 时长处就冲到 ~60%,
            // 观感是"闪一下就到位",用户仍会觉得"一下子出来"。Sine 在中段才过半,
            // 20~30fps 的采样下也能看出连续变化。
            animation.InsertKeyFrame(1f, 1f, new SineEaseOut());
            visual.StartAnimation("Opacity", animation);
        }
        catch (ObjectDisposedException)
        {
            if (generation == state.Generation) control.Opacity = 1d;
            return;
        }

        // 等动画走完再收尾（合成动画没有可 await 的完成回调）
        await Task.Delay(duration + TimeSpan.FromMilliseconds(32)).ConfigureAwait(true);
        if (generation != state.Generation) return;
        try
        {
            // ⚠ 顺序:先抬 UI 层基值,再摘合成动画。反了会有一帧回落到 0(内容闪一下变透明)。
            control.Opacity = 1d;
            visual.Opacity = 1f;
            visual.StopAnimation("Opacity");
        }
        catch (ObjectDisposedException)
        {
            // 控件已离树/合成视觉已回收：没什么可恢复的
        }
    }
}
