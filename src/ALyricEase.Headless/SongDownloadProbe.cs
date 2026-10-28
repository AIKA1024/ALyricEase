using System.Net;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;
using ALyricEase.ViewModels;

namespace ALyricEase.Headless;

internal static class SongDownloadProbe
{
    public static async Task RunAsync()
    {
        await NetEaseQualityProbe.RunOfflineAsync();
        await QqPlaybackQualityProbe.RunAsync();
        var root = Path.Combine(Path.GetTempPath(), $"aly-download-{Guid.NewGuid():N}");
        var handler = new AudioHandler();
        using var http = new HttpClient(handler);
        var cache = new MusicCacheService(128, Path.Combine(root, "cache"), http);
        var storage = new DirectorySongDownloadStorage(() => Path.Combine(root, "downloads"));
        var api = new FakeApi();
        var service = new SongDownloadService(new MusicApiProvider([api]), cache, storage, http);
        await CheckQualitiesAsync(service, api);
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
            Item = new() { Url = "https://audio.test/RS01.flac", Level = "RS01" } };
        var qqSong = new Song { Source = MusicSource.QQ, Mid = "qq-test", Name = "QQ 测试" };
        var qqQuality = SongDownloadService.GetQualities(MusicSource.QQ).Single(q => q.Level == "hires");
        var qqService = new SongDownloadService(new MusicApiProvider([qqApi]), cache, storage, http);
        var qqResult = await qqService.DownloadAsync(qqSong, qqQuality, null, default);
        Check(qqApi.Calls == 1 && qqResult.Location.EndsWith(".flac"), "QQ 音源路由及实际协议档位映射");
        Console.WriteLine("PASS: metadata filtering, quality loading/retry/empty/close/reopen, exact cache reuse, network fallback, filenames/collisions, downgrade/trial rejection, cancellation/empty cleanup, local exclusion, Hi-Res.");
    }

    private static async Task CheckQualitiesAsync(SongDownloadService service, FakeApi api)
    {
        var netease = JsonSerializer.Deserialize("""
            {"id":1,"l":{"br":128000,"size":100},"h":{"br":320000},"sq":null,"hr":null}
            """, NetEaseJsonContext.Default.SongDetailItem);
        Check(netease is not null && SongQualityMetadata.NetEase(netease.StandardQuality, netease.HighQuality,
            netease.LosslessQuality, netease.HiResQuality)!.SequenceEqual(["standard", "exhigh"]), "网易云 DTO 四档解析");
        var qq = JsonSerializer.Deserialize("""
            {"file":{"size_128mp3":100,"size_320mp3":0,"size_flac":500,"size_hires":0}}
            """, QQMusicJsonContext.Default.QQTrackDto)!;
        Check(SongQualityMetadata.QQ(qq.File)!.SequenceEqual(["standard", "lossless"]), "QQ 非连续音质不得推导中间档位");
        var desktopTrack = JsonSerializer.Deserialize("""
            {"file":{"size_128mp3":4667592,"size_320mp3":11668617,"size_flac":62427821,"size_hires":108382223,"hires_sample":96000,"hires_bitdepth":24}}
            """, QQMusicJsonContext.Default.QQTrackDto)!;
        Check(SongQualityMetadata.QQ(desktopTrack.File)!.SequenceEqual(["standard", "higher", "lossless", "hires"]),
            "融雪铭春桌面元数据必须包含 Hi-Res");
        Check(SongQualityMetadata.QQ(desktopTrack.File, hasFullQualityMetadata: false)!.Count == 4,
            "精简响应明确提供四档时可直接使用");
        var webTrack = JsonSerializer.Deserialize("""
            {"file":{"size_128mp3":4667592,"size_320mp3":11668617,"size_flac":62427821,"size_hires":0}}
            """, QQMusicJsonContext.Default.QQTrackDto)!;
        Check(SongQualityMetadata.QQ(webTrack.File, hasFullQualityMetadata: false) is null,
            "精简响应将 Hi-Res 清零时必须补查详情");
        Check(SongQualityMetadata.QQ(webTrack.File, hasFullQualityMetadata: true)!
            .SequenceEqual(["standard", "higher", "lossless"]), "完整元数据明确没有 Hi-Res 时仍只显示三档");
        Check(SongQualityMetadata.NetEase(default, default, default, default) is null
            && SongQualityMetadata.QQ(default) is null, "字段缺失必须保留未知状态");
        using var nullJson = JsonDocument.Parse("null");
        Check(SongQualityMetadata.NetEase(nullJson.RootElement, nullJson.RootElement,
            nullJson.RootElement, nullJson.RootElement)!.Count == 0, "明确无音质不能回退到四档");

        var closed = false;
        var vm = new DownloadSongDialogViewModel(service, () => closed = true);
        api.QualityRequest = (_, _) => throw new InvalidOperationException("已有元数据不应发请求");
        vm.Refresh(new Song { Id = 1, AvailableQualityLevels = ["standard", "lossless", "jyeffect"] }, 3);
        await vm.QualityLoadTask;
        Check(vm.Qualities.Count == 2 && vm.SelectedQuality?.Level == "lossless"
            && vm.DownloadCommand.CanExecute(null), "只显示四档内已存在音质，首选缺失时选较低档");
        vm.Refresh(new Song { Id = 2, AvailableQualityLevels = [] }, 0);
        await vm.QualityLoadTask;
        Check(vm.Qualities.Count == 0 && !vm.DownloadCommand.CanExecute(null), "空音质必须禁止下载");

        api.QualityRequest = (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);
        vm.Refresh(new Song { Id = 3 }, 0);
        await vm.QualityLoadTask;
        Check(vm.CanRetryQualities && vm.HasError && !vm.DownloadCommand.CanExecute(null), "未知音质不能显示全部四档");
        api.QualityRequest = (_, _) => Task.FromException<IReadOnlyList<string>?>(new HttpRequestException("offline"));
        await vm.RetryQualitiesCommand.ExecuteAsync(null);
        Check(vm.CanRetryQualities && !vm.IsLoadingQualities, "查询异常应允许重试");
        api.QualityRequest = (_, _) => Task.FromResult<IReadOnlyList<string>?>(["exhigh"]);
        await vm.RetryQualitiesCommand.ExecuteAsync(null);
        Check(vm.SelectedQuality?.Level == "exhigh" && !vm.HasError && !vm.CanRetryQualities, "重试成功应更新选择");

        var pending = new TaskCompletionSource<IReadOnlyList<string>?>();
        CancellationToken oldToken = default;
        api.QualityRequest = (_, token) => { oldToken = token; return pending.Task; };
        vm.Refresh(new Song { Id = 4 }, 3);
        var oldLoad = vm.QualityLoadTask;
        Check(vm.IsLoadingQualities && vm.Qualities.Count == 0 && !vm.DownloadCommand.CanExecute(null), "加载中不能下载");
        vm.CloseCommand.Execute(null);
        Check(closed && oldToken.IsCancellationRequested, "关闭窗口必须取消音质查询");
        vm.Refresh(new Song { Id = 5, AvailableQualityLevels = ["standard"] }, 3);
        await vm.QualityLoadTask;
        pending.SetResult(["hires"]);
        await oldLoad;
        Check(vm.SelectedQuality?.Level == "standard" && vm.Qualities.Count == 1 && !vm.IsLoadingQualities,
            "旧请求不得覆盖重新打开的歌曲");
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
        public Func<Song, CancellationToken, Task<IReadOnlyList<string>?>> QualityRequest =
            (_, _) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<IReadOnlyList<string>?> GetAvailableQualitiesAsync(Song song, CancellationToken ct = default)
            => QualityRequest(song, ct);
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
