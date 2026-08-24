namespace ALyricEase.Models;

/// <summary>一行歌词。Translation 来自 tlyric,按时间轴对齐合并(解析后可变)。</summary>
public sealed record LyricLine(TimeSpan Time, string Original)
{
    /// <summary>翻译(可能为 null),由 LrcParser 合并后设置。</summary>
    public string? Translation { get; set; }

    public long TimeMs => (long)Time.TotalMilliseconds;

    public bool HasTranslation => !string.IsNullOrEmpty(Translation);
}
