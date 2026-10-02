using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>详情页的一次性重数据快照；导航历史只持有对应缓存键与轻量定位参数。</summary>
internal sealed record DetailPageCacheData(
    IReadOnlyList<NavigationPageCacheTrack> Tracks,
    IReadOnlyList<NavigationPageCacheAlbum> Albums,
    IReadOnlyList<NavigationPageCacheAlbum> Singles,
    string Name = "",
    string Subtitle = "",
    string AvatarUrl = "",
    string ArtistName = "",
    string TrackCountText = "",
    long PublishTimeMs = 0,
    string Description = "",
    string CoverUrl = "",
    NavigationPageCacheArtist? PrimaryArtist = null,
    int Offset = 0,
    int Total = -1,
    bool HasMore = false);

internal sealed record NavigationPageCacheTrack(
    Song Song,
    bool IsPlayable,
    bool IsQueued = true,
    bool IsPlaybackUnavailable = false,
    bool PreferCachedPlayback = false);

internal sealed record NavigationPageCacheAlbum(
    long Id,
    string Title,
    string CoverUrl,
    string Mid,
    long PublishTimeMs = 0,
    int SongCount = 0);

internal sealed record NavigationPageCacheArtist(
    MusicSource Source,
    long NetEaseId,
    string QqMid,
    string Name);
