namespace ALyricEase.Models;

/// <summary>歌单(列表项)。</summary>
public sealed class Playlist
{
    public long Id { get; init; }

    /// <summary>QQ 音乐资产目录 id(asset 写接口 AddSonglist/DelSonglist/EditPlaylist 的 dirId 用)。
    /// 取值以创建响应/用户歌单列表为准:新建歌单是小序号(首建常为 1),未必与 tid 一致;
    /// "我喜欢"固定为 201。网易云歌单恒为 0。</summary>
    public long DirId { get; init; }

    /// <summary>来源音源(侧边栏分组与打开路由用)。默认网易云。</summary>
    public Services.MusicSource Source { get; init; } = Services.MusicSource.NetEase;

    /// <summary>QQ 官方榜单编号(热歌 26 / 新歌 27 / 飙升 62…),非榜单歌单为 0。
    /// 榜单不是歌单:曲目要走 musicToplist.ToplistInfoServer/GetDetail,不能拿 Id 当 disstid 用。
    /// 这类"临时歌单"只用于把榜单接进共享的歌单页(Id 填同一编号当缓存键)。</summary>
    public int TopId { get; init; }

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public int TrackCount { get; init; }

    public string CoverUrl { get; init; } = "";

    /// <summary>当前账号是否拥有该歌单的写权限。用户收藏的他人歌单为 false；
    /// 播放条“添加到歌单”只展示可写歌单，避免点选后才由服务端拒绝。</summary>
    public bool CanAddTracks { get; init; }

    /// <summary>创建者昵称(搜索结果等他人歌单场景;空 = 当前登录账号自己的歌单)。</summary>
    public string CreatorName { get; init; } = "";

    /// <summary>累计播放次数(用户页卡片播放量角标用;仅部分接口返回,默认 0)。</summary>
    public double PlayCount { get; init; }
}
