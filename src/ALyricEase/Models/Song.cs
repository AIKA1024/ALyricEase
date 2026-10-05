namespace ALyricEase.Models;

/// <summary>领域歌曲模型(搜索/歌单/详情统一映射到它)。</summary>
public sealed class Song
{
    public long Id { get; init; }

    /// <summary>来源音源(决定播放地址/歌词路由到哪个 API 客户端)。默认网易云。</summary>
    public Services.MusicSource Source { get; init; } = Services.MusicSource.NetEase;

    /// <summary>音源内字符串主键(QQ 音乐 songmid 等;网易云不用,留空)。</summary>
    public string Mid { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>展示用:多个艺术家以 / 拼接。</summary>
    public string Artist { get; init; } = "";

    public string Album { get; init; } = "";

    /// <summary>版本/用途副标题（如 Live、影视插曲）。搜索去重时用于避免合并不同录音版本。</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>发行日期文本（上游常见为 yyyy-MM-dd；空表示未知）。仅作跨平台匹配的弱证据。</summary>
    public string PublishDate { get; init; } = "";

    /// <summary>QQ 上游 ov 原版标识：1=原版，0=翻唱/Live，null=接口未提供。</summary>
    public int? OriginalVersion { get; init; }

    /// <summary>跨平台录音编码。当前两源搜索通常不返回，预留给未来可用的强匹配证据。</summary>
    public string Isrc { get; init; } = "";

    public string CoverUrl { get; init; } = "";

    /// <summary>时长,毫秒。</summary>
    public int DurationMs { get; init; }

    /// <summary>收费类型。网易云:0=免费,1=VIP,4=单独购买,8=普通音质免费；QQ 当前归一为 0/1。</summary>
    public int Fee { get; init; }

    /// <summary>当前账号是否已单独购买该曲/数字专辑。null 表示上游未返回购买权益。</summary>
    public bool? IsPurchased { get; init; }

    /// <summary>歌手 id 列表(点击歌手跳歌手页;显示名用 Artist。网易云填,QQ 留空)。</summary>
    public IReadOnlyList<long> ArtistIds { get; init; } = Array.Empty<long>();

    /// <summary>歌手名列表(多歌手时子菜单逐项用,与 ArtistIds/ArtistMids 一一对应)。</summary>
    public IReadOnlyList<string> ArtistNames { get; init; } = Array.Empty<string>();

    /// <summary>歌手 mid 列表(QQ 音乐填,与 ArtistNames 一一对应;网易云留空)。</summary>
    public IReadOnlyList<string> ArtistMids { get; init; } = Array.Empty<string>();

    /// <summary>专辑 id(点击专辑跳专辑页;显示名用 Album。网易云填)。</summary>
    public long AlbumId { get; init; }

    /// <summary>专辑 mid(QQ 音乐填;网易云留空)。</summary>
    public string AlbumMid { get; init; } = "";

    /// <summary>
    /// 本次运行中已经通过播放地址接口确认不可播放。列表行与播放队列共享此状态，
    /// 避免失败歌曲仍被上一曲/下一曲再次选中。
    /// </summary>
    public bool IsPlaybackUnavailable { get; internal set; }

    /// <summary>服务端明确判定无版权(网易云 privilege.st == -200)。
    /// 元数据阶段即可确定,与账号/VIP 无关,列表行可直接灰禁；-1 等状态仍允许播放接口实测。
    /// 由网易云映射器写入;QQ 歌恒 false。</summary>
    public bool IsNoCopyright { get; internal set; }

    /// <summary>
    /// 综合搜索中被折叠到当前展示项下的其他平台录音。播放仍使用当前实例；保留候选以便后续
    /// 增加手动切源或自动回退时无需重新匹配。普通列表为空。
    /// </summary>
    public IReadOnlyList<Song> AlternateRecordings { get; internal set; } = Array.Empty<Song>();

    /// <summary>该实例来自离线歌单快照且已确认有本地音频；播放时跳过在线音质升级请求。</summary>
    internal bool PreferCachedPlayback { get; set; }
}
