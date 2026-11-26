using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ALyricEase.Infrastructure;

/// <summary>触控"长按弹菜单"之后的误点击护栏。
///
/// Avalonia 只在手势层做了区分:长按进入 Holding 之后抬手只发 RightTapped、不发 Tapped
/// (Gestures.cs 的 PointerReleased 分支),所以订阅 Tapped 的控件天生安全。但 Button.Click
/// 完全不参与手势层 —— Button.OnPointerReleased 只看自己的 IsPressed,长按期间 IsPressed 一直
/// 是 true,松手照样 OnClick() → Command(Avalonia 12.2 的 Button.cs 里 Context/Holding 零引用)。
/// 官方没有"长按后不点击"的开关,ContextFlyout 也一样,因为 Button 不关心菜单是谁弹的。
///
/// 这里在长按成立时主动放弃指针捕获:PointerCaptureLost → Button.OnPointerCaptureLost 把
/// IsPressed 置 false,抬手就进不了 OnClick()。与 PreventGestureRecognition() 无关,
/// 那只短路手势识别器(滚动/拖拽),压不住 Button。
///
/// 用法:事件路由必经的根(页面/卡片控件)构造时 <see cref="Attach"/>,
/// ContextRequested 里<b>在弹菜单之前</b>调 <see cref="CancelPress"/>。</summary>
internal static class LongPressClickGuard
{
    private static readonly ConditionalWeakTable<Control, State> States = new();

    private sealed class State
    {
        public IPointer? Pressed;
    }

    /// <summary>挂到事件路由必经的根上。Button 会把 PointerPressed 标 Handled,
    /// 所以必须 handledEventsToo:true 才记得到指针。</summary>
    public static void Attach(Control root)
    {
        var state = States.GetOrCreateValue(root);
        root.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => state.Pressed = e.Pointer, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(InputElement.PointerReleasedEvent,
            (_, _) => state.Pressed = null, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>长按已弹菜单:放开捕获,让 Button 自己退出按下态。鼠标右键不走这条路
    /// (桌面右键的 ContextRequested 在抬起时才发,此时 Button 只看左键,本来就不会点)。</summary>
    public static void CancelPress(Control root)
    {
        if (!States.TryGetValue(root, out var state)) return;
        if (state.Pressed is not { } pointer || pointer.Type == PointerType.Mouse) return;
        state.Pressed = null;
        pointer.Capture(null);
    }
}
