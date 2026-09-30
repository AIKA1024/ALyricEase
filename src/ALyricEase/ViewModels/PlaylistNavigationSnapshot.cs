using ALyricEase.Models;

namespace ALyricEase.ViewModels;

internal enum PlaylistPageKind
{
    NetEase,
    Qq,
    Aggregate,
    Cloud,
}

/// <summary>
/// 导航栈中的轻量歌单状态。曲目、队列与分页索引只存在内存快照中，历史项不保活页面重数据。
/// </summary>
internal sealed record PlaylistNavigationSnapshot(
    string CacheKey,
    PlaylistPageKind Kind,
    Playlist Header,
    AggregatePlaylist? Aggregate,
    string PlaylistTitle,
    string CreatorName,
    int SelectedSortIndex,
    string SearchText,
    bool IsFilterExpanded,
    double ScrollOffset,
    // 默认 0 兼容旧构造点(Headless 探针):0 = 创建者不可跳转,与生产语义一致
    long CreatorId = 0);
