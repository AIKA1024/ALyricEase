using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

/// <summary>网易云加密通道诊断(--nediag):定位 playlist/create 空响应的成因。
/// A. 强制重跑匿名注册(weapi+CN IP):判断加密 POST 通道当前是否整体被拦;
/// B. 明文 form POST /api/playlist/create:老 app-api 风格明文写是否可用;
/// C. weapi /weapi/playlist/create:打印原始状态码与 body 前段。
/// B/C 若成功建出测试歌单,尝试 weapi 删除还原。</summary>
public static class NetEaseDiagProbe
{
    private const string BaseUrl = "https://music.163.com";

    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var store = new CookieStore();
        Console.WriteLine($"[nediag] MUSIC_U={CookieStore.Mask(store.MusicU)} anon已缓存={store.AnonymousMusicA is not null}");
        var crypto = new CryptoService();

        // D. 吐出可复放的加密请求体(curl 复放隔离客户端指纹 vs 请求内容)
        if (Environment.GetEnvironmentVariable("NEDIAG_DUMP") == "1")
        {
            var reg = crypto.EncryptWeapi(new Dictionary<string, object?> { ["csrf_token"] = "" });
            var cr = crypto.EncryptWeapi(new Dictionary<string, object?>
            {
                ["name"] = $"ALyricEase诊断{DateTime.Now:MMdd-HHmmss}",
                ["privacy"] = "10",
                ["type"] = "NORMAL",
                ["description"] = "",
                ["work"] = "",
                ["csrf_token"] = "",
            });
            Console.WriteLine("[nediag-D] REGISTER params=" + reg.Params);
            Console.WriteLine("[nediag-D] REGISTER encSecKey=" + reg.EncSecKey);
            Console.WriteLine("[nediag-D] CREATE params=" + cr.Params);
            Console.WriteLine("[nediag-D] CREATE encSecKey=" + cr.EncSecKey);
            return 0;
        }

        // A. 匿名注册(weapi + CN IP):加密 POST 的"晴雨表"
        var fresh = new CookieStore
        {
            AnonymousMusicA = null,
            AnonymousExpiresUtc = DateTime.MinValue,
        };
        var probeApi = new NetEaseApiClient(crypto, new CnIpPool(), fresh);
        try
        {
            await probeApi.EnsureAnonymousAsync();
            Console.WriteLine($"[nediag-A] 匿名注册成功,加密 POST 通道活着(MUSIC_A={CookieStore.Mask(fresh.AnonymousMusicA)})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[nediag-A] 匿名注册失败 → 加密 POST 当前整体被拦: {ex.Message}");
        }

        if (store.MusicU is not { Length: > 0 })
        {
            Console.WriteLine("[nediag] 无 MUSIC_U,B/C 步骤跳过");
            return 0;
        }

        var name = $"ALyricEase诊断{DateTime.Now:MMdd-HHmmss}";
        var http = BuildHttp(store.MusicU);
        long createdId = 0;

        // B. 明文 form POST /api/playlist/create
        try
        {
            using var resp = await http.PostAsync($"{BaseUrl}/api/playlist/create", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("name", name),
                new KeyValuePair<string, string>("privacy", "10"),
                new KeyValuePair<string, string>("type", "NORMAL"),
            ]));
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[nediag-B] 明文创建 HTTP {(int)resp.StatusCode} len={body.Length}: {Short(body)}");
            createdId = TryPickId(body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[nediag-B] 明文创建异常: {ex.Message}");
        }

        // C. weapi 创建
        try
        {
            var (parameters, encSecKey) = crypto.EncryptWeapi(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["privacy"] = "10",
                ["type"] = "NORMAL",
                ["description"] = "",
                ["work"] = "",
                ["csrf_token"] = "",
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/weapi/playlist/create?csrf_token=");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            req.Headers.Referrer = new Uri(BaseUrl);
            req.Headers.TryAddWithoutValidation("X-Real-IP", new CnIpPool().Next());
            req.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("params", parameters),
                new KeyValuePair<string, string>("encSecKey", encSecKey),
            ]);
            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[nediag-C] weapi 创建 HTTP {(int)resp.StatusCode} len={body.Length}: {Short(body)}");
            createdId = createdId == 0 ? TryPickId(body) : createdId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[nediag-C] weapi 创建异常: {ex.Message}");
        }

        if (createdId != 0)
        {
            try
            {
                var api = new NetEaseApiClient(crypto, new CnIpPool(), store);
                await api.DeletePlaylistAsync(createdId);
                Console.WriteLine($"[nediag] 已删除测试歌单 {createdId},账号还原");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[nediag][WARN] 删除测试歌单 {createdId} 失败(账号残留): {ex.Message}");
            }
        }

        // D/E. eapi /api/playlist/create 打客户端专用域名(interface/interface3,与网页版不同边缘):
        // 任一成功则网易创建在该域名可用(客户端实现改走此域名),失败歌单随后删除还原。
        foreach (var host in new[] { "https://interface.music.163.com", "https://interface3.music.163.com" })
        {
            long eapiId = 0;
            try
            {
                var (status, body) = await EapiCreateAsync(http, crypto, store.MusicU, host, name);
                eapiId = TryPickId(body);
                Console.WriteLine($"[nediag-{host.Split("//")[1].Split(".")[0]}] eapi 创建 HTTP {status}: {Short(body)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[nediag-{host.Split("//")[1].Split(".")[0]}] eapi 创建异常: {ex.Message}");
            }
            if (eapiId != 0)
            {
                try
                {
                    var (ds, db) = await EapiDeleteAsync(http, crypto, store.MusicU, host, eapiId);
                    Console.WriteLine($"[nediag] eapi 删除 {eapiId} HTTP {ds}: {Short(db)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[nediag][WARN] eapi 删除 {eapiId} 失败(账号残留): {ex.Message}");
                }
            }
        }
        // E. HTTP/2 对照:浏览器对 music.163.com 走 h2,以上失败样本全是 1.1 ——
        // 用 SocketsHttpHandler RequestVersion=2.0 复放 weapi 创建,验证协议版本是否是放行条件。
        try
        {
            using var h2 = BuildHttp(store.MusicU);
            h2.DefaultRequestVersion = new Version(2, 0);
            h2.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            var (parameters, encSecKey) = crypto.EncryptWeapi(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["privacy"] = "10",
                ["type"] = "NORMAL",
                ["description"] = "",
                ["work"] = "",
                ["csrf_token"] = "",
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/weapi/playlist/create?csrf_token=");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            req.Headers.Referrer = new Uri(BaseUrl);
            req.Headers.TryAddWithoutValidation("Origin", BaseUrl);
            req.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("params", parameters),
                new KeyValuePair<string, string>("encSecKey", encSecKey),
            ]);
            using var resp = await h2.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[nediag-E] h2 创建 HTTP {(int)resp.StatusCode} ver={resp.Version} len={body.Length}: {Short(body)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[nediag-E] h2 创建异常: {ex.Message}");
        }

        return 0;
    }

    /// <summary>手搓 eapi 请求(与 NetEaseApiClient.CreatePlaylistEapiAsync 同参形态,域名可换):
    /// 明文体顶层 header(PC 客户端字段)+ x-anticheattoken(HTTP 头与 header 各一份)。</summary>
    private static async Task<(int Status, string Body)> EapiCreateAsync(
        HttpClient http, CryptoService crypto, string musicU, string host, string name)
    {
        var header = new Dictionary<string, object?>
        {
            ["os"] = "pc",
            ["appver"] = "3.1.19.204510",
            ["requestId"] = 0,
            ["osver"] = "Microsoft-Windows-11-Home-China-build-22631-64bit",
            ["MUSIC_U"] = musicU,
            ["deviceId"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(26)),
            ["X-anticheattoken"] = AnticheatToken.Generate("eapi"),
        };
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["privacy"] = "10",
            ["type"] = "NORMAL",
            ["description"] = "",
            ["work"] = "",
        };
        var hex = crypto.EncryptEapi("/api/playlist/create",
            MergeHeader(payload, header));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{host}/eapi/playlist/create");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Cookie", $"MUSIC_U={musicU}");
        req.Headers.TryAddWithoutValidation("x-anticheattoken", AnticheatToken.Generate("eapi"));
        req.Content = new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("params", hex)]);
        using var resp = await http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static Task<(int Status, string Body)> EapiDeleteAsync(
        HttpClient http, CryptoService crypto, string musicU, string host, long id)
    {
        // 与创建同通道形态,仅换路径与业务参数
        return EapiCreateInnerAsync(http, crypto, musicU, host, "/api/playlist/delete",
            new Dictionary<string, object?> { ["id"] = id });
    }

    private static async Task<(int Status, string Body)> EapiCreateInnerAsync(
        HttpClient http, CryptoService crypto, string musicU, string host, string apiPath,
        Dictionary<string, object?> payload)
    {
        var header = new Dictionary<string, object?>
        {
            ["os"] = "pc",
            ["appver"] = "3.1.19.204510",
            ["requestId"] = 0,
            ["osver"] = "Microsoft-Windows-11-Home-China-build-22631-64bit",
            ["MUSIC_U"] = musicU,
            ["deviceId"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(26)),
            ["X-anticheattoken"] = AnticheatToken.Generate("eapi"),
        };
        var hex = crypto.EncryptEapi(apiPath,
            MergeHeader(payload, header));
        using var req = new HttpRequestMessage(HttpMethod.Post, host + "/eapi/" + apiPath["/api/".Length..]);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Cookie", $"MUSIC_U={musicU}");
        req.Headers.TryAddWithoutValidation("x-anticheattoken", AnticheatToken.Generate("eapi"));
        req.Content = new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("params", hex)]);
        using var resp = await http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>把内嵌 header 合进 payload(新 eapi 格式:header 是 payload 的一个键)。</summary>
    private static Dictionary<string, object?> MergeHeader(
        Dictionary<string, object?> payload, Dictionary<string, object?> header)
    {
        payload["header"] = header;
        return payload;
    }

    private static HttpClient BuildHttp(string musicU)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = false,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", $"MUSIC_U={musicU}");
        return http;
    }

    private static long TryPickId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("code", out var c) && c.GetInt32() == 200 &&
                   doc.RootElement.TryGetProperty("id", out var id)
                ? id.GetInt64() : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
