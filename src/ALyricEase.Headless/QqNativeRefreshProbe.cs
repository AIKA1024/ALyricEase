using System.Net;
using System.Reflection;
using System.Text.Json;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using ALyricEase.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace ALyricEase.Headless;

/// <summary>原生续签、提前续签及竞态回归，全部使用临时配置与模拟 HTTP。</summary>
internal static class QqNativeRefreshProbe
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string InitialKey = "probe-old-key";
    private const long Lifetime = 259200;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static ILogger<QQMusicApiClient>? _logger;

    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-qq-native-refresh-{Guid.NewGuid():N}");
        var logDirectory = Path.Combine(root, "logs");
        using var logFactory = new SerilogLoggerFactory(AppLogging.CreateFileLogger(logDirectory), dispose: true);
        _logger = logFactory.CreateLogger<QQMusicApiClient>();
        try
        {
            foreach (var (type, phone) in new[] { (2, true), (6, false), (2, false) })
            {
                var (api, cookie, handler) = Create(root, type, phone);
                handler.ExpiredKeys.Add(InitialKey);
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshCalls == 1 && cookie.QQCookieRaw!.Contains("probe-new-key-1"),
                    $"原生 {type}/{phone} 无 refresh_token 也能续签并保存新 key");
                var request = handler.RefreshRequest;
                var comm = request.GetProperty("comm");
                var param = request.GetProperty("req_0").GetProperty("param");
                Check(comm.GetProperty("ct").GetInt32() == 11
                      && comm.GetProperty("tmeLoginType").GetInt32() == type
                      && comm.GetProperty("QIMEI").GetString()!.Length > 0
                      && comm.GetProperty("sid").GetString() == "probe-session-sid",
                    "续签使用原生 Android comm、登录类型与设备会话");
                Check(param.GetProperty("refresh_token").GetString() == ""
                      && param.GetProperty("refresh_key").GetString() == "probe-refresh-key"
                      && param.GetProperty("loginMode").GetInt32() == 2
                      && param.GetProperty("expired_in").GetInt64() > Now
                      && param.GetProperty("musicid").GetInt64() == 123456789,
                    "原生刷新参数保留 refresh_key，缺失 OAuth 字段发送空值");
                Check(cookie.QQCookieRaw!.Contains("psrf_qqrefresh_key=probe-next-refresh-1")
                      && cookie.QQCookieRaw.Contains("alyric_qq_key_expires_in=259200"), "新刷新凭据及寿命同步轮换");
                handler.ExpiredKeys.Add("probe-new-key-1");
                SetField(api, "_credentialValidated", false);
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshCalls == 2, "新凭据以后失效时可以再次续签");
                var reloaded = new QQMusicApiClient(new CookieStore(Path.Combine(cookie.ConfigDirectory, "cookie.json")));
                var reloadHandler = new RefreshHandler();
                Attach(reloaded, new CookieStore(Path.Combine(cookie.ConfigDirectory, "cookie.json")), reloadHandler);
                await reloaded.EnsureCredentialValidAsync();
                Check(reloadHandler.RefreshCalls == 0, "重启后使用保存的新 key，不重复续签");
            }

            var (early, earlyCookie, earlyHandler) = Create(root, 2, true, Now - Lifetime + 14400);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => early.EnsureCredentialValidAsync()));
            Check(earlyHandler.RefreshCalls == 1, "最后 6 小时内提前续签，多个并发请求只刷新一次");
            await early.EnsureCredentialValidAsync();
            Check(earlyHandler.RefreshCalls == 1, "刷新后的有效期阻止重复刷新");

            foreach (var code in new[] { 104604, 104401, 20279 })
            {
                var (api, cookie, handler) = Create(root, 2, true, Now - Lifetime + 14400);
                handler.ErrorCode = code;
                var before = cookie.QQCookieRaw;
                await api.EnsureCredentialValidAsync();
                await api.EnsureCredentialValidAsync();
                Check(api.IsLoggedIn && cookie.QQCookieRaw == before && handler.RefreshCalls == 1,
                    $"提前续签失败 {code} 保留仍可用凭据，并延迟重试");
                handler.ErrorCode = 0;
                SetField(api, "_nativeCredentialRefreshAt", DateTimeOffset.MinValue);
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshCalls == 2 && cookie.QQCookieRaw != before, "重试窗口过后可续签成功");
            }

            var (network, networkCookie, networkHandler) = Create(root, 2, true, Now - Lifetime + 14400);
            networkHandler.NetworkFailure = true;
            var networkBefore = networkCookie.QQCookieRaw;
            await network.EnsureCredentialValidAsync();
            await network.EnsureCredentialValidAsync();
            Check(networkCookie.QQCookieRaw == networkBefore && networkHandler.RefreshCalls == 1,
                "提前续签网络失败不会清除账号或逐请求重试");

            var (fresh, _, freshHandler) = Create(root, 2, true);
            await fresh.EnsureCredentialValidAsync();
            Check(freshHandler.RefreshCalls == 0, "尚未接近到期时不刷新");
            _logger.LogInformation("logging-before-cached-check");
            await fresh.EnsureCredentialValidAsync();
            _logger.LogInformation("logging-after-cached-check");

            var (legacyQr, _, legacyQrHandler) = Create(root, 6, false);
            legacyQr.SetCookie("uin=123456789; qqmusic_key=probe-old-key; tmeLoginType=6; psrf_qqrefresh_key=probe-refresh-key");
            legacyQrHandler.ExpiredKeys.Add(InitialKey);
            await legacyQr.EnsureCredentialValidAsync();
            Check(legacyQrHandler.RefreshCalls == 1, "旧扫码 Cookie 没有来源标记时仍识别 loginType=6");

            var (minimal, _, minimalHandler) = Create(root, 6, false, refreshKey: "");
            minimalHandler.ExpiredKeys.Add(InitialKey);
            await ExpectApiAsync(() => minimal.EnsureCredentialValidAsync(), 104401);
            Check(minimalHandler.RefreshCalls == 0, "缺少 refresh_key 的旧凭据要求重新登录");

            var (denied, deniedCookie, deniedHandler) = Create(root, 2, true);
            deniedHandler.ExpiredKeys.Add(InitialKey);
            deniedHandler.ErrorCode = 104401;
            var deniedBefore = deniedCookie.QQCookieRaw;
            await ExpectApiAsync(() => denied.EnsureCredentialValidAsync(), 104401);
            await ExpectApiAsync(() => denied.EnsureCredentialValidAsync(), 104401);
            Check(deniedCookie.QQCookieRaw == deniedBefore && deniedHandler.RefreshCalls == 1,
                "已失效且续签被拒时透出错误，不覆盖凭据或重复占用设备名额");

            var (invalid, _, invalidHandler) = Create(root, 2, true);
            invalidHandler.ExpiredKeys.Add(InitialKey);
            invalidHandler.ExpiredKeys.Add("probe-new-key-1");
            await ExpectApiAsync(() => invalid.EnsureCredentialValidAsync(), 104401);
            Check(invalidHandler.RefreshCalls == 1, "新 key 校验失败不报告续签成功");

            var (switching, switchingCookie, switchingHandler) = Create(root, 2, true);
            switchingHandler.ExpiredKeys.Add(InitialKey);
            switchingHandler.PendingRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var switchingTask = switching.EnsureCredentialValidAsync();
            await switchingHandler.RefreshEntered.Task;
            switching.SetCookie("uin=987654321; qqmusic_key=probe-other-account");
            var otherAccount = switchingCookie.QQCookieRaw;
            switchingHandler.PendingRefresh.SetResult(switchingHandler.NewCredentialResponse());
            await ExpectApiAsync(() => switchingTask, -2);
            Check(switchingCookie.QQCookieRaw == otherAccount, "续签期间换号，旧响应不能覆盖新账号");

            var (cancelled, cancelledCookie, cancelledHandler) = Create(root, 2, true);
            cancelledHandler.ExpiredKeys.Add(InitialKey);
            cancelledHandler.PendingRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var cancellationBefore = cancelledCookie.QQCookieRaw;
            var cancelledTask = cancelled.EnsureCredentialValidAsync(cancellation.Token);
            await cancelledHandler.RefreshEntered.Task;
            cancellation.Cancel();
            try { await cancelledTask; throw new InvalidOperationException("取消请求没有传播"); }
            catch (OperationCanceledException) { }
            Check(cancelledCookie.QQCookieRaw == cancellationBefore, "取消续签不覆盖存档");

            var (unknownExpiry, unknownCookie, unknownHandler) = Create(root, 6, false);
            unknownHandler.ExpiredKeys.Add(InitialKey);
            unknownHandler.OmitCreateTime = true;
            await unknownExpiry.EnsureCredentialValidAsync();
            Check(unknownCookie.QQCookieRaw!.Contains("psrf_musickey_createtime=0"),
                "新 key 未提供创建时间时不沿用旧 key 的过期时间");

            var (web, webCookie, webHandler) = Create(root, 2, true);
            web.SetCookie("uin=123456789; qqmusic_key=probe-old-key; tmeLoginType=2; psrf_qqrefresh_key=probe-refresh-key");
            webHandler.ExpiredKeys.Add(InitialKey);
            await ExpectApiAsync(() => web.EnsureCredentialValidAsync(), 104401);
            Check(webHandler.RefreshCalls == 0, "网页 Cookie 不误走原生 refresh_key 通道");
            web.SetCookie("uin=123456789; qqmusic_key=probe-old-key; tmeLoginType=2; psrf_qqopenid=probe-openid; " +
                "psrf_qqaccess_token=probe-access; psrf_qqrefresh_token=probe-token; psrf_qqrefresh_key=probe-refresh-key");
            await web.EnsureCredentialValidAsync();
            Check(webHandler.RefreshRequest.GetProperty("comm").GetProperty("ct").GetInt32() == 24,
                "完整网页 Cookie 仍走原有 Web 续签通道");

            logFactory.Dispose();
            VerifyLogs(logDirectory);
            Console.WriteLine("[qq-native-refresh] PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[qq-native-refresh] FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            _logger = null;
            logFactory.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static (QQMusicApiClient Api, CookieStore Cookie, RefreshHandler Handler) Create(
        string root, int type, bool phone, long? createdAt = null, string refreshKey = "probe-refresh-key")
    {
        var cookie = new CookieStore(Path.Combine(root, Guid.NewGuid().ToString("N"), "cookie.json"));
        var api = new QQMusicApiClient(cookie, _logger);
        api.ApplyNativeCredential(new QQMusicQrCredential("123456789", InitialKey, type, "probe", "")
        {
            OpenId = phone ? "probe-openid" : "",
            AccessToken = phone ? "probe-access" : "",
            ExpiredAt = Now + 864000,
            RefreshKey = refreshKey,
            MusicKeyCreateTime = createdAt ?? Now,
            KeyExpiresIn = Lifetime,
        }, phone);
        SetField(api, "_credentialValidated", false);
        var handler = new RefreshHandler();
        Attach(api, cookie, handler);
        return (api, cookie, handler);
    }

    private static void Attach(QQMusicApiClient api, CookieStore cookie, RefreshHandler handler)
    {
        var qrField = typeof(QQMusicApiClient).GetField("_qrLogin", PrivateInstance)!;
        ((IDisposable)qrField.GetValue(api)!).Dispose();
        qrField.SetValue(api, new QQMusicQrLoginService(cookie, new HttpClient(handler)));
        var httpField = typeof(QQMusicApiClient).GetField("_http", PrivateInstance)!;
        ((IDisposable)httpField.GetValue(api)!).Dispose();
        httpField.SetValue(api, new HttpClient(handler));
    }

    private static void SetField(QQMusicApiClient api, string name, object value)
        => typeof(QQMusicApiClient).GetField(name, PrivateInstance)!.SetValue(api, value);

    private static async Task ExpectApiAsync(Func<Task> operation, int code)
    {
        try { await operation(); }
        catch (ApiException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException($"Expected API error {code}");
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        Console.WriteLine($"[qq-native-refresh] PASS {name}");
    }

    private static void VerifyLogs(string directory)
    {
        var text = string.Concat(Directory.GetFiles(directory, "*.log").Select(File.ReadAllText));
        Check(text.Contains("QQ native credentials applied") && text.Contains("HasRefreshToken=false", StringComparison.OrdinalIgnoreCase)
              && text.Contains("KeyExpiresInSeconds=259200"), "Serilog 记录登录字段存在性和有效期");
        Check(text.Contains("QQ native credential refresh scheduled") && text.Contains("RefreshAt=")
              && text.Contains("Reason=approaching-expiry") && text.Contains("Reason=expired")
              && text.Contains("QQ credential refresh response applied") && text.Contains("QQ credential refresh confirmed"),
            "Serilog 记录续签计划、触发原因和确认结果");
        Check(text.Contains("QQ credential refresh failed") && text.Contains("Code=104604")
              && text.Contains("QQ early credential refresh deferred") && text.Contains("RetryAt=")
              && text.Contains("ErrorType=HttpRequestException"), "Serilog 记录错误码、网络异常类型及下次重试时间");
        Check(text.Contains("QQ credential refresh cancelled") && text.Contains("response discarded because account changed"),
            "Serilog 记录取消及换号后丢弃响应");
        Check(!new[] { "probe-old-key", "probe-new-key", "probe-refresh-key", "probe-next-refresh", "probe-access", "probe-openid", "probe-network-secret" }
            .Any(secret => text.Contains(secret, StringComparison.Ordinal)), "Serilog 不包含 Cookie、刷新凭据或网络异常中的敏感文本");
        var cacheStart = text.IndexOf("logging-before-cached-check", StringComparison.Ordinal);
        var cacheEnd = text.IndexOf("logging-after-cached-check", cacheStart, StringComparison.Ordinal);
        Check(!text[cacheStart..cacheEnd].Contains("QQ credential"), "验证缓存命中时不逐请求写日志");
    }

    private sealed class RefreshHandler : HttpMessageHandler
    {
        public HashSet<string> ExpiredKeys { get; } = [];
        public int ErrorCode { get; set; }
        public bool NetworkFailure { get; set; }
        public bool OmitCreateTime { get; set; }
        public int RefreshCalls { get; private set; }
        public JsonElement RefreshRequest { get; private set; }
        public TaskCompletionSource<HttpResponseMessage>? PendingRefresh { get; set; }
        public TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpResponseMessage NewCredentialResponse()
        {
            var created = OmitCreateTime ? "" : $",\"musickeyCreateTime\":{Now}";
            return Response("{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"musicid\":123456789," +
                $"\"musickey\":\"probe-new-key-{RefreshCalls}\",\"refresh_key\":\"probe-next-refresh-{RefreshCalls}\"," +
                "\"keyExpiresIn\":259200" + created + "}}}");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield(); // 让并发调用真正交错，覆盖凭据单飞锁。
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host == "api.tencentmusic.com")
                return Response("""{"data":{"data":{"q16":"probe-qimei16","q36":"probe-qimei36"}}}""");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var req = doc.RootElement.GetProperty("req_0");
            switch (req.GetProperty("method").GetString())
            {
                case "GetSession":
                    return Response("""{"code":0,"req_0":{"code":0,"data":{"session":{"uid":"probe-session-uid","sid":"probe-session-sid"}}}}""");
                case "GetLoginUserInfo":
                    var key = doc.RootElement.GetProperty("comm").GetProperty("authst").GetString()!;
                    return ExpiredKeys.Contains(key)
                        ? Response("""{"code":0,"req_0":{"code":104401}}""")
                        : Response("""{"code":0,"req_0":{"code":0,"data":{}}}""");
                case "Login":
                    RefreshCalls++;
                    RefreshRequest = doc.RootElement.Clone();
                    RefreshEntered.TrySetResult();
                    if (PendingRefresh is { } pending) return await pending.Task.WaitAsync(ct);
                    if (NetworkFailure) throw new HttpRequestException("probe-network-secret https://example.invalid/?token=probe-network-token");
                    return ErrorCode == 0 ? NewCredentialResponse()
                        : Response($"{{\"code\":0,\"req_0\":{{\"code\":{ErrorCode}}}}}");
                default:
                    throw new InvalidOperationException("Unexpected request in native refresh probe");
            }
        }

        private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
