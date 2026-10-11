using System.Net;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;

namespace ALyricEase.Headless;

internal static class SongDownloadProbe
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-download-{Guid.NewGuid():N}");
        var handler = new AudioHandler();
        using var http = new HttpClient(handler);
        var cache = new MusicCacheService(128, Path.Combine(root, "cache"), http);
        var storage = new DirectorySongDownloadStorage(() => Path.Combine(root, "downloads"));
        var api = new FakeApi();
        var service = new SongDownloadService(new MusicApiProvider([api]), cache, storage, http);
        var song = new Song { Id = 1, Name = "歌曲/名称:*?", Artist = "歌手" };
        var quality = SongDownloadService.GetQualities(song.Source).Single(q => q.Level == "lossless");
        await cache.CacheAsync(song, "lossless", "https://audio.test/cached.flac");
        var calls = handler.Calls;
        var cached = await service.DownloadAsync(song, quality, null, default);
        Check(cached.FromCache && api.Calls == 0 && handler.Calls == calls, "缓存复用不得请求网络或 API");
        Check(File.ReadAllBytes(cached.Location).SequenceEqual(AudioHandler.Bytes), "缓存复制必须完整");
        var duplicate = await service.DownloadAsync(song, quality, null, default);
        Check(duplicate.Location != cached.Location && File.Exists(cached.Location), "重名不得覆盖已有文件");
        Check(!Path.GetFileName(cached.Location).Contains('?'), "非法文件名应清理");

        var lower = SongDownloadService.GetQualities(song.Source).Single(q => q.Level == "exhigh");
        api.Item = new() { Url = "https://audio.test/song.mp3", Level = "exhigh" };
        var online = await service.DownloadAsync(song, lower, null, default);
        Check(!online.FromCache && api.Calls == 1 && handler.Calls == calls + 1,
            "高档缓存不能替代用户选定音质");
        Check(online.Location.EndsWith(".mp3"), "应保留实际音频扩展名");

        api.Item = new() { Url = "https://audio.test/song.mp3", Level = "standard" };
        await ExpectFailure<InvalidOperationException>(() => service.DownloadAsync(song, lower, null, default));
        Check(handler.Calls == calls + 1, "降级音质不得开始下载");
        api.Item = new() { Url = "https://audio.test/song.mp3", Level = "exhigh", IsTrial = true };
        await ExpectFailure<InvalidOperationException>(() => service.DownloadAsync(song, lower, null, default));
        Check(handler.Calls == calls + 1, "试听音频不得作为完整歌曲下载");

        api.Item = new() { Url = "https://audio.test/song.mp3", Level = "exhigh" };
        handler.Empty = true;
        var before = Directory.GetFiles(Path.Combine(root, "downloads")).Length;
        await ExpectFailure<IOException>(() => service.DownloadAsync(song, lower, null, default));
        Check(Directory.GetFiles(Path.Combine(root, "downloads")).Length == before, "空文件失败应清理临时文件");
        using var cts = new CancellationTokenSource();
        await ExpectFailure<OperationCanceledException>(() => storage.SaveAsync("cancel.mp3", "audio/mpeg",
            async (output, token) =>
            {
                await output.WriteAsync(AudioHandler.Bytes, token);
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }, cts.Token));
        Check(Directory.GetFiles(Path.Combine(root, "downloads")).Length == before, "取消不得留下半成品");
        await ExpectFailure<InvalidOperationException>(() => service.DownloadAsync(
            new Song { Source = MusicSource.Local }, quality, null, default));
        Check(MusicCacheService.GetAudioExtension("https://audio.test/TL01.nac", null) == ".nac", "QQ NAC 扩展名");
        handler.Empty = false;
        var qqApi = new FakeApi { Source = MusicSource.QQ,
            Item = new() { Url = "https://audio.test/TL01.nac", Level = "TL01" } };
        var qqSong = new Song { Source = MusicSource.QQ, Mid = "qq-test", Name = "QQ 测试" };
        var qqQuality = SongDownloadService.GetQualities(MusicSource.QQ).Single(q => q.Level == "nac");
        var qqService = new SongDownloadService(new MusicApiProvider([qqApi]), cache, storage, http);
        var qqResult = await qqService.DownloadAsync(qqSong, qqQuality, null, default);
        Check(qqApi.Calls == 1 && qqResult.Location.EndsWith(".nac"), "QQ 音源路由及实际协议档位映射");
        Console.WriteLine("PASS: exact cache reuse, network fallback, filenames/collisions, downgrade/trial rejection, cancellation/empty cleanup, local exclusion, NAC.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task ExpectFailure<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private sealed class AudioHandler : HttpMessageHandler
    {
        public static readonly byte[] Bytes = Enumerable.Range(0, 10000).Select(i => (byte)i).ToArray();
        public int Calls;
        public bool Empty;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Empty ? [] : Bytes),
            });
        }
    }

    private sealed class FakeApi : IMusicApi
    {
        public int Calls;
        public PlayUrlItem? Item;
        public MusicSource Source { get; init; } = MusicSource.NetEase;
        public string DisplayName => "测试";
        public bool IsLoggedIn => true;
        public bool IsVip => true;
        public bool IsVipLoaded => true;
        public Task EnsureVipStatusAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<PlayUrlItem?> GetPlayUrlAsync(Song song, string level = "higher", CancellationToken ct = default)
        { Calls++; return Task.FromResult(Item); }
        public Task<List<Song>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
            => Task.FromResult(new List<Song>());
        public Task<LyricResult?> GetLyricAsync(Song song, CancellationToken ct = default) => Task.FromResult<LyricResult?>(null);
        public Task<Song?> GetSongDetailAsync(long id, CancellationToken ct = default) => Task.FromResult<Song?>(null);
    }
}
