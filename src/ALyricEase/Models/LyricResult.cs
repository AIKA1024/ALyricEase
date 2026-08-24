namespace ALyricEase.Models;

/// <summary>歌词接口的原始返回(未解析的 LRC 文本)。</summary>
public sealed class LyricResult
{
    public string Original { get; init; } = "";

    public string Translation { get; init; } = "";
}
