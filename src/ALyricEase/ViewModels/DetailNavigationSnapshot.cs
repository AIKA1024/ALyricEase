using ALyricEase.Services;

namespace ALyricEase.ViewModels;

internal enum DetailPageKind
{
    Artist,
    Album,
    ArtistSongs,
    ArtistAlbums,
    User,
    /// <summary>用户页"关注"点进去的名单(与下面那个共用同一个页面 VM)。</summary>
    UserFollowing,
    /// <summary>用户页"粉丝"点进去的名单。</summary>
    UserFollowers,
}

/// <summary>详情页导航历史中的轻量状态；列表、队列与卡片只存在一次性内存快照中。</summary>
internal sealed record DetailNavigationSnapshot(
    string CacheKey,
    DetailPageKind Kind,
    MusicSource Source,
    long Id,
    string Mid,
    string Name,
    int SelectedSortIndex,
    string SearchText,
    bool IsFilterExpanded,
    double ScrollOffset);
