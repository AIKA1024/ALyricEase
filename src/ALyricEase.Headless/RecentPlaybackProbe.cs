using ALyricEase.Models;
using ALyricEase.Services;

namespace ALyricEase.Headless;

/// <summary>最近播放状态回归：次数排序、同次数最近优先、容量上限、元数据持久化与清空落盘。</summary>
internal static class RecentPlaybackProbe
{
    public static int Run()
    {
        var probeRoot = Path.Combine(Path.GetTempPath(), "ALyricEase.RecentPlaybackProbe", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(probeRoot, "state.json");
        try
        {
            var state = new AppStateStore(statePath);
            state.RecordRecentSong(CreateNetEaseSong(1, "第一首"));
            state.RecordRecentSong(new Song
            {
                Source = MusicSource.QQ,
                Mid = "qq-two",
                Name = "第二首",
                Artist = "QQ 歌手",
                ArtistNames = new[] { "QQ 歌手" },
                ArtistMids = new[] { "singer-mid" },
                Album = "QQ 专辑",
                AlbumMid = "album-mid",
                DurationMs = 180_000,
            });
            state.RecordRecentSong(CreateNetEaseSong(1, "第一首（新元数据）"));
            state.Flush();

            var reloaded = new AppStateStore(statePath);
            var dedupOk = reloaded.RecentSongs.Count == 2
                          && reloaded.RecentSongs[0].Name == "第一首（新元数据）"
                          && reloaded.RecentSongs[1].Mid == "qq-two"
                          && reloaded.RecentSongs[1].ArtistMids.SequenceEqual(new[] { "singer-mid" });

            reloaded.RecordRecentSong(new Song
            {
                Source = MusicSource.QQ,
                Mid = "qq-two",
                Name = "第二首",
                Artist = "QQ 歌手",
            });
            reloaded.RecordRecentSong(new Song
            {
                Source = MusicSource.QQ,
                Mid = "qq-two",
                Name = "第二首",
                Artist = "QQ 歌手",
            });
            reloaded.Flush();

            var ranked = new AppStateStore(statePath);
            var rankingOk = ranked.RecentSongs.Count == 2
                            && ranked.RecentSongs[0].Mid == "qq-two"
                            && ranked.RecentSongs[1].Id == 1;

            for (var i = 0; i < 105; i++)
                ranked.RecordRecentSong(CreateNetEaseSong(1_000 + i, $"容量歌曲 {i}"));
            ranked.Flush();

            var capped = new AppStateStore(statePath);
            var capOk = capped.RecentSongs.Count == AppStateStore.MaximumRecentSongCount
                        && capped.RecentSongs[0].Mid == "qq-two"
                        && capped.RecentSongs[1].Id == 1
                        && capped.RecentSongs[2].Id == 1_104
                        && capped.RecentSongs[^1].Id == 1_007;

            capped.ClearRecentSongs();
            var cleared = new AppStateStore(statePath);
            var clearOk = cleared.RecentSongs.Count == 0;

            Console.WriteLine($"[recent-history] 去重/元数据={(dedupOk ? "OK" : "FAIL")} " +
                              $"次数排序={(rankingOk ? "OK" : "FAIL")} " +
                              $"容量/顺序={(capOk ? "OK" : "FAIL")} 清空落盘={(clearOk ? "OK" : "FAIL")}");
            return dedupOk && rankingOk && capOk && clearOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[recent-history] FAIL {ex}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(probeRoot)) Directory.Delete(probeRoot, recursive: true);
        }
    }

    private static Song CreateNetEaseSong(long id, string name) => new()
    {
        Id = id,
        Source = MusicSource.NetEase,
        Name = name,
        Artist = "歌手",
        ArtistIds = new[] { 88L },
        ArtistNames = new[] { "歌手" },
        Album = "专辑",
        AlbumId = 99,
        CoverUrl = "https://example.invalid/cover.jpg",
        DurationMs = 200_000,
    };
}
