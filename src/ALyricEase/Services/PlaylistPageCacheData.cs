using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>页面重数据的短生命周期传输形态；写盘完成后即可被 GC 回收。</summary>
internal sealed record PlaylistPageCacheData(
    IReadOnlyList<NavigationPageCacheTrack> Tracks,
    IReadOnlyList<long> TrackIds,
    int Materialized,
    AggregatePageCacheState? AggregateLoad,
    QqPlaylistPageCacheState? QqPaging = null);

/// <summary>QQ 歌单页的分页游标(QQ 没有全量 trackId,续页只能靠 song_begin)。
/// 未拉完就离页时把它一并存进快照,返回时滚动到底才能接着续。</summary>
internal sealed record QqPlaylistPageCacheState(
    long PlaylistId,
    int NextBegin,
    int TotalCount,
    bool HasMore);

internal sealed record AggregatePageCacheState(
    int MemberIndex,
    IReadOnlyList<long>? NetEaseTrackIds,
    IReadOnlyList<Song> NetEaseKnown,
    int NetEaseCursor,
    int QqBegin,
    int FailedCount,
    bool CoverSet);
