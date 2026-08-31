using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;

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
        var picks = Enumerable.Range(0, 500)
            .Select(_ => PlayerViewModel.SelectShuffleIndex(count, 10, excluded, random))
            .ToList();
        var fullRange = picks.All(index => index >= 0 && index < count && index != 10 && !excluded.Contains(index))
                        && picks.Any(index => index >= 200);

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

        Console.WriteLine($"[lazy-playback] count={count}, resolvesOnDemand={resolvesOnDemand}, " +
                          $"fullRange={fullRange}, aggregateRange={aggregateRange}, " +
                          $"maxPick={picks.Max()}, cache={bounded.Count}/32");
        return resolvesOnDemand && fullRange && aggregateRange && boundedMemory ? 0 : 1;
    }

    private static Song ToSong(long id) => new() { Id = id, Name = $"Song {id}" };
}
