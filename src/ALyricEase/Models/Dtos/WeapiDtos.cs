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

    // ---- 多类型搜索(cloudsearch type=10/100/1000/1002;单请求只有对应一个列表非空) ----

    [JsonPropertyName("albums")] public List<SearchAlbumItemDto>? Albums { get; init; }

    [JsonPropertyName("artists")] public List<SearchArtistItemDto>? Artists { get; init; }

    [JsonPropertyName("playlists")] public List<SearchPlaylistItemDto>? Playlists { get; init; }

    [JsonPropertyName("userprofiles")] public List<SearchUserItemDto>? Userprofiles { get; init; }
}

/// <summary>type=10 专辑条目(publishTime 为毫秒时间戳)。</summary>
public sealed record SearchAlbumItemDto
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("picUrl")] public string PicUrl { get; init; } = "";

    [JsonPropertyName("publishTime")] public long PublishTimeMs { get; init; }

    [JsonPropertyName("songCount")] public int SongCount { get; init; }

    [JsonPropertyName("artist")] public SearchArtist? Artist { get; init; }
}

/// <summary>type=100 歌手条目(img1v1Url 为方形头像;alias 为别名列表)。</summary>
public sealed record SearchArtistItemDto
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("img1v1Url")] public string Img1v1Url { get; init; } = "";

    [JsonPropertyName("picUrl")] public string PicUrl { get; init; } = "";

    [JsonPropertyName("alias")] public List<string>? Alias { get; init; }
}

/// <summary>type=1000 歌单条目(coverImgUrl 可能为 null,须回退 picUrl)。</summary>
public sealed record SearchPlaylistItemDto
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("coverImgUrl")] public string? CoverImgUrl { get; init; }

    [JsonPropertyName("picUrl")] public string? PicUrl { get; init; }

    [JsonPropertyName("trackCount")] public int TrackCount { get; init; }

    [JsonPropertyName("creator")] public SearchCreatorDto? Creator { get; init; }
}

public sealed record SearchCreatorDto
{
    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";

    /// <summary>创建者用户 id(用户页跳转用;个别响应缺失时为 0)。</summary>
    [JsonPropertyName("userId")] public long UserId { get; init; }
}

/// <summary>type=1002 用户条目。</summary>
public sealed record SearchUserItemDto
{
    [JsonPropertyName("userId")] public long UserId { get; init; }

    [JsonPropertyName("nickname")] public string Nickname { get; init; } = "";

    [JsonPropertyName("avatarUrl")] public string AvatarUrl { get; init; } = "";

    [JsonPropertyName("signature")] public string Signature { get; init; } = "";
}

/// <summary>歌曲权益(可播性判定;多接口内嵌 privilege,字段名固定)。</summary>
public sealed record SongPrivilegeDto
{
    public long Id { get; init; }

    public int Fee { get; init; }

    /// <summary>单曲/数字专辑购买状态；3 或 5 表示当前账号已购买。</summary>
    [JsonPropertyName("payed")] public int PurchaseStatus { get; init; }

    /// <summary>版权状态:-200 = 明确无版权；-1 等值可能因接口而异，不能直接禁用。</summary>
    public int St { get; init; }

    /// <summary>当前账号可播的最高码率(bps),0 = 不可播(账号相关)。</summary>
    public int Pl { get; init; }
}

/// <summary>搜索接口返回的单曲(ar/al 为搜索接口字段名)。</summary>
public sealed record SearchSong
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>歌曲别名/版本说明（常含影视用途、Live 等补充信息）。</summary>
    [JsonPropertyName("alia")] public List<string>? Aliases { get; init; }

    /// <summary>发行时间，毫秒时间戳；搜索响应可能缺省为 0。</summary>
    [JsonPropertyName("publishTime")] public long PublishTimeMs { get; init; }

    [JsonPropertyName("ar")] public List<SearchArtist>? Artists { get; init; }

    [JsonPropertyName("al")] public SearchAlbum? Album { get; init; }

    /// <summary>时长,毫秒。</summary>
    [JsonPropertyName("dt")] public int DurationMs { get; init; }

    public int Fee { get; init; }

    /// <summary>权益(专辑/歌单详情内嵌;部分接口不给则为 null,可播性回退到 fee 判据)。</summary>
    [JsonPropertyName("privilege")] public SongPrivilegeDto? Privilege { get; init; }
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

/// <summary>/api/v1/artist/songs 歌手全量歌曲(limit/offset 服务端分页,more 标记还有下一页)。
/// 注意:该接口回 legacy 曲目结构(artists/album/duration),不是搜索的 ar/al。</summary>
public sealed record ArtistSongsPageResponse
{
    public int Code { get; init; }

    public List<LegacySearchSong>? Songs { get; init; }

    /// <summary>是否还有下一页。</summary>
    public bool More { get; init; }

    public int Total { get; init; }
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

    /// <summary>发行时间,毫秒时间戳(0 = 未知)。QQ 侧由 publishDate 文本解析而来。</summary>
    [JsonPropertyName("publishTime")] public long PublishTime { get; init; }

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

    /// <summary>权益(只有 st=-200 可作为明确无版权；其他状态交播放地址接口确认)。</summary>
    [JsonPropertyName("privilege")] public SongPrivilegeDto? Privilege { get; init; }
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

    /// <summary>歌单创建者(v6 响应自带;推荐歌单详情页显示真实创建者用)。</summary>
    [JsonPropertyName("creator")] public SearchCreatorDto? Creator { get; init; }

    /// <summary>接口顺带返回的前段完整曲目(登录态约 150 首,匿名约 10 首)。</summary>
    public List<SearchSong>? Tracks { get; init; }
}

public sealed record TrackIdItem
{
    public long Id { get; init; }
}
