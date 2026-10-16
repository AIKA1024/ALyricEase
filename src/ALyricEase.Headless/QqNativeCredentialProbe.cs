using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ALyricEase.Services.Auth;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>原生登录刷新信息从响应到磁盘再到重启快照的回归；所有凭据均为测试值，不触网。</summary>
internal static class QqNativeCredentialProbe
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string FullCredential = """
        {"str_musicid":"123456789","musickey":"probe-key","loginType":6,
         "openid":"probe-openid","access_token":"probe-access","refresh_token":"probe-refresh-token",
         "refresh_key":"probe-refresh-key","unionid":"probe-unionid","encryptUin":"probe-encrypted-uin",
         "expired_at":"4102444800","musickeyCreateTime":1790000000,"keyExpiresIn":"259200"}
        """;

    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-qq-native-credential-{Guid.NewGuid():N}");
        try
        {
            var qrCookie = new CookieStore(Path.Combine(root, "qr", "cookie.json"));
            using var qr = new QQMusicQrLoginService(qrCookie, new HttpClient(new LoginHandler()));
            var credential = await ValidateLoginResponseAsync(qr, FullCredential, 6);
            Check(credential.RefreshKey == "probe-refresh-key" && credential.RefreshToken == "probe-refresh-token",
                "扫码登录校验后保留原始响应的刷新字段");
            Check(credential.ExpiredAt == 4102444800 && credential.MusicKeyCreateTime == 1790000000
                  && credential.KeyExpiresIn == 259200, "有效期支持数字、字符串和 64 位时间戳");
            var qrApi = new QQMusicApiClient(qrCookie);
            qrApi.ApplyNativeCredential(credential);
            VerifyStored(qrCookie, phone: false);
            var qrReloaded = new QQMusicApiClient(new CookieStore(Path.Combine(root, "qr", "cookie.json")));
            VerifySnapshot(Snapshot(qrReloaded), "probe-refresh-key", 1790000000, 259200);

            var phoneCookie = new CookieStore(Path.Combine(root, "phone", "cookie.json"));
            using var phone = new QQMusicQrLoginService(phoneCookie, new HttpClient(new LoginHandler()));
            var phoneApi = new QQMusicApiClient(phoneCookie);
            var qrField = typeof(QQMusicApiClient).GetField("_qrLogin", PrivateInstance)!;
            ((IDisposable)qrField.GetValue(phoneApi)!).Dispose();
            qrField.SetValue(phoneApi, phone);
            var phoneCredential = await phoneApi.LoginWithPhoneCodeAsync("13800000000", "86", "123456");
            Check(phoneCredential.RefreshKey == "probe-refresh-key", "验证码登录完整流程传递刷新字段");
            VerifyStored(phoneCookie, phone: true);
            var phoneReloaded = new QQMusicApiClient(new CookieStore(Path.Combine(root, "phone", "cookie.json")));
            VerifySnapshot(Snapshot(phoneReloaded), "probe-refresh-key", 1790000000, 259200);

            using (var refreshed = JsonDocument.Parse("""
                {"musicid":123456789,"musickey":"probe-new-key","refresh_key":"probe-new-refresh-key",
                 "musickeyCreateTime":"1790100000","keyExpiresIn":172800}
                """))
            {
                typeof(QQMusicApiClient).GetMethod("ApplyRefreshedCredential", PrivateInstance)!
                    .Invoke(qrReloaded, [refreshed.RootElement, Snapshot(qrReloaded)]);
            }
            var rotated = new QQMusicApiClient(new CookieStore(Path.Combine(root, "qr", "cookie.json")));
            VerifySnapshot(Snapshot(rotated), "probe-new-refresh-key", 1790100000, 172800);

            // 换成缺少可选字段的账号时，不能把前一个账号的刷新信息带过去。
            var minimal = await ValidateLoginResponseAsync(qr,
                """{"musicid":987654321,"musickey":"probe-minimal-key"}""", 6);
            qrApi.ApplyNativeCredential(minimal);
            var minimalStore = new CookieStore(Path.Combine(root, "qr", "cookie.json"));
            Check(minimalStore.QQCookieRaw is { } raw && !raw.Contains("psrf_qqrefresh")
                  && !raw.Contains("alyric_qq_key_expires_in"), "无刷新字段的登录不伪造信息，也不串用旧账号字段");
            var minimalApi = new QQMusicApiClient(minimalStore);
            Check(minimalApi.IsLoggedIn && Value<string>(Snapshot(minimalApi), "RefreshKey") == "",
                "最小原生凭据仍可恢复登录");

            var legacyStore = new CookieStore(Path.Combine(root, "legacy", "cookie.json"));
            new QQMusicApiClient(legacyStore).SetCookie("uin=123456789; qqmusic_key=probe-legacy-key; tmeLoginType=6");
            var legacy = new QQMusicApiClient(new CookieStore(Path.Combine(root, "legacy", "cookie.json")));
            Check(legacy.IsLoggedIn && Value<long>(Snapshot(legacy), "KeyExpiresIn") == 0,
                "兼容旧版缺少刷新信息的 Cookie 文件");
            Console.WriteLine("[qq-native-credential] PASS");
            return 0;
        }
        catch (Exception ex)
        {
            while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
            // 不输出凭据对象或实际字段值。
            Console.Error.WriteLine($"[qq-native-credential] FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<QQMusicQrCredential> ValidateLoginResponseAsync(
        QQMusicQrLoginService service, string json, int loginType)
    {
        object credential;
        using (var doc = JsonDocument.Parse(json))
            credential = typeof(QQMusicQrLoginService).GetMethod("CredentialFrom", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [doc.RootElement, loginType])!;
        return await (Task<QQMusicQrCredential>)typeof(QQMusicQrLoginService)
            .GetMethod("ValidateCredentialAsync", PrivateInstance)!.Invoke(service, [credential, CancellationToken.None])!;
    }

    private static void VerifyStored(CookieStore original, bool phone)
    {
        var header = original.QQCookieRaw ?? "";
        var fields = header.Split(';').Select(p => p.Trim().Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);
        (string Name, string Expected)[] expected =
        [
            ("psrf_qqopenid", "probe-openid"), ("psrf_qqaccess_token", "probe-access"),
            ("psrf_qqrefresh_token", "probe-refresh-token"), ("psrf_qqrefresh_key", "probe-refresh-key"),
            ("psrf_qqunionid", "probe-unionid"), ("euin", "probe-encrypted-uin"),
            ("psrf_access_token_expiresAt", "4102444800"), ("psrf_musickey_createtime", "1790000000"),
            ("alyric_qq_key_expires_in", "259200"),
        ];
        foreach (var field in expected)
            Check(fields.TryGetValue(field.Name, out var value) && value == field.Expected,
                $"{(phone ? "phone" : "qr")} 保存 {field.Name}");
        Check(fields.TryGetValue("alyric_native_login", out var source) && source == (phone ? "phone" : "qr"),
            "保留原生登录来源标记");
    }

    private static object Snapshot(QQMusicApiClient api)
        => typeof(QQMusicApiClient).GetMethod("ReadCredentialSnapshot", PrivateInstance)!.Invoke(api, null)!;

    private static T Value<T>(object snapshot, string name)
        => (T)snapshot.GetType().GetProperty(name)!.GetValue(snapshot)!;

    private static void VerifySnapshot(object snapshot, string key, long createdAt, long lifetime)
    {
        Check(Value<string>(snapshot, "OpenId") == "probe-openid"
              && Value<string>(snapshot, "AccessToken") == "probe-access"
              && Value<string>(snapshot, "RefreshToken") == "probe-refresh-token"
              && Value<string>(snapshot, "RefreshKey") == key
              && Value<string>(snapshot, "UnionId") == "probe-unionid"
              && Value<string>(snapshot, "EncryptUin") == "probe-encrypted-uin"
              && Value<long>(snapshot, "ExpiredAt") == 4102444800
              && Value<long>(snapshot, "MusicKeyCreateTime") == createdAt
              && Value<long>(snapshot, "KeyExpiresIn") == lifetime, "重启后刷新快照完整恢复凭据与有效期");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        Console.WriteLine($"[qq-native-credential] PASS {name}");
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string json;
            if (request.RequestUri!.Host == "api.tencentmusic.com")
                json = """{"data":{"data":{"q16":"probe-qimei16","q36":"probe-qimei36"}}}""";
            else
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var method = doc.RootElement.GetProperty("req_0").GetProperty("method").GetString();
                var data = method switch
                {
                    "GetSession" => """{"session":{"uid":"probe-session-uid","sid":"probe-session-sid"}}""",
                    "Login" => FullCredential.Replace("\"loginType\":6", "\"loginType\":2"),
                    "GetLoginUserInfo" => """{"info":{"nick":"probe","logo":""}}""",
                    _ => throw new InvalidOperationException("Unexpected request in native credential probe"),
                };
                json = "{\"code\":0,\"req_0\":{\"code\":0,\"data\":" + data + "}}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
