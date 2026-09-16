using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>媒体缓存中的离线歌单索引。单独使用源生成上下文，兼容 Android 裁剪/AOT。</summary>
[JsonSerializable(typeof(MusicCacheIndexFile))]
[JsonSerializable(typeof(PlaylistTracksCacheFile))]
[JsonSerializable(typeof(PlaylistPageSnapshotFile))]
[JsonSerializable(typeof(DetailPageSnapshotFile))]
internal sealed partial class MusicCacheJsonContext : JsonSerializerContext
{
}

internal sealed class MusicCacheIndexFile
{
    public int Version { get; set; } = 1;

    public List<CachedAccountFile> Accounts { get; set; } = new();
}

internal sealed class CachedAccountFile
{
    public int Source { get; set; }

    public string UserName { get; set; } = "";

    /// <summary>区分“成功同步过账号歌单”与仅缓存过同音源公共歌单详情。</summary>
    public bool HasPlaylistList { get; set; }

    public List<CachedPlaylistFile> Playlists { get; set; } = new();
}

internal sealed class CachedPlaylistFile
{
    public long Id { get; set; }

    public long DirId { get; set; }

    public int Source { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public int TrackCount { get; set; }

    public string CoverUrl { get; set; } = "";

    public bool CanAddTracks { get; set; }

    /// <summary>是否来自账号歌单列表；公共歌单详情快照不会因此出现在侧栏。</summary>
    public bool Listed { get; set; }

    public List<CachedSongFile> Tracks { get; set; } = new();
}

/// <summary>从总索引拆出的按歌单曲目文件；读取后不在单例缓存服务中常驻。</summary>
internal sealed class PlaylistTracksCacheFile
{
    public int Version { get; set; } = 1;

    public List<CachedSongFile> Tracks { get; set; } = new();
}

/// <summary>页面离开时保存的一次性重数据快照；轻量导航历史只持有对应键。</summary>
internal sealed class PlaylistPageSnapshotFile
{
    public int Version { get; set; } = 1;

    public List<CachedPageTrackFile> Tracks { get; set; } = new();

    public long[] TrackIds { get; set; } = [];

    public int Materialized { get; set; }

    public CachedAggregateLoadStateFile? AggregateLoad { get; set; }
}

internal sealed class CachedPageTrackFile
{
    public CachedSongFile Song { get; set; } = new();

    public bool IsPlayable { get; set; }

    public bool IsQueued { get; set; }

    public bool IsPlaybackUnavailable { get; set; }

    public bool PreferCachedPlayback { get; set; }
}

internal sealed class CachedAggregateLoadStateFile
{
    public int MemberIndex { get; set; }

    public long[]? NetEaseTrackIds { get; set; }

    public List<CachedSongFile> NetEaseKnown { get; set; } = new();

    public int NetEaseCursor { get; set; }

    public int QqBegin { get; set; }

    public int FailedCount { get; set; }

    public bool CoverSet { get; set; }
}

/// <summary>歌手、专辑及“查看全部”页面的一次性重数据快照。</summary>
internal sealed class DetailPageSnapshotFile
{
    public int Version { get; set; } = 1;

    public List<CachedPageTrackFile> Tracks { get; set; } = new();

    public List<CachedAlbumCardFile> Albums { get; set; } = new();

    public List<CachedAlbumCardFile> Singles { get; set; } = new();

    public string Name { get; set; } = "";

    public string Subtitle { get; set; } = "";

    public string AvatarUrl { get; set; } = "";

    public string ArtistName { get; set; } = "";

    public string TrackCountText { get; set; } = "";

    public long PublishTimeMs { get; set; }

    public string Description { get; set; } = "";

    public string CoverUrl { get; set; } = "";

    public CachedArtistPageRefFile? PrimaryArtist { get; set; }

    public int Offset { get; set; }

    public int Total { get; set; } = -1;

    public bool HasMore { get; set; }
}

internal sealed class CachedAlbumCardFile
{
    public long Id { get; set; }

    public string Title { get; set; } = "";

    public string CoverUrl { get; set; } = "";

    public string Mid { get; set; } = "";
}

internal sealed class CachedArtistPageRefFile
{
    public int Source { get; set; }

    public long NetEaseId { get; set; }

    public string QqMid { get; set; } = "";

    public string Name { get; set; } = "";
}

internal sealed class CachedSongFile
{
    public long Id { get; set; }

    public int Source { get; set; }

    public string Mid { get; set; } = "";

    public string Name { get; set; } = "";

    public string Artist { get; set; } = "";

    public string Album { get; set; } = "";

    public string CoverUrl { get; set; } = "";

    public int DurationMs { get; set; }

    public int Fee { get; set; }

    public long[] ArtistIds { get; set; } = [];

    public string[] ArtistNames { get; set; } = [];

    public string[] ArtistMids { get; set; } = [];

    public long AlbumId { get; set; }

    public string AlbumMid { get; set; } = "";
}
