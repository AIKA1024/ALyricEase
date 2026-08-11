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

    /// <summary>歌单内全量曲目 id(v6 接口 trackIds,权威顺序)。</summary>
    public List<TrackIdItem>? TrackIds { get; init; }

    /// <summary>接口顺带返回的前段完整曲目(登录态约 150 首,匿名约 10 首)。</summary>
    public List<SearchSong>? Tracks { get; init; }
}

public sealed record TrackIdItem
{
    public long Id { get; init; }
}
