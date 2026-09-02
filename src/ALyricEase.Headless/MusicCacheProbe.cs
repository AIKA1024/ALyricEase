using System.Net;
using ALyricEase.Models;
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

            await cache.ClearAsync();
            if (!File.Exists(highLease.FilePath))
                return Fail("清理删除了正在播放的高音质文件");
            if (await cache.TryGetCoverAsync(coverUrl) is not null
                || await cache.TryGetLyricAsync(song) is not null)
                return Fail("清理后封面或歌词仍存在");

            var highPath = highLease.FilePath;
            highLease.Dispose();
            if (File.Exists(highPath) || cache.GetCurrentSizeBytes() != 0)
                return Fail("租约释放后未删除待清理文件");

            Console.WriteLine("[media-cache] PASS: 音质升级/复用、封面、歌词翻译与统一清理均正常");
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
