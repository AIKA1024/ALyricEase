using ALyricEase.Services;

namespace ALyricEase.ViewModels;

internal enum DetailPageKind
{
    Artist,
    Album,
    ArtistSongs,
    ArtistAlbums,
}

/// <summary>详情页导航历史中的轻量状态；列表、队列与卡片只存在一次性磁盘快照中。</summary>
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
