using System.Net;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;

namespace ALyricEase.Headless;

/// <summary>统一媒体缓存回归：音质替换、封面/歌词持久化、播放中固定与清理。</summary>
internal static class MusicCacheProbe
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alyric-music-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var handler = new CountingHandler();
            using var http = new HttpClient(handler);
            var cache = new MusicCacheService(128, root, http);
            if (MusicCacheService.GetQualityRank(MusicSource.NetEase, "sky")
                    <= MusicCacheService.GetQualityRank(MusicSource.NetEase, "lossless")
                || MusicCacheService.GetQualityRank(MusicSource.QQ, "F000")
                    <= MusicCacheService.GetQualityRank(MusicSource.QQ, "M800"))
                return Fail("音源音质等级顺序错误");
            if (MusicCacheService.GetQualityRankFromBr(999000) != 4
                || MusicCacheService.GetQualityRankFromBr(320000) != 3
                || MusicCacheService.GetQualityRankFromBr(192000) != 2
                || MusicCacheService.GetQualityRankFromBr(128000) != 1
                || MusicCacheService.GetQualityRankFromBr(0) != 0)
                return Fail("码率反推音质等级错误");

            var song = new Song
            {
                Id = 42,
                Source = MusicSource.NetEase,
                Name = "缓存探针",
            };

            await cache.CacheAsync(song, "higher", "https://example.test/42-low.mp3");
            var lowLease = cache.TryAcquire(song, "higher");
            if (lowLease is null || handler.RequestCount != 1 || !File.Exists(lowLease.FilePath))
                return Fail("低音质首次缓存或命中失败");

            using (var shouldMiss = cache.TryAcquire(song, "lossless"))
                if (shouldMiss is not null)
                    return Fail("低音质缓存错误满足了高音质请求");

            using (var offlineFallback = cache.TryAcquireBestAvailable(song))
                if (offlineFallback is null || offlineFallback.QualityRank != 2)
                    return Fail("断网时没有回退到本地最高可用音质");

            await cache.CacheAsync(song, "lossless", "https://example.test/42-high.flac");
            var highLease = cache.TryAcquire(song, "higher");
            if (highLease is null || highLease.QualityRank != 4 || handler.RequestCount != 2)
                return Fail("高音质没有替换低音质，或低音质请求未复用高音质");

            var lowPath = lowLease.FilePath;
            lowLease.Dispose();
            if (File.Exists(lowPath))
                return Fail("高音质写入后低音质文件未删除");

            await cache.CacheAsync(song, "higher", "https://example.test/42-low-again.mp3");
            if (handler.RequestCount != 2)
                return Fail("已有高音质缓存时仍下载了低音质");

            var coverBytes = new byte[] { 8, 6, 7, 5, 3, 0, 9 };
            const string coverUrl = "https://example.test/cover.jpg?size=300";
            await cache.CacheCoverAsync(coverUrl, coverBytes);
            var cachedCover = await cache.TryGetCoverAsync(coverUrl);
            if (cachedCover is null || !cachedCover.SequenceEqual(coverBytes))
                return Fail("封面缓存读写失败");

            var lyric = new LyricResult { Original = "[00:01]原文", Translation = "[00:01]翻译" };
            await cache.CacheLyricAsync(song, lyric);
            var cachedLyric = await cache.TryGetLyricAsync(song);
            if (cachedLyric?.Original != lyric.Original || cachedLyric.Translation != lyric.Translation)
                return Fail("原文歌词或翻译缓存读写失败");

            var playlist = new Playlist
            {
                Id = 7,
                Source = MusicSource.NetEase,
                Name = "离线歌单",
                TrackCount = 1,
                CoverUrl = coverUrl,
            };
            var secondPlaylist = new Playlist
            {
                Id = 9,
                Source = MusicSource.NetEase,
                Name = "第二歌单",
            };
            await cache.CachePlaylistListAsync(
                MusicSource.NetEase, "离线用户", [playlist, secondPlaylist]);
            await cache.CachePlaylistTracksAsync(playlist, [song]);
            if (cache.RetainedInlinePlaylistTrackCount != 0)
                return Fail("歌单曲目仍常驻在单例离线总索引中");

            // 新实例模拟应用重启，确保不是只在本次运行的内存里可见。
            var restoredCache = new MusicCacheService(128, root, http);
            var library = restoredCache.TryGetPlaylistLibrary(MusicSource.NetEase);
            var restoredTracks = await restoredCache.TryGetPlaylistTracksAsync(playlist);
            if (library?.UserName != "离线用户"
                || library.Playlists.Count != 2
                || library.Playlists[0].Name != playlist.Name
                || library.Playlists[1].Name != secondPlaylist.Name
                || restoredTracks.Count != 1
                || restoredTracks[0].Name != song.Name)
                return Fail("歌单顺序或曲目离线索引未能跨重启恢复");
            if (restoredCache.RetainedInlinePlaylistTrackCount != 0)
                return Fail("重启后歌单曲目重新常驻进总索引");
            if (!await VerifyLegacyInlineTrackMigrationAsync(root, http, playlist, song))
                return Fail("旧版内嵌歌单曲目没有迁移到独立磁盘文件");

            var uncachedSong = new Song { Id = 404, Source = MusicSource.NetEase, Name = "未缓存" };
            if (!cache.IsAudioCached(song) || cache.IsAudioCached(uncachedSong))
                return Fail("离线歌曲缓存状态判断错误");

            var publicPlaylist = new Playlist
            {
                Id = 8,
                Source = MusicSource.QQ,
                Name = "公共歌单",
            };
            await cache.CachePlaylistTracksAsync(publicPlaylist, [song]);
            if (cache.TryGetPlaylistLibrary(MusicSource.QQ) is not null)
                return Fail("仅打开公共歌单时错误伪造了账号歌单列表");

            await cache.ClearAsync();
            if (!File.Exists(highLease.FilePath))
                return Fail("清理删除了正在播放的高音质文件");
            if (await cache.TryGetCoverAsync(coverUrl) is not null
                || await cache.TryGetLyricAsync(song) is not null
                || cache.TryGetPlaylistLibrary(MusicSource.NetEase) is not null)
                return Fail("清理后封面、歌词或离线歌单索引仍存在");

            var highPath = highLease.FilePath;
            highLease.Dispose();
            if (File.Exists(highPath) || cache.GetCurrentSizeBytes() != 0)
                return Fail("租约释放后未删除待清理文件");

            Console.WriteLine("[media-cache] PASS: 音质升级/断网降级、封面、歌词、离线歌单索引与统一清理均正常");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("[music-cache] FAIL: " + message);
        return 1;
    }

    private static async Task<bool> VerifyLegacyInlineTrackMigrationAsync(
        string root, HttpClient http, Playlist playlist, Song song)
    {
        var migrationRoot = Path.Combine(root, "legacy-inline");
        Directory.CreateDirectory(migrationRoot);
        var legacy = new MusicCacheIndexFile
        {
            Accounts =
            [
                new CachedAccountFile
                {
                    Source = (int)playlist.Source,
                    HasPlaylistList = true,
                    Playlists =
                    [
                        new CachedPlaylistFile
                        {
                            Id = playlist.Id,
                            Source = (int)playlist.Source,
                            Name = playlist.Name,
                            Listed = true,
                            Tracks =
                            [
                                new CachedSongFile
                                {
                                    Id = song.Id,
                                    Source = (int)song.Source,
                                    Name = song.Name,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
        await File.WriteAllTextAsync(
            Path.Combine(migrationRoot, "offline-index.json"),
            JsonSerializer.Serialize(legacy, MusicCacheJsonContext.Default.MusicCacheIndexFile));

        var migrated = new MusicCacheService(128, migrationRoot, http);
        var tracks = await migrated.TryGetPlaylistTracksAsync(playlist);
        return migrated.RetainedInlinePlaylistTrackCount == 0
               && tracks.Count == 1
               && tracks[0].Name == song.Name;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new ByteArrayContent([1, 2, 3, 4, 5]);
            content.Headers.ContentType = new("audio/mpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }
}
