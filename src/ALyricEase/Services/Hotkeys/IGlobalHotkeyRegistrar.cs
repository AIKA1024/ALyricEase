using Avalonia.Input;

namespace ALyricEase.Services.Hotkeys;

/// <summary>
/// 系统级热键注册器(平台实现)。
/// <para>
/// 应用内分发只在该窗口有键盘焦点时收得到按键;<see cref="GlobalHotkeyHost"/> 通过本接口
/// 把勾选了「全局」的动作注册到操作系统,应用在后台(甚至最小化到托盘)也能响应。
/// </para>
/// <para>
/// 契约:一个动作最多占一个组合,重复 <see cref="TryRegister"/> 同一个动作视为替换;
/// 注册失败(被其他程序占用)返回 false 且不留半成品,调用方可回落为应用内生效。
/// </para>
/// </summary>
public interface IGlobalHotkeyRegistrar
{
    /// <summary>占住 <paramref name="gesture"/>;失败返回 false(通常是组合已被占用)。</summary>
    bool TryRegister(string actionId, HotkeyGesture gesture);

    /// <summary>释放该动作占用的组合(未注册时为空操作)。</summary>
    void Unregister(string actionId);

    /// <summary>释放全部组合。</summary>
    void UnregisterAll();
}

/// <summary>全局热键的可行性规则(平台无关,UI 与注册器共用)。</summary>
public static class GlobalHotkeyRules
{
    private const KeyModifiers Supported = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

    /// <summary>
    /// 能否注册成系统级热键。<c>RegisterHotKey</c> 其实允许不带修饰键,但那样会把这个键
    /// 从所有程序手里抢走 —— 空格、字母、Insert、PrintScreen 实测都能占上,占了用户就没法打字/编辑/截图。
    /// 所以无修饰键只在白名单里放行:<see cref="IsSafeBareKey"/>。
    /// </summary>
    public static bool CanRegister(HotkeyGesture gesture)
        => !gesture.IsEmpty && (HasModifier(gesture.Modifiers) || IsSafeBareKey(gesture.Key));

    /// <summary>是否带 Ctrl / Alt / Shift 之一。</summary>
    public static bool HasModifier(KeyModifiers modifiers) => (modifiers & Supported) != 0;

    /// <summary>
    /// 可以单独当全局热键的键:不产生字符,也不承担常用功能。
    /// <para>
    /// Pause / ScrollLock 平时没人按,媒体键本来就该由播放器接管。
    /// 空格、字母、数字、Insert、PrintScreen、音量键虽然也能注册成功,但会抢走别人的常用键,一律不放行。
    /// </para>
    /// </summary>
    public static bool IsSafeBareKey(Key key) => key is
        Key.Pause or Key.Scroll
        or Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop;
}
