using Avalonia;
using Avalonia.Controls;
using ALyricEase.ViewModels;

namespace ALyricEase.Infrastructure;

/// <summary>统一的宽度状态；视图只通过伪类表达布局，便于桌面、平板和 Android 共用。</summary>
public static class ResponsiveClasses
{
    public const double CompactWidth = 900;
    public const double NarrowWidth = 641;

    public static void Apply(Control control, double width)
    {
        control.Classes.Set("wide", width >= CompactWidth);
        control.Classes.Set("compact", width is >= NarrowWidth and < CompactWidth);
        control.Classes.Set("narrow", width is > 0 and < NarrowWidth);
    }

    /// <summary>按顶层窗口宽度应用响应式类。侧边栏展开时内容区/播放条自身宽度小于窗口宽度,
    /// 用控件自身宽度当断点会在窗口还宽时就过早切小屏;断点常量与 AppShell 共用。</summary>
    public static void ApplyByWindow(Control control)
    {
        var width = TopLevel.GetTopLevel(control)?.ClientSize.Width ?? control.Bounds.Width;
        Apply(control, width);
    }
}

/// <summary>汉堡按钮语义:宽屏切内联展开/收起,中/小屏切抽屉开关(原版 NavigationView 的 PaneToggle
/// 在 Expanded 模式收起/展开内联栏,在 Compact/Minimal 模式开合覆盖抽屉)。</summary>
public static class NavigationHamburger
{
    public static void Dispatch(Control host, MainViewModel? vm)
    {
        if (vm is null) return;
        var width = TopLevel.GetTopLevel(host)?.ClientSize.Width ?? double.PositiveInfinity;
        if (width >= ResponsiveClasses.CompactWidth)
            vm.ToggleNavigationExpandedCommand.Execute(null);
        else
            vm.ToggleNavigationDrawerCommand.Execute(null);
    }
}
