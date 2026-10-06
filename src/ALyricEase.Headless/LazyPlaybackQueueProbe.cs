using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;

namespace ALyricEase.Headless;

/// <summary>懒歌单完整播放范围回归：随机不能被 UI 已物化前缀截断，详情按命中下标解析并缓存。</summary>
internal static class LazyPlaybackQueueProbe
{
    public static async Task<int> RunAsync()
    {
        var requested = new List<long>();
        var ids = Enumerable.Range(1, 1000).Select(value => (long)value).ToList();
        var known = ids.Take(200).Select(ToSong).ToList();
        var queue = new IndexedSongQueue(ids, known, (slice, _) =>
        {
            requested.AddRange(slice);
            return Task.FromResult(slice.Select(ToSong).ToList());
        });

        var count = await queue.GetCountAsync();
        var unloaded = await queue.GetSongAsync(899);
        var cached = await queue.GetSongAsync(899);
        var resolvesOnDemand = count == 1000 && unloaded?.Id == 900 && ReferenceEquals(unloaded, cached)
                               && requested.SequenceEqual([900L]);

        var random = new Random(20260831);
        var excluded = new HashSet<int> { 3, 5, 8 };
        var bag = new ShuffleIndexBag();
        bag.Refill(count, 10, excluded, random);
        var picks = new List<int>();
        while (bag.TryTake(out var pick)) picks.Add(pick);
        var expectedCount = count - excluded.Count - 1;
        var fullRange = picks.Count == expectedCount
                        && picks.Distinct().Count() == expectedCount
                        && picks.All(index => index >= 0 && index < count
                                              && index != 10 && !excluded.Contains(index))
                        && picks.Any(index => index >= 200);

        var previousRoundLast = picks[^1];
        bag.Refill(count, previousRoundLast, excluded, random);
        var avoidsRoundBoundaryRepeat = bag.TryTake(out var nextRoundFirst)
                                        && nextRoundFirst != previousRoundLast;
        bag.PutBack(nextRoundFirst);
        var putBackOccurrences = 0;
        while (bag.TryTake(out var retryPick))
            if (retryPick == nextRoundFirst) putBackOccurrences++;
        var preservesTransientCandidate = putBackOccurrences == 1;

        var shiftedBag = new ShuffleIndexBag();
        shiftedBag.Refill(6, 0, new HashSet<int>(), new Random(7));
        shiftedBag.RemoveAndShift(2);
        shiftedBag.InsertAndShift(2);
        var shiftedPicks = new List<int>();
        while (shiftedBag.TryTake(out var shiftedPick)) shiftedPicks.Add(shiftedPick);
        shiftedPicks.Sort();
        var preservesNextInsertion = shiftedPicks.SequenceEqual([1, 3, 4, 5]);

        var bounded = new BoundedSongCache(32);
        for (var index = 0; index < 1000; index++) bounded.Set(index, ToSong(index));
        var boundedMemory = bounded.Count == 32;

        var aggregate = new AggregateSongQueue(null!, null!,
        [
            new(new AggregatePlaylistMember { Source = MusicSource.NetEase, PlaylistId = 1 }, 400),
            new(new AggregatePlaylistMember { Source = MusicSource.QQ, PlaylistId = 2 }, 600),
        ]);
        aggregate.Remember(850, ToSong(851));
        var aggregateRange = await aggregate.GetCountAsync() == 1000
                             && (await aggregate.GetSongAsync(850))?.Id == 851;

        var pageRequests = new List<(int Begin, int Count)>();
        var paged = new PagedSongQueue(
            1000,
            ids.Take(50).Select(ToSong).ToList(),
            (begin, take, _) =>
            {
                pageRequests.Add((begin, take));
                var songs = ids.Skip(begin).Take(take).Select(ToSong).ToList();
                return Task.FromResult(new PagedSongQueue.Page(songs, 1000));
            });
        var pagedUnloaded = await paged.GetSongAsync(899);
        var pagedCached = await paged.GetSongAsync(899);
        var pagedOnDemand = pagedUnloaded?.Id == 900
                            && ReferenceEquals(pagedUnloaded, pagedCached)
                            && pageRequests.SequenceEqual([(850, 50)]);

        Console.WriteLine($"[lazy-playback] count={count}, resolvesOnDemand={resolvesOnDemand}, " +
                          $"fullRange={fullRange}, boundary={avoidsRoundBoundaryRepeat}, " +
                          $"putBack={preservesTransientCandidate}, nextInsert={preservesNextInsertion}, " +
                          $"aggregateRange={aggregateRange}, pagedOnDemand={pagedOnDemand}, " +
                          $"maxPick={picks.Max()}, cache={bounded.Count}/32");
        return resolvesOnDemand && fullRange && avoidsRoundBoundaryRepeat
               && preservesTransientCandidate && preservesNextInsertion
               && aggregateRange && pagedOnDemand && boundedMemory ? 0 : 1;
    }

    private static Song ToSong(long id) => new() { Id = id, Name = $"Song {id}" };
}
