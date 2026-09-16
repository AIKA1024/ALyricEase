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
    double ScrollOffset);
