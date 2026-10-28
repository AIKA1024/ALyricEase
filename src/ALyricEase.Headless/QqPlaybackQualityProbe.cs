using System.Net;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

internal static class QqPlaybackQualityProbe
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-qq-playback-{Guid.NewGuid():N}");
        var cookie = new CookieStore(Path.Combine(root, "cookie.json"));
        Song SongWith(IReadOnlyList<string>? levels) => new()
            { Id = 1, Mid = "test-mid", Source = MusicSource.QQ, AvailableQualityLevels = levels };

        async Task CheckCase(string detail, IReadOnlyList<string>? known, string? expected,
            string[] prefixes, bool rejectLossless = false)
        {
            var handler = new PlaybackHandler(detail, rejectLossless);
            var api = new QQMusicApiClient(cookie, null, handler);
            var result = await api.GetPlayUrlAsync(SongWith(known), "hires");
            Check(result?.Level == expected, $"播放应返回 {expected ?? "无地址"}");
            Check(handler.Prefixes.SequenceEqual(prefixes), "必须先筛选音质，再请求实际档位的 vkey");
            Check(handler.DetailCalls == (known is null ? 1 : 0), "已有元数据无需补查");
        }

        const string unknown = """
            {"code":0,"req_0":{"data":{"track_info":{"id":1,"mid":"test-mid","file":{}}}}}
            """;
        const string empty = """
            {"code":0,"req_0":{"data":{"track_info":{"id":1,"mid":"test-mid","file":{"size_128mp3":0,"size_320mp3":0,"size_flac":0,"size_hires":0}}}}}
            """;
        const string hires = """
            {"code":0,"req_0":{"data":{"track_info":{"id":1,"mid":"test-mid","file":{"size_128mp3":10,"size_320mp3":20,"size_flac":30,"size_hires":40}}}}}
            """;
        await CheckCase(unknown, [], null, []);
        await CheckCase(empty, null, null, []);
        await CheckCase(unknown, null, "F000", ["F000"]);
        await CheckCase("{\"code\":500}", null, "F000", ["F000"]);
        await CheckCase(unknown, null, "M800", ["F000", "M800"], rejectLossless: true);
        await CheckCase(unknown, ["standard", "lossless"], "F000", ["F000"]);
        await CheckCase(unknown, ["hires"], "RS01", ["RS01"]);
        await CheckCase(hires, null, "RS01", ["RS01"]);

        var failure = new PlaybackHandler("{\"code\":500}");
        var client = new QQMusicApiClient(cookie, null, failure);
        try
        {
            await client.GetAvailableQualitiesAsync(SongWith(null));
            throw new InvalidOperationException("下载音质查询应保留失败语义");
        }
        catch (ApiException) { }
        Check(failure.Prefixes.Count == 0, "下载音质失败不得尝试取播放地址");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await client.GetPlayUrlAsync(SongWith(null), "hires", cancelled.Token);
            throw new InvalidOperationException("取消必须透传");
        }
        catch (OperationCanceledException) { }
        Check(failure.DetailCalls == 1 && failure.Prefixes.Count == 0, "取消不能触发补查或降级");
        Console.WriteLine("PASS: QQ unknown/error skips RS01, empty stops vkey, confirmed Hi-Res retained, lower-tier fallback and cancellation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PlaybackHandler(string detail, bool rejectLossless = false) : HttpMessageHandler
    {
        public int DetailCalls;
        public List<string> Prefixes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var module = doc.RootElement.GetProperty("req_0");
            string body;
            if (module.GetProperty("method").GetString() == "get_song_detail_yqq")
            {
                DetailCalls++;
                body = detail;
            }
            else
            {
                Check(module.GetProperty("method").GetString() == "CgiGetVkey", "非预期请求");
                var prefix = module.GetProperty("param").GetProperty("filename")[0].GetString()![..4];
                Prefixes.Add(prefix);
                // 即便请求 RS01 也给非空地址，模拟原本会阻断降级的幽灵 purl。
                var purl = rejectLossless && prefix == "F000" ? "" : prefix + ".flac";
                body = "{\"code\":0,\"req_0\":{\"data\":{\"sip\":[\"https://audio.test/\"],"
                    + "\"midurlinfo\":[{\"songmid\":\"test-mid\",\"purl\":\"" + purl + "\"}]}}}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
