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
    bool HasMore = false,
    UserProfilePageCacheData? UserProfile = null,
    IReadOnlyList<NavigationPageCacheArtist>? Artists = null,
    IReadOnlyList<NavigationPageCacheUser>? RelationUsers = null,
    // RelationStage:QQ 关注名单的翻页段(0 = 关注用户,1 = 关注歌手);网易云不用。
    int RelationStage = 0);

internal sealed record UserProfilePageCacheData(
    string Nickname, string AvatarUrl, string Signature, int Level, int Follows, int Followeds,
    IReadOnlyList<UserPlaylistCacheItem> Taste,
    IReadOnlyList<UserPlaylistCacheItem> Created,
    IReadOnlyList<UserPlaylistCacheItem> Collected,
    int AccountGeneration, int PlaylistRevision);

internal sealed record UserPlaylistCacheItem(
    Playlist Playlist, string? CreatorName, long CreatorId, string CoverUrl,
    int TrackCount, double PlayCount, bool ShowHeart = false);

internal sealed record NavigationPageCacheTrack(
    Song Song,
    bool IsPlayable,
    bool IsQueued = true,
    bool IsPlaybackUnavailable = false,
    bool PreferCachedPlayback = false,
    Playlist? MemberPlaylist = null,
    LocalPlaylist? MemberLocal = null);

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

/// <summary>关注/粉丝名单里的一行(返回列表页时免联网重建)。QQ 条目没有数字 uid:
/// 点行靠 EncryptUin(用户)或 SingerMid(歌手)定位,所以三者都要存。</summary>
internal sealed record NavigationPageCacheUser(
    long UserId,
    string Nickname,
    string AvatarUrl,
    string Signature,
    int Follows,
    int Followeds,
    MusicSource Source = MusicSource.NetEase,
    string EncryptUin = "",
    string SingerMid = "");
