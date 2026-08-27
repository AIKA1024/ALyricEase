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
    /// <summary>0 = 免费可播,其余 VIP/付费。track_info 形态为 payplay。</summary>
    [JsonPropertyName("payplay")] public int PayPlay { get; init; }

    /// <summary>雷达 Track 形态为 snake_case(pay_play)。</summary>
    [JsonPropertyName("pay_play")] public int PayPlaySnake { get; init; }

    /// <summary>两命名形态合并取值(0=免费)。</summary>
    public int EffectivePayPlay => PayPlay != 0 ? PayPlay : PayPlaySnake;
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

// ---------- 账号能力(用户主页 / 歌单曲目)----------

public sealed record QQHomepageResponse
{
    /// <summary>0 成功;1000 = 登录失效/凭据不完整。</summary>
    public int Code { get; init; }

    public QQHomepageData? Data { get; init; }
}

public sealed record QQHomepageData
{
    public QQCreatorDto? Creator { get; init; }

    public QQMyDiss? Mydiss { get; init; }
}

/// <summary>主页创建者资料。字段随端版本漂移,nick/avatar 多候选兜底。</summary>
public sealed record QQCreatorDto
{
    public string? Nick { get; init; }

    public string? Nickname { get; init; }

    public long Uin { get; init; }

    public string? Avatar { get; init; }

    [JsonPropertyName("faceurl")] public string? FaceUrl { get; init; }

    [JsonPropertyName("headpic")] public string? Headpic { get; init; }
}

public sealed record QQMyDiss
{
    public List<QQDissItem>? List { get; init; }
}

/// <summary>歌单项(老主页通道回落用)。id 兼容 dissid/disstid,名兼容 dissname/dirname,封面兼容 picurl/logo。</summary>
public sealed record QQDissItem
{
    [JsonPropertyName("dissid")] public long DissId { get; init; }

    [JsonPropertyName("disstid")] public long DissTid { get; init; }

    [JsonPropertyName("dissname")] public string? DissName { get; init; }

    [JsonPropertyName("dirname")] public string? Dirname { get; init; }

    [JsonPropertyName("picurl")] public string? Picurl { get; init; }

    public string? Logo { get; init; }

    [JsonPropertyName("song_count")] public int SongCount { get; init; }

    [JsonPropertyName("songcount")] public int SongcountAlt { get; init; }

    public string? Intro { get; init; }
}

// ---------- 官方"每日30首"入口(music.recommend.RecommendFeed/get_recommend_feed)----------

public sealed record QQRecommendFeedResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQRecommendFeedReq? Req0 { get; init; }
}

public sealed record QQRecommendFeedReq
{
    public int Code { get; init; }

    public QQRecommendFeedData? Data { get; init; }
}

public sealed record QQRecommendFeedData
{
    /// <summary>首页货架;shelf[0].niche[0].v_card 为"每日30首/新歌推荐"等入口卡片。</summary>
    [JsonPropertyName("v_shelf")] public List<QQFeedShelfDto>? VShelf { get; init; }
}

public sealed record QQFeedShelfDto
{
    [JsonPropertyName("v_niche")] public List<QQFeedNicheDto>? VNiche { get; init; }
}

public sealed record QQFeedNicheDto
{
    [JsonPropertyName("v_card")] public List<QQFeedCardDto>? VCard { get; init; }
}

public sealed record QQFeedCardDto
{
    /// <summary>入口名("每日30首"等);部分卡片无标题。</summary>
    public string? Title { get; init; }

    /// <summary>卡片目标 id("每日30首" → 当日动态 disstid,每日轮换)。</summary>
    public long Id { get; init; }
}

// ---------- 雷达每日推荐(music.recommend.TrackRelationServer/GetRadarSong;官方路径回落)----------

public sealed record QQRadarResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQRadarReq? Req0 { get; init; }
}

public sealed record QQRadarReq
{
    public int Code { get; init; }

    public QQRadarData? Data { get; init; }
}

public sealed record QQRadarData
{
    /// <summary>推荐曲目数字 id(现网行为:ReqType=2 每页稳定回 5 个去重 id;旧 vec_songs 轨道流已停止下发,
    /// 字段不再建模)。</summary>
    [JsonPropertyName("RecommendSongIds")] public List<long>? RecommendSongIds { get; init; }

    /// <summary>还有下一页(单页仅约 5 首,翻页拼满)。</summary>
    [JsonPropertyName("hasMore")] public bool HasMore { get; init; }
}

// ---------- 歌手/专辑(mid 维度,QQ 导航页)----------

public sealed record QQSongEntriesResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQSongEntriesReq? Req0 { get; init; }
}

public sealed record QQSongEntriesReq
{
    public int Code { get; init; }

    public QQSongEntriesData? Data { get; init; }
}

public sealed record QQSongEntriesData
{
    /// <summary>歌手歌曲/专辑曲目共用外壳。</summary>
    [JsonPropertyName("songList")] public List<QQSongEntryDto>? SongList { get; init; }

    public int TotalNum { get; init; }
}

public sealed record QQSongEntryDto
{
    /// <summary>包裹层,内为标准曲目结构(track_info 同构)。</summary>
    [JsonPropertyName("songInfo")] public QQTrackDto? SongInfo { get; init; }
}

public sealed record QQAlbumListResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQAlbumListReq? Req0 { get; init; }
}

public sealed record QQAlbumListReq
{
    public int Code { get; init; }

    public QQAlbumListData? Data { get; init; }
}

public sealed record QQAlbumListData
{
    [JsonPropertyName("albumList")] public List<QQAlbumItemDto>? AlbumList { get; init; }
}

public sealed record QQAlbumItemDto
{
    [JsonPropertyName("albumID")] public long AlbumId { get; init; }

    [JsonPropertyName("albumMid")] public string? AlbumMid { get; init; }

    [JsonPropertyName("albumName")] public string? AlbumName { get; init; }

    [JsonPropertyName("publishDate")] public string? PublishDate { get; init; }

    [JsonPropertyName("totalNum")] public int TotalNum { get; init; }

    [JsonPropertyName("singerName")] public string? SingerName { get; init; }

    [JsonPropertyName("albumType")] public string? AlbumType { get; init; }
}

public sealed record QQAlbumDetailResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQAlbumDetailReq? Req0 { get; init; }
}

public sealed record QQAlbumDetailReq
{
    public int Code { get; init; }

    public QQAlbumDetailData? Data { get; init; }
}

public sealed record QQAlbumDetailData
{
    [JsonPropertyName("basicInfo")] public QQAlbumBasicInfoDto? BasicInfo { get; init; }
}

public sealed record QQAlbumBasicInfoDto
{
    [JsonPropertyName("albumName")] public string? AlbumName { get; init; }

    [JsonPropertyName("publishDate")] public string? PublishDate { get; init; }

    [JsonPropertyName("desc")] public string? Desc { get; init; }

    [JsonPropertyName("language")] public string? Language { get; init; }
}

/// <summary>专辑基础信息(客户端映射后的领域形态,非上游响应)。</summary>
public sealed record QQAlbumInfo(string Name, string PublishDate, string Description);

// ---------- 歌单详情(music.srfDissInfo.aiDissInfo/uniform_get_Dissinfo;与网页端同源,
// 旧 DissInfo/CgiGetDiss 缺 userinfo/tag/orderlist 时现网只回 code=0 但 songlist 为空)----------

public sealed record QQUniformDissResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQUniformDissReq? Req0 { get; init; }
}

public sealed record QQUniformDissReq
{
    public int Code { get; init; }

    public QQUniformDissData? Data { get; init; }
}

public sealed record QQUniformDissData
{
    /// <summary>内部业务码:-100006 歌单不存在/不可见;通常与 songlist 空伴随出现。</summary>
    public int Code { get; init; }

    /// <summary>曲目为标准 track_info 同构,直接复用 QQTrackDto。</summary>
    [JsonPropertyName("songlist")] public List<QQTrackDto>? Songlist { get; init; }

    /// <summary>歌单总曲数(分页终止条件;个别形态漂移时为 0)。</summary>
    [JsonPropertyName("total_song_num")] public long TotalSongNum { get; init; }

    /// <summary>歌单元信息(title/picurl/host_nick),备用。</summary>
    [JsonPropertyName("dirinfo")] public QQDirInfoDto? DirInfo { get; init; }
}

public sealed record QQDirInfoDto
{
    [JsonPropertyName("title")] public string? Title { get; init; }

    [JsonPropertyName("picurl")] public string? PicUrl { get; init; }

    [JsonPropertyName("host_nick")] public string? HostNick { get; init; }
}

// ---------- 用户歌单(music.musicasset.PlaylistBaseRead/GetPlaylistByUin 自建 +
// music.musicasset.PlaylistFavRead/GetPlaylistFavInfo 收藏;老 homepage mydiss 不再稳定下发歌单名)----------

public sealed record QQCreatedPlaylistsResponse
{
    public int Code { get; init; }

    public QQCreatedPlaylistsData? Data { get; init; }
}

public sealed record QQCreatedPlaylistsData
{
    [JsonPropertyName("v_playlist")] public List<QQCreatedPlaylistDto>? VPlaylist { get; init; }
}

public sealed record QQCreatedPlaylistDto
{
    /// <summary>歌单数字 id(即 disstid)。</summary>
    public long Tid { get; init; }

    /// <summary>资产目录 id(资产写接口 AddSonglist 的 dirId 用;普通歌单与该 tid 一致)。</summary>
    [JsonPropertyName("dirId")] public long DirId { get; init; }

    [JsonPropertyName("dirName")] public string? DirName { get; init; }

    [JsonPropertyName("picUrl")] public string? PicUrl { get; init; }

    [JsonPropertyName("songNum")] public int SongNum { get; init; }

    public string? Desc { get; init; }
}

public sealed record QQFavPlaylistsResponse
{
    public int Code { get; init; }

    public QQFavPlaylistsData? Data { get; init; }
}

public sealed record QQFavPlaylistsData
{
    [JsonPropertyName("v_list")] public List<QQFavPlaylistDto>? VList { get; init; }
}

public sealed record QQFavPlaylistDto
{
    public long Tid { get; init; }

    public string? Name { get; init; }

    public string? Logo { get; init; }

    [JsonPropertyName("songnum")] public int SongNum { get; init; }

    public string? Desc { get; init; }
}

// ---------- 用户基础资料(userInfo.BaseUserInfoServer/get_user_baseinfo_v2;按 uin 键回 map)----------

public sealed record QQUserInfoResponse
{
    public int Code { get; init; }

    [JsonPropertyName("req_0")] public QQUserInfoReq? Req0 { get; init; }
}

public sealed record QQUserInfoReq
{
    public int Code { get; init; }

    public QQUserInfoData? Data { get; init; }
}

public sealed record QQUserInfoData
{
    /// <summary>map_userinfo 以 uin 字符串为键取唯一条目;未登录/凭据缺失时为 null。</summary>
    [JsonPropertyName("map_userinfo")] public Dictionary<string, QQUserBaseInfoDto>? MapUserinfo { get; init; }
}

public sealed record QQUserBaseInfoDto
{
    public string? Nick { get; init; }

    [JsonPropertyName("headurl")] public string? HeadUrl { get; init; }

    [JsonPropertyName("ifpicurl")] public string? IfPicUrl { get; init; }
}
