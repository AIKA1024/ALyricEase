using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

/// <summary>官方客户端代理登录端到端探针(--neproxy):
/// 启动 CopycatProxy 监听 → 用"不校验证书的客户端"(模拟网易云 PC 客户端行为)经代理
/// 发送真实 eapi 加密格式的伪造请求 → 断言回调提取出 MUSIC_U 并走完 Verifying → Ready。
/// 验证器注入假实现,不访问网络、不写真实 Cookie 存档。</summary>
public static class NetEaseProxyLoginProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!NetEaseProxyLoginService.IsSupported)
        {
            Console.WriteLine("[neproxy] 当前平台不支持,跳过");
            return 0;
        }

        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), new CookieStore());
        var service = new NetEaseProxyLoginService(api, _ => Task.FromResult("探针昵称"));
        var updates = new System.Collections.Concurrent.ConcurrentQueue<NetEaseProxyLoginUpdate>();
        var readySignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var port = service.Start(update =>
        {
            updates.Enqueue(update);
            Console.WriteLine($"[neproxy] update: stage={update.Stage} nick={update.Nickname ?? "-"} port={update.Port} err={update.Error ?? "-"}");
            if (update.Stage == NetEaseProxyLoginStage.Ready) readySignal.TrySetResult();
        });
        Console.WriteLine($"[neproxy] 代理已监听 127.0.0.1:{port}");
        if (port <= 0)
        {
            Console.WriteLine("[neproxy][FAIL] 端口分配失败");
            return 1;
        }

        try
        {
            await SendFakeEapiRequestAsync(port);
            var completed = await Task.WhenAny(readySignal.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != readySignal.Task)
            {
                Console.WriteLine("[neproxy][FAIL] 15 秒内未收到 Ready 回调");
                return 1;
            }
            var stages = updates.ToArray().Select(u => u.Stage.ToString()).ToArray();
            Console.WriteLine($"[neproxy] 状态序列: {string.Join(" → ", stages)}");
            var ok = stages.Contains(nameof(NetEaseProxyLoginStage.Verifying)) &&
                     stages.Contains(nameof(NetEaseProxyLoginStage.Ready));
            Console.WriteLine(ok ? "[neproxy][PASS] MITM 捕获 → 解密 → MUSIC_U 提取 → 验证全链路打通"
                                 : "[neproxy][FAIL] 状态序列不完整");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[neproxy][FAIL] 异常: {ex}");
            return 1;
        }
        finally
        {
            service.Stop();
            service.Dispose();
        }
    }

    /// <summary>模拟网易云客户端:信任任意证书(客户端不校验 TLS),代理指向本机,
    /// 发送标准 eapi 三段式加密请求(path-36cd479b6b5-text-36cd479b6b5-md5,
    /// header 为字符串化 JSON —— 与 Copycat Go 侧的解密/提取约定一致)。</summary>
    private static async Task SendFakeEapiRequestAsync(int port)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

        const string fakeMusicU = "probeFAKE0000000000000000000000000000000000000000000000000000probe";
        const string apiPath = "/api/album/detail";
        var headerJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["os"] = "pc",
            ["appver"] = "3.1.19.204510",
            ["requestId"] = "0",
            ["osver"] = "Microsoft-Windows-11-Home-China-build-22631-64bit",
            ["MUSIC_U"] = fakeMusicU,
            ["deviceId"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(26)),
        });
        var text = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["header"] = headerJson,
            ["id"] = "123",
        });
        var digest = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes($"nobody{apiPath}use{text}md5forencrypt"))).ToLowerInvariant();
        var plain = $"{apiPath}-36cd479b6b5-{text}-36cd479b6b5-{digest}";
        var hex = Convert.ToHexString(EapiAesEcbEncrypt(plain)).ToLowerInvariant();

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://interface.music.163.com/eapi/album/detail");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Cookie", $"MUSIC_U={fakeMusicU}");
        req.Content = new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("params", hex)]);
        try
        {
            using var resp = await http.SendAsync(req);
            Console.WriteLine($"[neproxy] 伪造 eapi 请求经代理发出,HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            // 上游真实服务器拒绝假凭证没关系,代理捕获发生在请求转发阶段
            Console.WriteLine($"[neproxy] 上游响应异常(预期内,不影响捕获): {ex.Message}");
        }
    }

    /// <summary>eapi 的 AES-128-ECB 加密(PKCS7,密钥 e82ckenh8dichen8)。</summary>
    private static byte[] EapiAesEcbEncrypt(string plaintext)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = System.Text.Encoding.UTF8.GetBytes("e82ckenh8dichen8");
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(
            System.Text.Encoding.UTF8.GetBytes(plaintext), 0, System.Text.Encoding.UTF8.GetByteCount(plaintext));
    }
}
