namespace ALyricEase.Models;

/// <summary>领域歌曲模型(搜索/歌单/详情统一映射到它)。</summary>
public sealed class Song
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>展示用:多个艺术家以 / 拼接。</summary>
    public string Artist { get; init; } = "";

    public string Album { get; init; } = "";

    public string CoverUrl { get; init; } = "";

    /// <summary>时长,毫秒。</summary>
    public int DurationMs { get; init; }

    /// <summary>0 免费,其余为 VIP/付费(展示用)。</summary>
    public int Fee { get; init; }
}
