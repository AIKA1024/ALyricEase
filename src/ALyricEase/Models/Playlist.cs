namespace ALyricEase.Models;

/// <summary>歌单(列表项)。</summary>
public sealed class Playlist
{
    public long Id { get; init; }

    /// <summary>来源音源(侧边栏分组与打开路由用)。默认网易云。</summary>
    public Services.MusicSource Source { get; init; } = Services.MusicSource.NetEase;

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public int TrackCount { get; init; }

    public string CoverUrl { get; init; } = "";
}
