using Avalonia.Controls;

namespace ALyricEase.Infrastructure;

/// <summary>统一的宽度状态；视图只通过伪类表达布局，便于桌面、平板和 Android 共用。</summary>
internal static class ResponsiveClasses
{
    internal const double CompactWidth = 900;
    internal const double NarrowWidth = 641;

    internal static void Apply(Control control, double width)
    {
        control.Classes.Set("wide", width >= CompactWidth);
        control.Classes.Set("compact", width >= NarrowWidth && width < CompactWidth);
        control.Classes.Set("narrow", width > 0 && width < NarrowWidth);
    }
}
