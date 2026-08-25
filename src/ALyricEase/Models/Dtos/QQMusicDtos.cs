using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>QQ 音乐接口响应 DTO。搜索/歌词走 c.y.qq.com fcg 明文端点,
/// vkey/详情走 u.y.qq.com musicu.fcg(参考开源 qq-music-api 的上游协议)。
/// 字符串一律可空(上游会显式回 null,反序列化会覆盖默认值),由映射层归一。
/// 同一曲目 DTO 兼容两种命名:搜索(songname/songmid + 平铺 albumid…)与 track_info(name/mid + 嵌套 album)。</summary>

public sealed record QQSearchResponse
{
    public int Code { get; init; }

    public QQSearchData? Data { get; init; }
}

public sealed record QQSearchData
{
    public QQSearchSongContainer? Song { get; init; }
}

public sealed record QQSearchSongContainer
{
    public List<QQTrackDto>? List { get; init; }

    public long Total { get; init; }
}

public sealed record QQSingerDto
{
    public long Id { get; init; }

    public string? Mid { get; init; }

    public string? Name { get; init; }
}

/// <summary>专辑引用。client_search_cp 为曲目上的平铺字段(albumid/albummid/albumname),
/// pf_song_detail track_info 为嵌套对象(album:{id,mid,name}),两形态都收。</summary>
public sealed record QQAlbumRefDto
{
    [JsonPropertyName("albumid")] public long AlbumId { get; init; }

    [JsonPropertyName("albummid")] public string? AlbumMid { get; init; }

    [JsonPropertyName("albumname")] public string? AlbumName { get; init; }

    public long Id { get; init; }

    public string? Mid { get; init; }

    public string? Name { get; init; }
}

public sealed record QQPayDto
{
    /// <summary>0 = 免费可播,其余 VIP/付费。</summary>
    [JsonPropertyName("payplay")] public int PayPlay { get; init; }
}

public sealed record QQTrackDto
{
    // client_search_cp 形态
    [JsonPropertyName("songid")] public long SongId { get; init; }

    [JsonPropertyName("songmid")] public string? SongMid { get; init; }

    [JsonPropertyName("songname")] public string? SongName { get; init; }

    /// <summary>平铺专辑字段(仅搜索响应有)。</summary>
    [JsonPropertyName("albumid")] public long AlbumIdFlat { get; init; }

    [JsonPropertyName("albummid")] public string? AlbumMidFlat { get; init; }

    [JsonPropertyName("albumname")] public string? AlbumNameFlat { get; init; }

    // track_info 形态
    public long Id { get; init; }

    public string? Mid { get; init; }

    public string? Name { get; init; }

    /// <summary>时长,秒(两形态同名)。</summary>
    public int Interval { get; init; }

    public List<QQSingerDto>? Singer { get; init; }

    /// <summary>嵌套专辑(仅详情 track_info 有)。</summary>
    public QQAlbumRefDto? Album { get; init; }

    public QQPayDto? Pay { get; init; }
}

public sealed record QQVkeyResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQVkeyReq? Req0 { get; init; }
}

public sealed record QQVkeyReq
{
    public int Code { get; init; }

    public QQVkeyData? Data { get; init; }
}

public sealed record QQVkeyData
{
    /// <summary>候选下载域名(优先 https / 非 ws 前缀)。</summary>
    public List<string>? Sip { get; init; }

    [JsonPropertyName("midurlinfo")] public List<QQMidUrlInfo>? MidUrlInfo { get; init; }
}

public sealed record QQMidUrlInfo
{
    [JsonPropertyName("songmid")] public string? SongMid { get; init; }

    /// <summary>相对播放路径;空表示不可播(VIP 需登录,匿名 result=104003)。</summary>
    public string? Purl { get; init; }

    public int Result { get; init; }
}

public sealed record QQLyricFcgResponse
{
    public int Retcode { get; init; }

    public int Code { get; init; }

    public int Subcode { get; init; }

    /// <summary>LRC 原文(base64)。</summary>
    public string? Lyric { get; init; }

    /// <summary>翻译歌词(base64,可能为空)。</summary>
    public string? Trans { get; init; }
}

public sealed record QQMusicuLyricResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQMusicuLyricReq? Req0 { get; init; }
}

public sealed record QQMusicuLyricReq
{
    public int Code { get; init; }

    public QQLyricData? Data { get; init; }
}

public sealed record QQLyricData
{
    public string? Lyric { get; init; }

    public string? Trans { get; init; }
}

public sealed record QQDetailResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQDetailReq? Req0 { get; init; }
}

public sealed record QQDetailReq
{
    public int Code { get; init; }

    public QQDetailData? Data { get; init; }
}

public sealed record QQDetailData
{
    [JsonPropertyName("track_info")] public QQTrackDto? TrackInfo { get; init; }
}
