using ALyricEase.Models;
using ALyricEase.Models.Dtos;

namespace ALyricEase.Services;

/// <summary>音乐源抽象:搜索 / 播放地址 / 歌词 / 歌曲详情。
/// 各曲库客户端(网易云 NetEaseApiClient、QQ 音乐 QQMusicApiClient)实现此接口,
/// ViewModel 只依赖接口与 MusicApiProvider 路由,不感知具体音源协议。</summary>
public interface IMusicApi
{
    /// <summary>实现对应的音源(MusicApiProvider 据此注册/路由)。</summary>
    MusicSource Source { get; }

    /// <summary>UI 显示名(如"网易云"/"QQ音乐")。</summary>
    string DisplayName { get; }

    /// <summary>是否已登录(影响可播音质与账号能力)。</summary>
    bool IsLoggedIn { get; }

    /// <summary>当前登录用户是否为该音源会员(VIP 歌曲播放能力;未登录/未加载为 false)。
    /// 网易云 vipType≠0;QQ 音乐绿钻(identity.vip/huge_vip)。</summary>
    bool IsVip { get; }

    /// <summary>会员状态是否已确认(区别于 IsVip 的默认 false:未加载时不应据此判"非会员")。
    /// 未登录时亦为 true(无需确认)。</summary>
    bool IsVipLoaded { get; }

    /// <summary>确保会员状态已加载(幂等,单飞)。网易云随资料接口即时返回;QQ 首次会发一次
    /// vip_login_base 请求。失败按非会员处理不抛(播放失败消息用)。</summary>
    Task EnsureVipStatusAsync(CancellationToken ct = default);

    /// <summary>关键词搜索歌曲,映射为统一 Song 模型(Source/Mid 由实现填充)。</summary>
    Task<List<Song>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default);

    /// <summary>多类型搜索(结果页分区/类型 Tab)。kind=All 拉全部支持的分区,单类型只填对应列表;
    /// limit 为该类型条数。不支持多类型/该类型的音源返回 null(结果页退化为仅歌曲)。</summary>
    Task<SearchAllResult?> SearchAllAsync(string keyword, SearchKind kind, int limit, CancellationToken ct = default)
        => Task.FromResult<SearchAllResult?>(null);

    /// <summary>获取播放地址;VIP/版权受限返回 null 或 Url 为空。
    /// 固定目标档位不可用时由实现自动降级；auto 从歌曲元数据选最高档并只请求一次。失败抛 ApiException。</summary>
    Task<PlayUrlItem?> GetPlayUrlAsync(Song song, string level = "higher", CancellationToken ct = default);

    /// <summary>获取歌词(未解析的 LRC 原文 + 可选翻译)。失败抛 ApiException。</summary>
    Task<LyricResult?> GetLyricAsync(Song song, CancellationToken ct = default);

    /// <summary>按数字 id 取歌曲详情(用于只有 id 的入口补全元数据/mid)。</summary>
    Task<Song?> GetSongDetailAsync(long id, CancellationToken ct = default);
}
