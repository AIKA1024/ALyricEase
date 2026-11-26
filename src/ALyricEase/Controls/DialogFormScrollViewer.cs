using Avalonia.Controls;

namespace ALyricEase.Controls;

public sealed class DialogFormScrollViewer : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);
        // 输入框先获得焦点，软键盘随后才缩小视口，此时不会再次触发焦点滚动。
        if (e.ViewportDelta.Y < 0 && IsKeyboardFocusWithin &&
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox field)
            field.BringIntoView();
    }
}
