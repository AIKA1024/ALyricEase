namespace ALyricEase.Models;

/// <summary>一行歌词。Translation 来自 tlyric,按时间轴对齐合并(解析后可变)。</summary>
public sealed record LyricLine(TimeSpan Time, string Original)
{
    /// <summary>翻译(可能为 null),由 LrcParser 合并后设置。</summary>
    public string? Translation { get; set; }

    /// <summary>逐字时间轴(来自 YRC 或 QRC)。null = 该行没有逐字数据,按整行高亮。
    /// 文本取自 yrc 原串,与 <see cref="Original"/> 可能有标点差异(全角括号、和声括号被剥)。</summary>
    public IReadOnlyList<LyricWord>? Words { get; set; }

    public long TimeMs => (long)Time.TotalMilliseconds;

    public bool HasTranslation => !string.IsNullOrEmpty(Translation);

    public bool HasWordTiming => Words is { Count: > 0 };

    /// <summary>逐字文本(Words 拼接)。无逐字时退回 Original —— 逐字渲染应使用这个而不是 Original。</summary>
    public string SyllableText => HasWordTiming ? string.Concat(Words!.Select(w => w.Text)) : Original;
}
