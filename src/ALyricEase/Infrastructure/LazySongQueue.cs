using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 播放器使用的逻辑全量队列。界面只需物化可见批次；播放器按逻辑下标取歌时才解析详情。
/// </summary>
internal interface ILazySongQueue
{
    ValueTask<int> GetCountAsync(CancellationToken ct = default);

    ValueTask<Song?> GetSongAsync(int index, CancellationToken ct = default);

    /// <summary>复用界面已经取到的 Song，避免播放时重复请求。</summary>
    void Remember(int index, Song song);
}

/// <summary>已知全量网易云 trackId 的按需歌曲队列。</summary>
internal sealed class IndexedSongQueue : ILazySongQueue
{
    private readonly IReadOnlyList<long> _trackIds;
    private readonly Func<IReadOnlyList<long>, CancellationToken, Task<List<Song>>> _resolver;
    private readonly BoundedSongCache _cache = new(384);

    public IndexedSongQueue(
        IReadOnlyList<long> trackIds,
        IEnumerable<Song> knownSongs,
        Func<IReadOnlyList<long>, CancellationToken, Task<List<Song>>> resolver)
    {
        _trackIds = trackIds;
        _resolver = resolver;
        var byId = knownSongs.Where(song => song.Id != 0).GroupBy(song => song.Id)
            .ToDictionary(group => group.Key, group => group.First());
        for (var index = 0; index < trackIds.Count; index++)
            if (byId.TryGetValue(trackIds[index], out var song))
                _cache.Set(index, song);
    }

    public ValueTask<int> GetCountAsync(CancellationToken ct = default)
        => ValueTask.FromResult(_trackIds.Count);

    public async ValueTask<Song?> GetSongAsync(int index, CancellationToken ct = default)
    {
        if ((uint)index >= (uint)_trackIds.Count) return null;
        if (_cache.TryGet(index, out var cached)) return cached;

        var id = _trackIds[index];
        var songs = await _resolver(new[] { id }, ct).ConfigureAwait(false);
        var song = songs.FirstOrDefault(candidate => candidate.Id == id);
        if (song is not null) _cache.Set(index, song);
        return song;
    }

    public void Remember(int index, Song song)
    {
        if ((uint)index < (uint)_trackIds.Count)
            _cache.Set(index, song);
    }
}

/// <summary>已知总数、按页解析歌曲的懒队列。适合只有 offset/limit 接口的音源；
/// 命中未缓存下标时只拉所在页，不会为了开始播放先获取完整歌单。</summary>
internal sealed class PagedSongQueue : ILazySongQueue
{
    internal sealed record Page(IReadOnlyList<Song> Songs, int TotalCount);

    private readonly Func<int, int, CancellationToken, Task<Page>> _loader;
    private readonly BoundedSongCache _cache = new(384);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly int _pageSize;
    private int _count;

    public PagedSongQueue(
        int count,
        IReadOnlyList<Song> firstPage,
        Func<int, int, CancellationToken, Task<Page>> loader,
        int pageSize = 50)
    {
        _count = Math.Max(count, firstPage.Count);
        _loader = loader;
        _pageSize = Math.Max(1, pageSize);
        for (var index = 0; index < firstPage.Count; index++)
            _cache.Set(index, firstPage[index]);
    }

    public ValueTask<int> GetCountAsync(CancellationToken ct = default)
        => ValueTask.FromResult(_count);

    public async ValueTask<Song?> GetSongAsync(int index, CancellationToken ct = default)
    {
        if (index < 0 || (_count > 0 && index >= _count)) return null;
        if (_cache.TryGet(index, out var cached)) return cached;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGet(index, out cached)) return cached;
            var begin = index / _pageSize * _pageSize;
            var page = await _loader(begin, _pageSize, ct).ConfigureAwait(false);
            _count = Math.Max(page.TotalCount, begin + page.Songs.Count);
            for (var offset = 0; offset < page.Songs.Count; offset++)
                _cache.Set(begin + offset, page.Songs[offset]);
            return _cache.TryGet(index, out cached) ? cached : null;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void Remember(int index, Song song)
    {
        if (index >= 0) _cache.Set(index, song);
    }
}

/// <summary>
/// 聚合歌单逻辑队列。成员只保留计数；网易云在命中该成员时取 trackId 概览，QQ 则按命中下标取一首。
/// </summary>
internal sealed class AggregateSongQueue : ILazySongQueue
{
    internal sealed record Member(AggregatePlaylistMember Playlist, int? KnownCount);

    private sealed class MemberState
    {
        public required AggregatePlaylistMember Playlist { get; init; }
        public int? Count { get; set; }
        public IReadOnlyList<long>? NetEaseTrackIds { get; set; }
        public Task? MetadataTask { get; set; }
    }

    private readonly NetEaseApiClient _netEase;
    private readonly QQMusicApiClient _qq;
    private readonly MemberState[] _members;
    private readonly BoundedSongCache _cache = new(384);
    private readonly object _gate = new();

    public AggregateSongQueue(NetEaseApiClient netEase, QQMusicApiClient qq, IReadOnlyList<Member> members)
    {
        _netEase = netEase;
        _qq = qq;
        _members = members.Select(member => new MemberState
        {
            Playlist = member.Playlist,
            Count = member.KnownCount is >= 0 ? member.KnownCount : null,
        }).ToArray();
    }

    public async ValueTask<int> GetCountAsync(CancellationToken ct = default)
    {
        for (var index = 0; index < _members.Length; index++)
            await EnsureMetadataAsync(index, requireTrackIds: false, ct).ConfigureAwait(false);

        lock (_gate)
            return _members.Sum(member => member.Count ?? 0);
    }

    public async ValueTask<Song?> GetSongAsync(int index, CancellationToken ct = default)
    {
        if (index < 0) return null;
        if (_cache.TryGet(index, out var cached)) return cached;

        await GetCountAsync(ct).ConfigureAwait(false);
        var (memberIndex, localIndex) = Locate(index);
        if (memberIndex < 0) return null;

        var state = _members[memberIndex];
        Song? song;
        if (state.Playlist.Source == MusicSource.NetEase)
        {
            await EnsureMetadataAsync(memberIndex, requireTrackIds: true, ct).ConfigureAwait(false);
            IReadOnlyList<long>? ids;
            lock (_gate) ids = state.NetEaseTrackIds;
            if (ids is null || (uint)localIndex >= (uint)ids.Count) return null;
            var id = ids[localIndex];
            song = (await _netEase.GetSongsByIdsAsync(new[] { id }, ct).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.Id == id);
        }
        else
        {
            var page = await _qq.GetPlaylistTrackPageAsync(
                state.Playlist.PlaylistId, localIndex, 1, ct).ConfigureAwait(false);
            song = page.Songs.FirstOrDefault();
        }

        if (song is not null) _cache.Set(index, song);
        return song;
    }

    public void Remember(int index, Song song)
    {
        if (index >= 0) _cache.Set(index, song);
    }

    /// <summary>界面流式加载已经拿到权威网易云概览时同步给播放队列。</summary>
    public void ConfigureNetEaseMember(int memberIndex, NetEaseApiClient.PlaylistTrackOverview overview)
    {
        if ((uint)memberIndex >= (uint)_members.Length) return;
        lock (_gate)
        {
            var state = _members[memberIndex];
            state.Count = overview.TrackIds.Count;
            state.NetEaseTrackIds = overview.TrackIds;
        }
    }

    /// <summary>界面流式加载已经拿到 QQ 权威总数时同步给播放队列。</summary>
    public void ConfigureQqMember(int memberIndex, int totalCount)
    {
        if ((uint)memberIndex >= (uint)_members.Length || totalCount < 0) return;
        lock (_gate) _members[memberIndex].Count = totalCount;
    }

    /// <summary>成员读取失败后从本次逻辑队列排除，保持后续行下标与界面顺序一致。</summary>
    public void MarkMemberUnavailable(int memberIndex)
    {
        if ((uint)memberIndex >= (uint)_members.Length) return;
        lock (_gate)
        {
            _members[memberIndex].Count = 0;
            _members[memberIndex].NetEaseTrackIds = Array.Empty<long>();
        }
    }

    private async Task EnsureMetadataAsync(int memberIndex, bool requireTrackIds, CancellationToken ct)
    {
        var state = _members[memberIndex];
        Task? task;
        lock (_gate)
        {
            if (state.Count is not null && (!requireTrackIds
                                            || state.Playlist.Source != MusicSource.NetEase
                                            || state.NetEaseTrackIds is not null))
                return;
            task = state.MetadataTask;
            if (task is null || task.IsFaulted || task.IsCanceled)
                state.MetadataTask = task = LoadMetadataAsync(memberIndex);
        }

        await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadMetadataAsync(int memberIndex)
    {
        var state = _members[memberIndex];
        try
        {
            if (state.Playlist.Source == MusicSource.NetEase)
            {
                var overview = await _netEase.GetPlaylistTrackOverviewAsync(state.Playlist.PlaylistId)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    state.Count = overview.TrackIds.Count;
                    state.NetEaseTrackIds = overview.TrackIds;
                }
            }
            else
            {
                var page = await _qq.GetPlaylistTrackPageAsync(state.Playlist.PlaylistId, 0, 1)
                    .ConfigureAwait(false);
                lock (_gate) state.Count = page.TotalCount;
            }
        }
        catch
        {
            // 单个成员不可用不应拖垮其余成员的随机/顺序队列。
            MarkMemberUnavailable(memberIndex);
        }
    }

    private (int MemberIndex, int LocalIndex) Locate(int absoluteIndex)
    {
        lock (_gate)
        {
            var start = 0;
            for (var memberIndex = 0; memberIndex < _members.Length; memberIndex++)
            {
                var count = _members[memberIndex].Count ?? 0;
                if (absoluteIndex < start + count)
                    return (memberIndex, absoluteIndex - start);
                start += count;
            }
        }
        return (-1, -1);
    }
}

/// <summary>小型线程安全 LRU，只缓存播放附近的 Song，不随超大歌单线性增长。</summary>
internal sealed class BoundedSongCache
{
    private readonly int _capacity;
    private readonly Dictionary<int, (Song Song, LinkedListNode<int> Node)> _items = new();
    private readonly LinkedList<int> _lru = new();
    private readonly object _gate = new();

    public BoundedSongCache(int capacity) => _capacity = Math.Max(1, capacity);

    internal int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public bool TryGet(int index, out Song? song)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(index, out var item))
            {
                song = null;
                return false;
            }
            _lru.Remove(item.Node);
            _lru.AddLast(item.Node);
            song = item.Song;
            return true;
        }
    }

    public void Set(int index, Song song)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(index, out var existing))
            {
                _lru.Remove(existing.Node);
                _lru.AddLast(existing.Node);
                _items[index] = (song, existing.Node);
                return;
            }

            var node = _lru.AddLast(index);
            _items[index] = (song, node);
            while (_items.Count > _capacity && _lru.First is { } first)
            {
                _lru.RemoveFirst();
                _items.Remove(first.Value);
            }
        }
    }
}
