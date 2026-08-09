namespace ALyricEase.Models;

/// <summary>歌单(列表项)。</summary>
public sealed class Playlist
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    public int TrackCount { get; init; }

    public string CoverUrl { get; init; } = "";
}
