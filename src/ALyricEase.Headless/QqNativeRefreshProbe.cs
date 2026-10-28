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
                Check(handler.RefreshKeyCalls == 1 && handler.RefreshCalls == 0
                      && cookie.QQCookieRaw!.Contains("probe-new-key-1"),
                    $"原生 {type}/{phone} 无 refresh_token 也走 QQRefreshKey 续签并保存新 key");
                var request = handler.RefreshKeyRequest;
                var comm = request.GetProperty("comm");
                var param = request.GetProperty("req_0").GetProperty("param");
                Check(comm.GetProperty("ct").GetInt32() == 11
                      && comm.GetProperty("tmeLoginType").GetInt32() == type
                      && comm.GetProperty("QIMEI").GetString()!.Length > 0
                      && comm.GetProperty("sid").GetString() == "probe-session-sid",
                    "续签使用原生 Android comm、登录类型与设备会话");
                Check(request.GetProperty("req_0").GetProperty("method").GetString() == "QQRefreshKey"
                      && param.GetProperty("musickey").GetString() == InitialKey
                      && param.GetProperty("refresh_key").GetString() == "probe-refresh-key"
                      && param.GetProperty("onlyNeedAccessToken").GetInt32() == 0
                      && param.GetProperty("forceRefreshToken").GetInt32() == 0
                      && !param.TryGetProperty("loginMode", out _)
                      && param.GetProperty("expired_in").GetInt64() > Now
                      && param.GetProperty("musicid").GetInt64() == 123456789,
                    "QQRefreshKey 不发 loginMode，带 musickey/refresh_key 与绝对 expired_in");
                Check(cookie.QQCookieRaw!.Contains("psrf_qqrefresh_key=probe-next-refresh-1")
                      && cookie.QQCookieRaw.Contains("alyric_qq_key_expires_in=259200"), "新刷新凭据及寿命同步轮换");
                handler.ExpiredKeys.Add("probe-new-key-1");
                SetField(api, "_credentialValidated", false);
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshKeyCalls == 2 && handler.RefreshCalls == 0, "新凭据以后失效时可以再次续签");
                var reloaded = new QQMusicApiClient(new CookieStore(Path.Combine(cookie.ConfigDirectory, "cookie.json")));
                var reloadHandler = new RefreshHandler();
                Attach(reloaded, new CookieStore(Path.Combine(cookie.ConfigDirectory, "cookie.json")), reloadHandler);
                await reloaded.EnsureCredentialValidAsync();
                Check(reloadHandler.RefreshCalls == 0 && reloadHandler.RefreshKeyCalls == 0, "重启后使用保存的新 key，不重复续签");
            }

            // QQRefreshKey 被拒：同一把 key 只试一次，随后回落 Login（loginMode=2，会消耗登录链）；
            // Login 换出新 key 后重新给该通道机会。
            {
                var (api, cookie, handler) = Create(root, 2, true);
                handler.ExpiredKeys.Add(InitialKey);
                handler.RejectRefreshKey = true;
                await api.EnsureCredentialValidAsync();
                var loginParam = handler.RefreshRequest.GetProperty("req_0").GetProperty("param");
                Check(handler.RefreshKeyCalls == 1 && handler.RefreshCalls == 1
                      && loginParam.GetProperty("loginMode").GetInt32() == 2
                      && loginParam.GetProperty("refresh_key").GetString() == "probe-refresh-key"
                      && loginParam.GetProperty("refresh_token").GetString() == ""
                      && cookie.QQCookieRaw!.Contains("probe-new-key-1"),
                    "QQRefreshKey 被拒时回落 Login 并保存新 key");
                handler.ExpiredKeys.Add("probe-new-key-1");
                SetField(api, "_credentialValidated", false);
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshKeyCalls == 2 && handler.RefreshCalls == 2,
                    "换到新 key 后重新尝试 QQRefreshKey 通道");
            }

            // 1000 是通用失败码：先隔一次重试，不直接判死也不立刻回落 Login。
            {
                var (api, cookie, handler) = Create(root, 2, true);
                SetField(api, "_nativeRefreshKeyRetryDelay", TimeSpan.Zero);
                handler.ExpiredKeys.Add(InitialKey);
                handler.ErrorCode = 1000;
                handler.FailRefreshKeyTimes = 1;
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshKeyCalls == 2 && handler.RefreshCalls == 0
                      && cookie.QQCookieRaw!.Contains("probe-new-key-2"),
                    "QQRefreshKey 返回 1000 先重试一次，成功就不回落 Login");
            }

            // QQRefreshKey 通道：PC 客户端登录成功后每 4 小时走这条，在同一登录会话内换 key。
            // 它不发 loginMode，所以不像 Login 那样每次续签都新建会话（登录链走满就是 1000）。
            {
                var refreshKeyCookie = new CookieStore(Path.Combine(root, Guid.NewGuid().ToString("N"), "cookie.json"));
                var refreshKeyHandler = new RefreshHandler();
                using var refreshKeyService = new QQMusicQrLoginService(refreshKeyCookie, new HttpClient(refreshKeyHandler), _logger);
                var refreshKeyCredential = new QQMusicQrCredential("123456789", InitialKey, 2, "probe", "")
                {
                    OpenId = "probe-openid",
                    AccessToken = "probe-access",
                    ExpiredAt = Now + 864000,
                    RefreshKey = "probe-refresh-key",
                    MusicKeyCreateTime = Now,
                    KeyExpiresIn = Lifetime,
                };
                var refreshKeyData = await refreshKeyService
                    .RefreshStoredCredentialWithRefreshKeyAsync(refreshKeyCredential, CancellationToken.None);
                var refreshKeyRequest = refreshKeyHandler.RefreshKeyRequest.GetProperty("req_0");
                Check(refreshKeyHandler.RefreshKeyCalls == 1
                      && refreshKeyHandler.RefreshCalls == 0
                      && refreshKeyRequest.GetProperty("module").GetString() == "music.login.LoginServer"
                      && refreshKeyRequest.GetProperty("method").GetString() == "QQRefreshKey",
                    "QQRefreshKey 独立成方法，不占用 Login 通道");
                var refreshKeyParam = refreshKeyRequest.GetProperty("param");
                Check(refreshKeyParam.GetProperty("refresh_key").GetString() == "probe-refresh-key"
                      && refreshKeyParam.GetProperty("musickey").GetString() == InitialKey
                      && refreshKeyParam.GetProperty("musicid").GetInt64() == 123456789
                      && refreshKeyParam.GetProperty("openid").GetString() == "probe-openid"
                      && !refreshKeyParam.TryGetProperty("loginMode", out _),
                    "QQRefreshKey 带 refresh_key 与 musickey，且不发 loginMode");
                Check(refreshKeyData.GetProperty("musickey").GetString() == "probe-new-key-1"
                      && refreshKeyData.GetProperty("refresh_key").GetString() == "probe-next-refresh-1",
                    "QQRefreshKey 响应可解析出轮换后的 key");
                refreshKeyHandler.ErrorCode = 1000;
                refreshKeyHandler.FailureMethod = "QQRefreshKey";
                await ExpectApiAsync(() => refreshKeyService
                    .RefreshStoredCredentialWithRefreshKeyAsync(refreshKeyCredential, CancellationToken.None), 1000);
                Check(refreshKeyHandler.RefreshCalls == 0, "QQRefreshKey 失败时不回落到 Login");
            }

            var (early, earlyCookie, earlyHandler) = Create(root, 2, true, Now - 14401);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => early.EnsureCredentialValidAsync()));
            Check(earlyHandler.RefreshKeyCalls == 1 && earlyHandler.RefreshCalls == 0,
                "key 创建满 4 小时即续签，多个并发请求只刷新一次");
            await early.EnsureCredentialValidAsync();
            Check(earlyHandler.RefreshKeyCalls == 1, "刷新后的新排期阻止重复刷新");

            foreach (var code in new[] { 104604, 104401, 20279 })
            {
                var (api, cookie, handler) = Create(root, 2, true, Now - 14401);
                handler.ErrorCode = code;
                handler.FailAllChannels = true;
                var before = cookie.QQCookieRaw;
                await api.EnsureCredentialValidAsync();
                await api.EnsureCredentialValidAsync();
                Check(api.IsLoggedIn && cookie.QQCookieRaw == before
                      && handler.RefreshKeyCalls == 1 && handler.RefreshCalls == 0,
                    $"例行续签失败 {code} 不消耗登录链，并延迟重试");
                handler.ErrorCode = 0;
                handler.FailAllChannels = false;
                SetField(api, "_nativeCredentialRefreshAt", DateTimeOffset.MinValue);
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshKeyCalls == 2 && cookie.QQCookieRaw != before, "重试窗口过后可续签成功");
            }

            // 40000 = 通道级拒绝：不打 Login 兜底、判定写进 Cookie、节奏改 24 小时；失效后仍可 Login。
            {
                var (api, cookie, handler) = Create(root, 2, true, Now - 14401);
                handler.ErrorCode = QQMusicApiClient.RefreshKeyChannelRejectedCode;
                handler.FailRefreshKeyTimes = 1;
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshKeyCalls == 1 && handler.RefreshCalls == 0,
                    "QQRefreshKey 返回 40000 时例行续签不拿 Login 兜底");
                Check(cookie.QQCookieRaw!.Contains("alyric_qq_refreshkey_unsupported=1"),
                    "通道不可用判定写进 Cookie（重启后不再白试）");
                var refreshAt = (DateTimeOffset)typeof(QQMusicApiClient)
                    .GetField("_nativeCredentialRefreshAt", PrivateInstance)!.GetValue(api)!;
                Check((refreshAt - DateTimeOffset.UtcNow).TotalHours is > 19 and < 25,
                    "已知通道不可用时改成 24 小时节奏");
                handler.ErrorCode = 0;
                handler.FailRefreshKeyTimes = 0;
                handler.ExpiredKeys.Add(InitialKey);
                SetField(api, "_credentialValidated", false);
                await api.EnsureCredentialValidAsync();
                Check(handler.RefreshCalls == 1 && cookie.QQCookieRaw.Contains("probe-new-key-1"),
                    "通道不可用时凭证失效仍用 Login 续签");
            }

            var (network, networkCookie, networkHandler) = Create(root, 2, true, Now - 14401);
            networkHandler.NetworkFailure = true;
            var networkBefore = networkCookie.QQCookieRaw;
            await network.EnsureCredentialValidAsync();
            await network.EnsureCredentialValidAsync();
            Check(networkCookie.QQCookieRaw == networkBefore
                  && networkHandler.RefreshKeyCalls == 1 && networkHandler.RefreshCalls == 0,
                "续签网络失败不会清除账号、不换通道也不逐请求重试");

            var (fresh, _, freshHandler) = Create(root, 2, true);
            await fresh.EnsureCredentialValidAsync();
            Check(freshHandler.RefreshKeyCalls == 0 && freshHandler.RefreshCalls == 0, "刚签发的 key 不刷新");
            var (young, _, youngHandler) = Create(root, 2, true, Now - 3600);
            await young.EnsureCredentialValidAsync();
            Check(youngHandler.RefreshKeyCalls == 0, "key 才 1 小时不刷新（4 小时节奏）");
            _logger.LogInformation("logging-before-cached-check");
            await fresh.EnsureCredentialValidAsync();
            _logger.LogInformation("logging-after-cached-check");

            var (legacyQr, _, legacyQrHandler) = Create(root, 6, false);
            legacyQr.SetCookie("uin=123456789; qqmusic_key=probe-old-key; tmeLoginType=6; psrf_qqrefresh_key=probe-refresh-key");
            legacyQrHandler.ExpiredKeys.Add(InitialKey);
            await legacyQr.EnsureCredentialValidAsync();
            Check(legacyQrHandler.RefreshKeyCalls == 1, "旧扫码 Cookie 没有来源标记时仍识别 loginType=6");

            var (minimal, _, minimalHandler) = Create(root, 6, false, refreshKey: "");
            minimalHandler.ExpiredKeys.Add(InitialKey);
            await ExpectApiAsync(() => minimal.EnsureCredentialValidAsync(), 104401);
            Check(minimalHandler.RefreshCalls == 0, "缺少 refresh_key 的旧凭据要求重新登录");

            var (denied, deniedCookie, deniedHandler) = Create(root, 2, true);
            deniedHandler.ExpiredKeys.Add(InitialKey);
            deniedHandler.ErrorCode = 104401;
            deniedHandler.FailAllChannels = true;
            var deniedBefore = deniedCookie.QQCookieRaw;
            await ExpectApiAsync(() => denied.EnsureCredentialValidAsync(), 104401);
            await ExpectApiAsync(() => denied.EnsureCredentialValidAsync(), 104401);
            Check(deniedCookie.QQCookieRaw == deniedBefore
                  && deniedHandler.RefreshKeyCalls == 1 && deniedHandler.RefreshCalls == 1,
                "已失效且续签被拒时透出错误，不覆盖凭据或重复占用设备名额");

            var (invalid, _, invalidHandler) = Create(root, 2, true);
            invalidHandler.ExpiredKeys.Add(InitialKey);
            invalidHandler.ExpiredKeys.Add("probe-new-key-1");
            await ExpectApiAsync(() => invalid.EnsureCredentialValidAsync(), 104401);
            Check(invalidHandler.RefreshKeyCalls == 1, "新 key 校验失败不报告续签成功");

            var (switching, switchingCookie, switchingHandler) = Create(root, 2, true);
            switchingHandler.ExpiredKeys.Add(InitialKey);
            switchingHandler.RejectRefreshKey = true;
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
            cancelledHandler.RejectRefreshKey = true;
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

            foreach (var method in new[] { "GetQimei", "GetSession", "Login" })
            {
                var (api, _, handler) = Create(root, 2, true);
                handler.ExpiredKeys.Add(InitialKey);
                handler.ErrorCode = 1000;
                handler.FailureMethod = method;
                handler.RejectRefreshKey = true; // 让 1000 落在 Login 上，验证"定位到具体接口"
                await ExpectApiAsync(() => api.EnsureCredentialValidAsync(), 1000);
                Check(handler.RefreshCalls == (method == "Login" ? 1 : 0), $"模拟 {method} 返回 1000 时保留原有失败行为");
            }
            var (gateway, _, gatewayHandler) = Create(root, 2, true);
            gatewayHandler.ExpiredKeys.Add(InitialKey);
            gatewayHandler.ErrorCode = 1000;
            gatewayHandler.GlobalError = true;
            gatewayHandler.RejectRefreshKey = true;
            await ExpectApiAsync(() => gateway.EnsureCredentialValidAsync(), 1000);

            foreach (var authorization in new[] { "changed", "unchanged", "empty" })
            {
                var (api, cookie, handler) = Create(root, 2, true);
                handler.ExpiredKeys.Add(InitialKey);
                handler.AuthorizationResponse = authorization;
                await api.EnsureCredentialValidAsync();
                Check(cookie.QQCookieRaw!.Contains(authorization == "changed" ? "probe-rotated-access" : "probe-access"),
                    $"授权响应 {authorization} 的诊断不改变原有字段合并行为");
            }

            Check(QQMusicApiClient.RequiresFreshLogin(1000) && !QQMusicApiClient.ShouldDiscardCredential(1000)
                  && QQMusicApiClient.ShouldDiscardCredential(104401)
                  && QQMusicApiClient.ShouldDiscardCredential(20279),
                "1000 只提示重登、不丢弃本地凭据（refresh_key 留给冷却重试）");

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
        qrField.SetValue(api, new QQMusicQrLoginService(cookie, new HttpClient(handler), _logger));
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
        Check(text.Contains("QQ native refresh key channel accepted")
              && text.Contains("QQ native refresh key channel unavailable. Code=104400")
              && text.Contains("QQ native refresh key channel returned 1000; retrying once")
              && text.Contains("QQ native refresh key channel unsupported for this account. Code=40000"),
            "Serilog 记录 QQRefreshKey 通道启用、失败码、1000 重试与通道不可用判定");
        Check(text.Contains("QQ credential refresh failed") && text.Contains("Code=104604")
              && text.Contains("QQ early credential refresh deferred") && text.Contains("RetryAt=")
              && text.Contains("ErrorType=HttpRequestException"), "Serilog 记录错误码、网络异常类型及下次重试时间");
        Check(text.Contains("QQ credential refresh cancelled") && text.Contains("response discarded because account changed"),
            "Serilog 记录取消及换号后丢弃响应");
        var lines = text.Split('\n');
        foreach (var method in new[] { "GetQimei", "GetSession", "Login" })
            Check(lines.Any(line => line.Contains("QQ native protocol") && line.Contains($"Method={method}")
                                   && line.Contains("Code=1000")), $"Serilog 将 1000 定位到 {method}");
        Check(lines.Any(line => line.Contains("Method=Login") && line.Contains("GlobalCode=1000 ItemCode=0"))
              && lines.Any(line => line.Contains("Method=Login") && line.Contains("GlobalCode=0 ItemCode=1000"))
              && lines.Any(line => line.Contains("Method=GetLoginUserInfo") && line.Contains("HttpStatusCode=200")),
            "Serilog 区分顶层码、子请求码和 HTTP 状态");
        foreach (var state in new[] { "changed", "unchanged", "missing", "empty" })
            Check(text.Contains($"AccessTokenState={state}"), $"Serilog 记录授权字段状态 {state}");
        Check(text.Contains("QQ credential refresh authorization snapshot") && text.Contains("AccessExpiresAt=")
              && text.Contains("QQ credential refresh expiry changes") && text.Contains("PreviousAccessExpiresAt=")
              && text.Contains("ReturnedAccessExpiresAt=null") && text.Contains("EffectiveAccessExpiresAt=")
              && text.Contains("AccessExpiryChanged=true", StringComparison.OrdinalIgnoreCase)
              && text.Contains("AccessExpiryChanged=false", StringComparison.OrdinalIgnoreCase),
            "Serilog 记录续签前、响应中及合并后的授权有效期");
        Check(!new[] { "probe-old-key", "probe-new-key", "probe-refresh-key", "probe-next-refresh", "probe-access", "probe-openid", "probe-network-secret",
                       "probe-rotated-access", "probe-rotated-token", "probe-qimei16", "probe-qimei36", "probe-session-uid", "probe-session-sid" }
            .Any(secret => text.Contains(secret, StringComparison.Ordinal)), "Serilog 不包含 Cookie、刷新凭据或网络异常中的敏感文本");
        var cacheStart = text.IndexOf("logging-before-cached-check", StringComparison.Ordinal);
        var cacheEnd = text.IndexOf("logging-after-cached-check", cacheStart, StringComparison.Ordinal);
        Check(!text[cacheStart..cacheEnd].Contains("QQ credential"), "验证缓存命中时不逐请求写日志");
    }

    private sealed class RefreshHandler : HttpMessageHandler
    {
        public HashSet<string> ExpiredKeys { get; } = [];
        public int ErrorCode { get; set; }
        public string FailureMethod { get; set; } = "Login";
        public bool GlobalError { get; set; }
        public string AuthorizationResponse { get; set; } = "missing";
        public bool NetworkFailure { get; set; }
        public bool OmitCreateTime { get; set; }
        /// <summary>QQRefreshKey 一律返回 104400（模拟"这条通道不认移动端 refresh_key"）。</summary>
        public bool RejectRefreshKey { get; set; }
        /// <summary>两条通道都按 ErrorCode 失败。</summary>
        public bool FailAllChannels { get; set; }
        /// <summary>前 N 次 QQRefreshKey 返回 ErrorCode（1000 重试用）。</summary>
        public int FailRefreshKeyTimes { get; set; }
        public int RefreshCalls { get; private set; }
        public int RefreshKeyCalls { get; private set; }
        public JsonElement RefreshRequest { get; private set; }
        public JsonElement RefreshKeyRequest { get; private set; }
        public JsonElement LastParam { get; private set; }
        public TaskCompletionSource<HttpResponseMessage>? PendingRefresh { get; set; }
        public TaskCompletionSource RefreshEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpResponseMessage NewCredentialResponse() => NewCredentialResponse(RefreshCalls);

        public HttpResponseMessage NewCredentialResponse(int sequence)
        {
            var created = OmitCreateTime ? "" : $",\"musickeyCreateTime\":{Now}";
            var authorization = AuthorizationResponse switch
            {
                "changed" => $",\"access_token\":\"probe-rotated-access\",\"refresh_token\":\"probe-rotated-token\",\"expired_at\":{Now + 1728000}",
                "unchanged" => ",\"access_token\":\"probe-access\",\"expired_at\":" +
                    LastParam.GetProperty("expired_in").GetInt64(),
                "empty" => ",\"access_token\":\"\",\"expired_at\":0",
                _ => "",
            };
            return Response("{\"code\":0,\"req_0\":{\"code\":0,\"data\":{\"musicid\":123456789," +
                $"\"musickey\":\"probe-new-key-{sequence}\",\"refresh_key\":\"probe-next-refresh-{sequence}\"," +
                "\"keyExpiresIn\":259200" + created + authorization + "}}}");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield(); // 让并发调用真正交错，覆盖凭据单飞锁。
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host == "api.tencentmusic.com")
            {
                if (FailureMethod == "GetQimei" && ErrorCode != 0)
                    return Response($"{{\"code\":0,\"data\":{{\"code\":{ErrorCode},\"data\":{{}}}}}}");
                return Response("""{"data":{"data":{"q16":"probe-qimei16","q36":"probe-qimei36"}}}""");
            }
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var req = doc.RootElement.GetProperty("req_0");
            var param = req.GetProperty("param");
            switch (req.GetProperty("method").GetString())
            {
                case "GetSession":
                    if (FailureMethod == "GetSession" && ErrorCode != 0)
                        return Response($"{{\"code\":0,\"req_0\":{{\"code\":{ErrorCode}}}}}");
                    return Response("""{"code":0,"req_0":{"code":0,"data":{"session":{"uid":"probe-session-uid","sid":"probe-session-sid"}}}}""");
                case "GetLoginUserInfo":
                    var key = doc.RootElement.GetProperty("comm").GetProperty("authst").GetString()!;
                    return ExpiredKeys.Contains(key)
                        ? Response("""{"code":0,"req_0":{"code":104401}}""")
                        : Response("""{"code":0,"req_0":{"code":0,"data":{}}}""");
                case "Login":
                    RefreshCalls++;
                    RefreshRequest = doc.RootElement.Clone();
                    LastParam = param.Clone();
                    RefreshEntered.TrySetResult();
                    if (PendingRefresh is { } pending) return await pending.Task.WaitAsync(ct);
                    if (NetworkFailure) throw new HttpRequestException("probe-network-secret https://example.invalid/?token=probe-network-token");
                    if (ErrorCode == 0 || !(FailureMethod == "Login" || FailAllChannels)) return NewCredentialResponse();
                    return FailureResponse();
                case "QQRefreshKey":
                    RefreshKeyCalls++;
                    RefreshKeyRequest = doc.RootElement.Clone();
                    LastParam = param.Clone();
                    if (NetworkFailure) throw new HttpRequestException("probe-network-secret https://example.invalid/?token=probe-network-token");
                    if (RejectRefreshKey) return ItemFailure(104400);
                    if (FailRefreshKeyTimes > 0)
                    {
                        FailRefreshKeyTimes--;
                        return FailureResponse();
                    }
                    if (ErrorCode == 0 || !(FailureMethod == "QQRefreshKey" || FailAllChannels))
                        return NewCredentialResponse(RefreshKeyCalls);
                    return FailureResponse();
                default:
                    throw new InvalidOperationException("Unexpected request in native refresh probe");
            }
        }

        private HttpResponseMessage FailureResponse() => GlobalError
            ? Response($"{{\"code\":{ErrorCode},\"req_0\":{{\"code\":0}}}}")
            : ItemFailure(ErrorCode);

        private static HttpResponseMessage ItemFailure(int code)
            => Response($"{{\"code\":0,\"req_0\":{{\"code\":{code}}}}}");

        private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
