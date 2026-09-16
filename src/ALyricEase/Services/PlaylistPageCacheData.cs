using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>页面重数据的短生命周期传输形态；写盘完成后即可被 GC 回收。</summary>
internal sealed record PlaylistPageCacheData(
    IReadOnlyList<NavigationPageCacheTrack> Tracks,
    IReadOnlyList<long> TrackIds,
    int Materialized,
    AggregatePageCacheState? AggregateLoad);

internal sealed record AggregatePageCacheState(
    int MemberIndex,
    IReadOnlyList<long>? NetEaseTrackIds,
    IReadOnlyList<Song> NetEaseKnown,
    int NetEaseCursor,
    int QqBegin,
    int FailedCount,
    bool CoverSet);
