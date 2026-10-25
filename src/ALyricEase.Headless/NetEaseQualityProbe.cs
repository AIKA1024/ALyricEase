using System.Net;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

internal static class NetEaseQualityProbe
{
    public static async Task<int> RunAsync(long id, bool cloud)
    {
        var source = new CookieStore();
        var root = Path.Combine(Path.GetTempPath(), $"aly-ne-quality-{Guid.NewGuid():N}");
        var cookie = new CookieStore(Path.Combine(root, "cookie.json"))
        {
            MusicU = source.MusicU, Csrf = source.Csrf,
            AnonymousMusicA = source.AnonymousMusicA, AnonymousExpiresUtc = source.AnonymousExpiresUtc,
        };
        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), cookie);
        using var http = new HttpClient();
        var service = new SongDownloadService(new MusicApiProvider([api]),
            new MusicCacheService(128, Path.Combine(root, "cache"), http),
            new DirectorySongDownloadStorage(() => Path.Combine(root, "downloads")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var song = cloud ? (await api.GetCloudListAsync(1, 0, timeout.Token)).Songs.Single()
            : new Song { Id = id };
        // 清空列表元数据，确保实际覆盖下载时的补查通道。
        var qualities = await service.GetAvailableQualitiesAsync(new Song { Id = song.Id }, timeout.Token);
        Check(qualities is { Count: > 0 }, "真实接口应返回可用音质");
        Console.WriteLine($"PASS: {(cloud ? "cloud" : "catalog")} id={song.Id}: {string.Join(",", qualities!.Select(q => q.Level))}");
        return 0;
    }

    public static async Task RunOfflineAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-ne-quality-test-{Guid.NewGuid():N}");
        var cookie = new CookieStore(Path.Combine(root, "cookie.json"));
        var handler = new MetadataHandler();
        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), cookie, handler);
        var song = new Song { Id = 1 };
        handler.Replies.Enqueue("""
            {"code":200,"data":{"l":{"size":100},"h":{"br":320000},"sq":{"size":1000},"hr":null}}
            """);
        var levels = await api.GetAvailableQualitiesAsync(song);
        Check(levels!.SequenceEqual(["standard", "exhigh", "lossless"]), "专用接口三档解析");
        Check(handler.Paths.Single() == "/eapi/song/music/detail/get", "优先使用专用 eapi 音质接口");

        handler.Paths.Clear();
        handler.Replies.Enqueue("");
        handler.Replies.Enqueue("""
            {"code":200,"songs":[{"id":99,"l":{"size":1}},{"id":1,"l":{"size":100},"h":null,"sq":null,"hr":{"size":500}}]}
            """);
        levels = await api.GetAvailableQualitiesAsync(song);
        Check(levels!.SequenceEqual(["standard", "hires"]), "空响应回退后应按歌曲 ID 匹配且不推测中间档位");
        Check(handler.Paths.SequenceEqual(["/eapi/song/music/detail/get", "/eapi/v3/song/detail"]),
            "补查仍走客户端通道");

        handler.Replies.Enqueue("{\"code\":200,\"data\":null}");
        handler.Replies.Enqueue("{\"code\":200,\"songs\":[]}");
        Check(await api.GetAvailableQualitiesAsync(song) is null, "两接口均缺失元数据时保留未知");
        handler.Paths.Clear();
        handler.Replies.Enqueue("{\"code\":200,\"data\":{\"l\":null,\"h\":null,\"sq\":null,\"hr\":null}}");
        Check((await api.GetAvailableQualitiesAsync(song))!.Count == 0 && handler.Paths.Count == 1,
            "明确无音质不可补成全部四档");

        handler.Paths.Clear();
        handler.Replies.Enqueue("{\"code\":405}");
        try { await api.GetAvailableQualitiesAsync(song); throw new InvalidOperationException("应保留限速错误"); }
        catch (ApiException ex) when (ex.Code == 405) { }
        Check(handler.Paths.Count == 1, "限速不得换端点重复请求");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await api.GetAvailableQualitiesAsync(song, cancelled.Token); throw new InvalidOperationException("应取消"); }
        catch (OperationCanceledException) { }
        Check(handler.Paths.Count == 1, "已取消不得发送请求");
        levels = await api.GetAvailableQualitiesAsync(new Song { Id = 2, AvailableQualityLevels = ["hires"] });
        Check(levels!.SequenceEqual(["hires"]) && handler.Paths.Count == 1, "已有元数据直接复用");
        Check(handler.Replies.Count == 0, "测试响应应全部消费");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        public Queue<string> Replies { get; } = new();
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(request.RequestUri!.Host == "interface.music.163.com" && request.Method == HttpMethod.Post,
                "必须使用客户端网关 POST");
            Paths.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Replies.Dequeue()) });
        }
    }
}
