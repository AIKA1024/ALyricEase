namespace ALyricEase.Infrastructure;

/// <summary>输入模式约定(主要输入形态):由各 Head(App)在启动时声明一次,
/// 共享 UI(控件/样式)只消费此抽象,自身不做平台判断。桌面默认指针模式:
/// hover 浮现操作按钮、双击整行播放;触控模式(触屏 Head,如 Android/iOS):
/// 无 hover、操作按钮恒隐、单击整行播放。不直接用 OperatingSystem.IsAndroid():
/// 桌面也可能是触屏/平板,且平台判断应收敛在 Head 项目,共享库保持平台无关
/// (Headless 测试默认指针模式)。</summary>
public static class InteractionDefaults
{
    /// <summary>触控模式类名:TrackRow 构造时按 IsTouchPrimary 挂到控件上,
    /// 由 TrackRow.axaml 末尾的 touch 覆盖样式驱动"常显/常隐"等无 hover 差异。</summary>
    public const string TouchClass = "touch";

    /// <summary>触控是否为主要输入形态。默认 false(指针模式,桌面/Headless)。</summary>
    public static bool IsTouchPrimary { get; private set; }

    /// <summary>Head 必须在任何视图创建之前调用;触屏 Head(Android/iOS)传 true,桌面 Head 可不调(默认 false)。</summary>
    public static void Init(bool touchPrimary) => IsTouchPrimary = touchPrimary;
}