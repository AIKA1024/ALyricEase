using Avalonia;
using Avalonia.Controls;
using ALyricEase.ViewModels;

namespace ALyricEase.Infrastructure;

/// <summary>统一的宽度状态；视图只通过伪类表达布局，便于桌面、平板和 Android 共用。</summary>
internal static class ResponsiveClasses
{
    internal const double CompactWidth = 900;
    internal const double NarrowWidth = 641;

    internal static void Apply(Control control, double width)
    {
        control.Classes.Set("wide", width >= CompactWidth);
        control.Classes.Set("compact", width is >= NarrowWidth and < CompactWidth);
        control.Classes.Set("narrow", width is > 0 and < NarrowWidth);
    }
}

/// <summary>汉堡按钮语义:宽屏切内联展开/收起,中/小屏切抽屉开关(原版 NavigationView 的 PaneToggle
/// 在 Expanded 模式收起/展开内联栏,在 Compact/Minimal 模式开合覆盖抽屉)。</summary>
internal static class NavigationHamburger
{
    internal static void Dispatch(Control host, MainViewModel? vm)
    {
        if (vm is null) return;
        var width = TopLevel.GetTopLevel(host)?.ClientSize.Width ?? double.PositiveInfinity;
        if (width >= ResponsiveClasses.CompactWidth)
            vm.ToggleNavigationExpandedCommand.Execute(null);
        else
            vm.ToggleNavigationDrawerCommand.Execute(null);
    }
}
