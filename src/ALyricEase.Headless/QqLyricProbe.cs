using System.Net;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Lrc;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

internal static class QqLyricProbe
{
    // 自制 QRC，含三个带时间的词、一行普通歌词、XML 转义和 offset。
    private const string Fixture =
        "EE7BEFB746F93831D42DCDF2E21F0D5D977DE00C85449A7C535DA3BCF1BEB3CA6E9B3E2EB93862B9" +
        "74306C9963A3A192D5DB92F0FD753F682AD965E5B162C97E7DBA1AFCF0D832FD6E914650C08A9CE370" +
        "48BDEE010B8E8AC25E21A8533302895F512FC8CB8C2F95D038CBA0E753BC3486594B9B49A608BF6F0FE49" +
        "006314013EA7086648E3D61A5";

    public static async Task RunAsync()
    {
        var dir = Directory.CreateTempSubdirectory("aly-qrc-");
        try
        {
            using var handler = new LyricHandler();
            var api = new QQMusicApiClient(new CookieStore(Path.Combine(dir.FullName, "cookie.json")), null, handler);
            var song = new Song { Source = MusicSource.QQ, Id = 42, Mid = "fixture" };
            var result = await api.GetLyricAsync(song) ?? throw new InvalidOperationException("QQ 歌词缺失");
            using var http = new HttpClient();
            var cache = new MusicCacheService(16, dir.FullName, http);
            await cache.CacheLyricAsync(song, result);
            var cached = await cache.TryGetLyricAsync(song) ?? throw new InvalidOperationException("QQ 歌词缓存缺失");
            var doc = LrcParser.Parse(cached.Original, cached.Translation);
            if (doc.Lines is not [{ Original: "你 & me", Translation: "翻译", Words: { } words }, { Words: null }]
                || !words.SequenceEqual([new("你", 1000, 1180), new(" & ", 1180, 1300), new("me", 1300, 1600)])
                || doc.FindIndex(975) != 0)
                throw new InvalidOperationException("QQ 缓存回读后的字级时间、翻译或 offset 错误");

            var current = dir.GetFiles("*.qrc.lyrics").Single();
            File.Move(current.FullName, current.FullName.Replace(".qrc.lyrics", ".lyrics"));
            if (await cache.TryGetLyricAsync(song) is not { Cacheable: false })
                throw new InvalidOperationException("旧 QQ 歌词应可读并在下次播放时刷新");

            handler.Payload = "FFFFFFFFFFFFFFFF";
            var fallback = await api.GetLyricAsync(song);
            if (fallback is not { Original: "[00:01]普通歌词", Cacheable: false })
                throw new InvalidOperationException("损坏的 QRC 应回退到普通歌词并允许重试");
            await cache.CacheLyricAsync(song, fallback);
            if (await cache.TryGetLyricAsync(song) is not { Cacheable: false })
                throw new InvalidOperationException("临时回退覆盖了待升级的缓存");

            handler.Payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("[00:01]普通歌词"));
            if (await api.GetLyricAsync(song) is not { Original: "[00:01]普通歌词", Cacheable: true })
                throw new InvalidOperationException("逐字接口返回的普通 LRC 应当可缓存");
            Console.WriteLine("[qq-karaoke] PASS: QRC 请求、解码、字级时间、翻译、缓存升级和 LRC 回退");
        }
        finally { dir.Delete(recursive: true); }
    }

    private sealed class LyricHandler : HttpMessageHandler
    {
        public string Payload { get; set; } = Fixture;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string json;
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (body.RootElement.GetProperty("req_0").GetProperty("param").GetProperty("qrc").GetInt32() != 1)
                    throw new InvalidOperationException("未请求 QRC");
                json = $$"""{"req_0":{"code":0,"data":{"lyric":"{{Payload}}","trans":"[00:01]翻译"} } }""";
            }
            else json = """{"code":0,"lyric":"[00:01]普通歌词","trans":"[00:01]翻译"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
