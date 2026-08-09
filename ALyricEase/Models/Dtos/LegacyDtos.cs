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
