using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>网易云明文 GET 接口(/api/...)响应 DTO。字段命名与 weapi cloudsearch 不同:
/// 明文搜索用 artists/album/duration,weapi 用 ar/al/dt。仅在 weapi/eapi 被风控拦截时回落使用。</summary>

public sealed record LegacySearchResponse
{
    public int Code { get; init; }

    public LegacySearchResult? Result { get; init; }
}

public sealed record LegacySearchResult
{
    public List<LegacySearchSong>? Songs { get; init; }
}

public sealed record LegacySearchSong
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>明文搜索返回 artists(weapi 是 ar)。</summary>
    public List<SearchArtist>? Artists { get; init; }

    /// <summary>明文搜索返回 album(weapi 是 al)。</summary>
    public SearchAlbum? Album { get; init; }

    /// <summary>时长,毫秒(weapi 是 dt)。</summary>
    public int Duration { get; init; }

    public int Fee { get; init; }
}

// ---------- 登录 / 歌单(明文 GET) ----------

public sealed record LegacyAccountResponse
{
    public int Code { get; init; }

    public LegacyProfile? Profile { get; init; }
}

public sealed record LegacyProfile
{
    [JsonPropertyName("userId")] public long UserId { get; init; }

    public string Nickname { get; init; } = "";

    [JsonPropertyName("avatarUrl")] public string AvatarUrl { get; init; } = "";
}

public sealed record LegacyUserPlaylistResponse
{
    public int Code { get; init; }

    public List<LegacyPlaylistItem>? Playlist { get; init; }
}

public sealed record LegacyPlaylistItem
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    [JsonPropertyName("trackCount")] public int TrackCount { get; init; }

    [JsonPropertyName("coverImgUrl")] public string CoverUrl { get; init; } = "";
}

public sealed record LegacyPlaylistDetailResponse
{
    public int Code { get; init; }

    public LegacyPlaylistResult? Result { get; init; }
}

public sealed record LegacyPlaylistResult
{
    /// <summary>歌单曲目:字段是 artists/album/duration(与明文搜索一致),复用 LegacySearchSong。</summary>
    public List<LegacySearchSong>? Tracks { get; init; }
}

// ---------- 首页推荐(明文 GET) ----------

/// <summary>personalized/playlist、personalized/newsong、discovery/recommend/resource 的通用封面项。
/// PlayCount 用 double:接口返回科学计数法(如 6.3607476E7),long 反序列化会抛异常。
/// 不要加 [JsonPropertyName("playCount")]:每日推荐返回小写 playcount,显式名字会盖掉大小写不敏感匹配。</summary>
public sealed record RecommendItemDto
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    public string Copywriter { get; init; } = "";

    public string PicUrl { get; init; } = "";

    public double PlayCount { get; init; }

    public int TrackCount { get; init; }

    /// <summary>newsong 的真实曲目嵌套在 song 下(artists 数组)。</summary>
    public RecommendSongDto? Song { get; init; }
}

public sealed record RecommendSongDto
{
    public string Name { get; init; } = "";

    public List<SearchArtist>? Artists { get; init; }
}

/// <summary>personalized/playlist、personalized/newsong 的响应(result 数组)。</summary>
public sealed record RecommendListResponse
{
    public int Code { get; init; }

    public List<RecommendItemDto>? Result { get; init; }
}

/// <summary>discovery/recommend/resource(每日推荐,登录后才有数据;匿名返回 code 301)。</summary>
public sealed record RecommendResourceResponse
{
    public int Code { get; init; }

    public List<RecommendItemDto>? Recommend { get; init; }
}
