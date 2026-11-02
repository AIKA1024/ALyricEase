using Avalonia.Input;

namespace ALyricEase.Services.Hotkeys;

/// <summary>一个可绑定的按键组合(键 + 修饰键)。
/// <para>
/// 只支持 Ctrl / Shift / Alt 三种修饰键:带 Win(Meta)的组合属于系统保留,
/// 应用抢不到也不该抢;录音式改键同样拒收,避免"录进去了却永远不触发"。
/// </para>
/// <para>
/// 落盘格式是 <c>Control+Shift+Left</c>(键名取 <see cref="Key"/> 枚举名,与界面语言无关),
/// 界面显示格式是 <c>Ctrl + Shift + ←</c>。两种格式分开,改显示文案不会动已存配置。
/// </para>
/// </summary>
public readonly record struct HotkeyGesture(Key Key, KeyModifiers Modifiers)
{
    /// <summary>本应用支持的修饰键范围。</summary>
    public const KeyModifiers SupportedModifiers =
        KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

    public static readonly HotkeyGesture None = new(Key.None, KeyModifiers.None);

    public bool IsEmpty => Key == Key.None;

    /// <summary>单独按下不能当作快捷键的键:纯修饰键、Tab(焦点导航)、以及系统键。</summary>
    public static bool IsBindableKey(Key key) => Enum.IsDefined(key) && key switch
    {
        Key.None => false,
        Key.LeftCtrl or Key.RightCtrl => false,
        Key.LeftShift or Key.RightShift => false,
        Key.LeftAlt or Key.RightAlt => false,
        Key.LWin or Key.RWin => false,
        Key.Tab => false,
        Key.Escape => false,
        Key.CapsLock or Key.NumLock or Key.Scroll => false,
        _ => true,
    };

    public static HotkeyGesture Create(Key key, KeyModifiers modifiers)
        => new(key, modifiers & SupportedModifiers);

    /// <summary>严格相等匹配:多按一个 Shift 就不再命中,避免 Ctrl+← 与 Ctrl+Shift+← 互相抢。</summary>
    public bool Matches(Key key, KeyModifiers modifiers)
        => !IsEmpty && Key == key && Modifiers == modifiers;

    public bool IsValid => IsBindableKey(Key) && (Modifiers & ~SupportedModifiers) == 0
        && !(Modifiers.HasFlag(KeyModifiers.Alt) && Key == Key.F4);

    /// <summary>解析落盘格式;无法识别时返回 false(配置里写坏了就退回默认,不崩)。</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var key = Key.None;
        var modifiers = KeyModifiers.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0) return false;
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Control;
                continue;
            }

            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Shift;
                continue;
            }

            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Alt;
                continue;
            }

            // 出现第二个非修饰键 ⇒ 不是本应用写出的格式(比如 "Ctrl+A+B")
            if (key != Key.None) return false;
            // 纯数字("123")会被 Enum.TryParse 当成枚举值接受,必须先确认是名字
            if (!char.IsLetter(part[0]) || part.Contains(',')) return false;
            if (!Enum.TryParse<Key>(part, ignoreCase: true, out var parsed)) return false;
            if (!IsBindableKey(parsed)) return false;
            key = parsed;
        }

        var candidate = new HotkeyGesture(key, modifiers);
        if (!candidate.IsValid) return false;
        gesture = candidate;
        return true;
    }

    /// <summary>落盘格式(修饰键顺序固定,便于比较与人工排查)。</summary>
    public string ToStorageString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Control");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }

    /// <summary>界面显示格式。</summary>
    public string ToDisplayString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(FormatKeyName(Key));
        return string.Join(" + ", parts);
    }

    /// <summary>键帽文案:字母/数字直接给字符,方向键用箭头,其余走枚举名。</summary>
    private static string FormatKeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + ((int)key - (int)Key.D0))).ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"小键盘 {(int)key - (int)Key.NumPad0}",
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "空格",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Back => "Backspace",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        _ => key.ToString(),
    };
}
