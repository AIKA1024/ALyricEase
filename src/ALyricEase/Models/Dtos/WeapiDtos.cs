using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>网易云 weapi 接口响应 DTO。字段名与接口返回的 snake_case 对应。</summary>

public sealed record SearchResponse
{
    public int Code { get; init; }

    public SearchResult? Result { get; init; }
}

public sealed record SearchResult
{
    [JsonPropertyName("songs")] public List<SearchSong>? Songs { get; init; }

    [JsonPropertyName("songCount")] public int SongCount { get; init; }
}

/// <summary>搜索接口返回的单曲(ar/al 为搜索接口字段名;v3/song/detail 是 artists/album)。</summary>
public sealed record SearchSong
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("ar")] public List<SearchArtist>? Artists { get; init; }

    [JsonPropertyName("al")] public SearchAlbum? Album { get; init; }

    /// <summary>时长,毫秒。</summary>
    [JsonPropertyName("dt")] public int DurationMs { get; init; }

    public int Fee { get; init; }
}

public sealed record SearchArtist
{
    public long Id { get; init; }

    public string Name { get; init; } = "";
}

public sealed record SearchAlbum
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("picUrl")] public string PicUrl { get; init; } = "";
}

// ---------- 云盘(weapi /api/v1/cloud/get,需登录) ----------

/// <summary>云盘歌曲项:simpleSong 为 weapi 风格单曲(ar/al/dt),复用 SearchSong 映射。
/// simpleSong 可为 null(异常条目),由调用方跳过。</summary>
public sealed record CloudSongItemDto
{
    [JsonPropertyName("songId")] public long SongId { get; init; }

    [JsonPropertyName("simpleSong")] public SearchSong? SimpleSong { get; init; }

    [JsonPropertyName("fileName")] public string? FileName { get; init; }
}

public sealed record CloudListResponse
{
    public int Code { get; init; }

    /// <summary>云盘曲目总数(头部"N 首"显示用)。</summary>
    [JsonPropertyName("count")] public int TotalCount { get; init; }

    public List<CloudSongItemDto>? Data { get; init; }

    [JsonPropertyName("hasMore")] public bool HasMore { get; init; }
}

// ---------- 歌手 / 专辑详情(明文 GET) ----------

public sealed record ArtistDetailResponse
{
    public int Code { get; init; }

    public ArtistDetailData? Data { get; init; }
}

public sealed record ArtistDetailData
{
    public ArtistDetailInfo? Artist { get; init; }
}

public sealed record ArtistDetailInfo
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>头像(/api/artist/head/info/get 返回 avatar 字段)。</summary>
    [JsonPropertyName("avatar")] public string Avatar { get; init; } = "";
}

/// <summary>/api/artist/top/song 热门歌曲,songs 与搜索单曲同构(ar/al/dt)。</summary>
public sealed record ArtistTopSongsResponse
{
    public int Code { get; init; }

    public List<SearchSong>? Songs { get; init; }
}

/// <summary>/api/artist/albums/{id} 歌手专辑列表。</summary>
public sealed record ArtistAlbumsResponse
{
    public int Code { get; init; }

    [JsonPropertyName("hotAlbums")] public List<ArtistAlbumItem>? HotAlbums { get; init; }

    /// <summary>是否还有更多(需 offset 翻页)。</summary>
    public bool More { get; init; }
}

public sealed record ArtistAlbumItem
{
    public long Id { get; init; }

    /// <summary>专辑 mid(QQ 音乐填,专辑页按 mid 取数据;网易云留空)。</summary>
    public string Mid { get; init; } = "";

    public string Name { get; init; } = "";

    [JsonPropertyName("picUrl")] public string PicUrl { get; init; } = "";

    public int Size { get; init; }

    /// <summary>专辑类型:字符串"专辑" / "Single" / "EP"(实测路径接口返回的是中文+英文,非数字)。</summary>
    public string Type { get; init; } = "";
}

public sealed record AlbumDetailResponse
{
    public int Code { get; init; }

    public AlbumDetailInfo? Album { get; init; }

    public List<SearchSong>? Songs { get; init; }
}

public sealed record AlbumDetailInfo
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("picUrl")] public string PicUrl { get; init; } = "";

    public List<ArtistDetailInfo>? Artists { get; init; }

    public int Size { get; init; }

    /// <summary>发行时间,毫秒时间戳。</summary>
    [JsonPropertyName("publishTime")] public long PublishTime { get; init; }

    public string Description { get; init; } = "";
}

/// <summary>每日歌曲推荐(/api/v3/discovery/recommend/songs,需登录)响应。
/// dailySongs 数组元素与搜索单曲同构(ar/al/dt),可直接复用 SearchSong 映射。</summary>
public sealed record DailySongsResponse
{
    public DailySongsData? Data { get; init; }
}

public sealed record DailySongsData
{
    [JsonPropertyName("dailySongs")] public List<SearchSong>? DailySongs { get; init; }
}

public sealed record LyricResponse
{
    public int Code { get; init; }

    public LyricBody? Lrc { get; init; }

    [JsonPropertyName("tlyric")] public LyricBody? TLyric { get; init; }

    [JsonPropertyName("yrc")] public LyricBody? Yrc { get; init; }
}

public sealed record LyricBody
{
    public string Lyric { get; init; } = "";
}

public sealed record SongDetailResponse
{
    public int Code { get; init; }

    public List<SongDetailItem>? Songs { get; init; }
}

/// <summary>v3/song/detail 的曲目(字段是 artists/album)。</summary>
public sealed record SongDetailItem
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    public List<SearchArtist>? Artists { get; init; }

    public SearchAlbum? Album { get; init; }

    [JsonPropertyName("dt")] public int DurationMs { get; init; }

    public int Fee { get; init; }
}

public sealed record UserAccountResponse
{
    public UserProfile? Profile { get; init; }
}

public sealed record UserProfile
{
    [JsonPropertyName("userId")] public long UserId { get; init; }

    public string Nickname { get; init; } = "";

    [JsonPropertyName("avatarUrl")] public string AvatarUrl { get; init; } = "";
}

public sealed record PlaylistListResponse
{
    public List<PlaylistDto>? Playlist { get; init; }
}

public sealed record PlaylistDto
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("trackCount")] public int TrackCount { get; init; }

    [JsonPropertyName("coverImgUrl")] public string CoverImgUrl { get; init; } = "";
}

public sealed record PlaylistDetailResponse
{
    public int Code { get; init; }

    public PlaylistDetail? Playlist { get; init; }
}

public sealed record PlaylistDetail
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("trackCount")] public int TrackCount { get; init; }

    /// <summary>歌单封面(会随曲目变化:如"我喜欢的音乐"自动生成的封面,加歌后 coverImgUrl 会变)。</summary>
    [JsonPropertyName("coverImgUrl")] public string CoverImgUrl { get; init; } = "";

    /// <summary>歌单内全量曲目 id(v6 接口 trackIds,权威顺序)。</summary>
    public List<TrackIdItem>? TrackIds { get; init; }

    /// <summary>接口顺带返回的前段完整曲目(登录态约 150 首,匿名约 10 首)。</summary>
    public List<SearchSong>? Tracks { get; init; }
}

public sealed record TrackIdItem
{
    public long Id { get; init; }
}
