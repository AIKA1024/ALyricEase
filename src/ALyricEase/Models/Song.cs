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

    /// <summary>歌手 id 列表(点击歌手跳歌手页;显示名用 Artist)。</summary>
    public IReadOnlyList<long> ArtistIds { get; init; } = Array.Empty<long>();

    /// <summary>歌手名列表(多歌手时子菜单逐项用,与 ArtistIds 一一对应)。</summary>
    public IReadOnlyList<string> ArtistNames { get; init; } = Array.Empty<string>();

    /// <summary>专辑 id(点击专辑跳专辑页;显示名用 Album)。</summary>
    public long AlbumId { get; init; }
}
