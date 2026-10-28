using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>真机 QQRefreshKey 探针（--qqrefreshkey）：用本机真实凭据回答两个问题——
/// ① 移动端签发的 refresh_key 是否被 `music.login.LoginServer/QQRefreshKey` 接受；
/// ② `expired_in` 该发绝对时间戳还是相对秒数（先打生产值，失败再打对照值）。
/// 先做只读校验；凭据已失效就不发续签，避免白白消耗一次登录链。</summary>
public static class QqRefreshKeyProbe
{
    public static async Task<int> RunAsync(string? mode = null)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cookie = new CookieStore();
        var raw = cookie.QQCookieRaw ?? "";
        if (raw.Length == 0)
        {
            Console.WriteLine("[qqrefreshkey] 本机没有 QQ Cookie，请先在应用里登录");
            return 1;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var musicId = Val(raw, "uin")?.TrimStart('o') ?? "";
        var musicKey = Val(raw, "qm_keyst") ?? Val(raw, "qqmusic_key") ?? "";
        var loginType = int.TryParse(Val(raw, "tmeLoginType"), out var parsedType) ? parsedType : 0;
        var createTime = long.TryParse(Val(raw, "psrf_musickey_createtime"), out var parsedCreate) ? parsedCreate : 0;
        var expiresIn = long.TryParse(Val(raw, "alyric_qq_key_expires_in"), out var parsedLifetime) ? parsedLifetime : 0;
        var accessExpiresAt = long.TryParse(Val(raw, "psrf_access_token_expiresAt"), out var parsedAccess) ? parsedAccess : 0;
        Console.WriteLine(
            $"[qqrefreshkey] loginType={loginType} keyAge={(createTime > 0 ? Hours(now - createTime) : "缺")} " +
            $"lifetime={(expiresIn > 0 ? Hours(expiresIn) : "缺")} " +
            $"refreshKey={(Val(raw, "psrf_qqrefresh_key") is { Length: > 0 } ? "有" : "缺")} " +
            $"refreshToken={(Val(raw, "psrf_qqrefresh_token") is { Length: > 0 } ? "有" : "缺")} " +
            $"accessTokenExpiresAt={(accessExpiresAt > 0 ? accessExpiresAt.ToString() : "缺")}");

        using var service = new QQMusicQrLoginService(cookie);
        var valid = await service.ValidateStoredCredentialAsync(musicId, musicKey, loginType, default);
        Console.WriteLine($"[qqrefreshkey] 只读校验: {(valid ? "有效（可以测续签）" : "已失效")}");
        if (!valid)
        {
            Console.WriteLine("[qqrefreshkey] 凭据已失效时续签测不出结论。先在应用里重新登录，再跑本探针。");
            return 1;
        }

        var credential = new QQMusicQrCredential(musicId, musicKey, loginType, "", "")
        {
            OpenId = Val(raw, "psrf_qqopenid") ?? "",
            AccessToken = Val(raw, "psrf_qqaccess_token") ?? "",
            RefreshToken = Val(raw, "psrf_qqrefresh_token") ?? "",
            RefreshKey = Val(raw, "psrf_qqrefresh_key") ?? "",
            UnionId = Val(raw, "psrf_qqunionid") ?? "",
            ExpiredAt = accessExpiresAt,
            MusicKeyCreateTime = createTime,
            KeyExpiresIn = expiresIn,
        };

        var api = new QQMusicApiClient(cookie);

        if (string.Equals(mode, "variants", StringComparison.OrdinalIgnoreCase))
            return await RunVariantsAsync(service, api, credential);
        if (string.Equals(mode, "login", StringComparison.OrdinalIgnoreCase))
            return await RunLoginAsync(api, cookie);

        // 生产值：绝对时间戳（Login 通道三次都靠它成功，服务端显然不严格校验）
        if (await TryAsync(service, api, credential, 0, "绝对时间戳 expired_in")) return Finish(cookie, api);

        // 对照：相对秒数（字段名的字面语义）
        var remaining = accessExpiresAt > now ? accessExpiresAt - now
            : createTime > 0 && expiresIn > 0 ? Math.Max(0, createTime + expiresIn - now) : 0;
        if (remaining <= 0)
        {
            Console.WriteLine("[qqrefreshkey] 没有可用的相对秒数，跳过对照");
            return 1;
        }
        Console.WriteLine($"[qqrefreshkey] 相对秒数对照值 = {remaining}s（{Hours(remaining)}）");
        if (await TryAsync(service, api, credential, remaining, "相对秒数 expired_in")) return Finish(cookie, api);
        Console.WriteLine("[qqrefreshkey] 结论: 两种单位都被拒 → QQRefreshKey 不接受移动端 refresh_key，回到 Login 兜底+4 小时节奏观察");
        return 1;
    }

    /// <summary>variants：逐个改请求形状（appid/deviceName/deviceType、expired_in 单位、缺 refresh_key），
    /// 判断 40000 是"参数不对"还是"通道不认这套凭据"。某个形状成功就按生产路径落盘并停手。</summary>
    private static async Task<int> RunVariantsAsync(QQMusicQrLoginService service, QQMusicApiClient api,
        QQMusicQrCredential credential)
    {
        Console.WriteLine("[qqrefreshkey] variants: 参数形状取自 QQMusic_Protocol!FUN_100c8865");
        var host = Environment.MachineName;
        var cases = new (string Label, uint Appid, long ExpiredIn, bool IncludeKey, int? Ct)[]
        {
            ("现状：无 appid/deviceName/deviceType，ct=11", 0, credential.ExpiredAt, true, null),
            ("+appid=100310537 +deviceName +deviceType=Widnows", 100310537, credential.ExpiredAt, true, null),
            ("+appid=2000000024 +deviceName +deviceType", 2000000024, credential.ExpiredAt, true, null),
            ("+appid=100310537，expired_in 换成毫秒", 100310537, credential.ExpiredAt * 1000, true, null),
            ("comm ct=24（网页通道）+appid/deviceName", 100310537, credential.ExpiredAt, true, 24),
            ("comm ct=19（桌面通道）+appid/deviceName", 100310537, credential.ExpiredAt, true, 19),
            ("对照：不带 refresh_key", 0, credential.ExpiredAt, false, null),
        };
        foreach (var (label, appid, expiredIn, includeKey, ct) in cases)
        {
            var (code, message, data) = await service.ProbeMusicuCodeAsync("music.login.LoginServer",
                "QQRefreshKey", w =>
                {
                    w.WriteString("openid", credential.OpenId);
                    w.WriteString("musickey", credential.MusicKey);
                    if (includeKey) w.WriteString("refresh_key", credential.RefreshKey);
                    if (long.TryParse(credential.MusicId, out var musicId)) w.WriteNumber("musicid", musicId);
                    else w.WriteString("musicid", credential.MusicId);
                    if (credential.LoginType != 2) w.WriteString("str_musicid", credential.MusicId);
                    if (credential.AccessToken.Length > 0) w.WriteString("access_token", credential.AccessToken);
                    if (credential.RefreshToken.Length > 0) w.WriteString("refresh_token", credential.RefreshToken);
                    w.WriteNumber("onlyNeedAccessToken", 0);
                    w.WriteNumber("forceRefreshToken", 0);
                    if (appid > 0)
                    {
                        w.WriteNumber("appid", appid);
                        w.WriteString("deviceName", host);
                        w.WriteString("deviceType", "Widnows");
                    }
                    w.WriteNumber("expired_in", expiredIn);
                }, credential, ct, null, default);
            Console.WriteLine($"[qqrefreshkey]   code={code,-6} {label}{(message.Length > 0 ? " msg=" + message : "")}");
            if (code != 0) continue;
            api.ApplyRefreshedCredentialForProbe(data);
            Console.WriteLine("[qqrefreshkey] 结论: 这个参数形状被接受，已按生产路径落盘");
            return 0;
        }

        var (bogusCode, bogusMessage, _) = await service.ProbeMusicuCodeAsync("music.login.LoginServer",
            "QQRefreshKeyNotExist", w => w.WriteNumber("x", 1), credential, null, null, default);
        Console.WriteLine($"[qqrefreshkey]   code={bogusCode,-6} 对照：不存在的 method（看 40000 是不是通用参数错）" +
                          (bogusMessage.Length > 0 ? " msg=" + bogusMessage : ""));
        Console.WriteLine("[qqrefreshkey] 结论: 换参数形状仍被拒 → QQRefreshKey 只认 PC 客户端会话，移动端 refresh_key 用不了");
        return 1;
    }

    /// <summary>login：走生产续签路径（QQRefreshKey 被拒后回落 Login），验证 Login 通道对当前凭据是否有效。
    /// 会真实消耗一次登录链（one hop），只在用户明确要求时跑。</summary>
    private static async Task<int> RunLoginAsync(QQMusicApiClient api, CookieStore cookie)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await api.RefreshCredentialForProbeAsync(default);
            var raw = cookie.QQCookieRaw ?? "";
            var created = long.TryParse(Val(raw, "psrf_musickey_createtime"), out var parsed) ? parsed : 0;
            var lifetime = long.TryParse(Val(raw, "alyric_qq_key_expires_in"), out var parsedLifetime)
                ? parsedLifetime : 0;
            Console.WriteLine($"[qqrefreshkey] Login 续签: OK {watch.ElapsedMilliseconds}ms 新 keyCreate={created} " +
                              $"lifetime={(lifetime > 0 ? Hours(lifetime) : "缺")}");
            return 0;
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqrefreshkey] Login 续签: FAIL code={ex.Code} {watch.ElapsedMilliseconds}ms msg={ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqrefreshkey] Login 续签: FAIL {ex.GetType().Name} {watch.ElapsedMilliseconds}ms");
            return 1;
        }
    }

    private static async Task<bool> TryAsync(QQMusicQrLoginService service, QQMusicApiClient api,
        QQMusicQrCredential credential, long expiredInOverride, string label)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var data = await service.RefreshStoredCredentialWithRefreshKeyAsync(credential, default, expiredInOverride);
            var newKey = PickString(data, "musickey");
            Console.WriteLine(
                $"[qqrefreshkey] {label}: OK {watch.ElapsedMilliseconds}ms " +
                $"musickey={(newKey.Length > 0 && newKey != credential.MusicKey ? "已轮换" : "未变")} " +
                $"refresh_key={(PickString(data, "refresh_key").Length > 0 ? "已返回" : "未返回")} " +
                $"createTime={PickLong(data, "musickeyCreateTime")?.ToString() ?? "缺"} " +
                $"keyExpiresIn={PickLong(data, "keyExpiresIn")?.ToString() ?? "缺"}");
            api.ApplyRefreshedCredentialForProbe(data);
            return true;
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqrefreshkey] {label}: FAIL code={ex.Code} {watch.ElapsedMilliseconds}ms msg={ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqrefreshkey] {label}: FAIL {ex.GetType().Name} {watch.ElapsedMilliseconds}ms");
            return false;
        }
    }

    private static int Finish(CookieStore cookie, QQMusicApiClient api)
    {
        var raw = cookie.QQCookieRaw ?? "";
        var createTime = long.TryParse(Val(raw, "psrf_musickey_createtime"), out var parsedCreate) ? parsedCreate : 0;
        var expiresIn = long.TryParse(Val(raw, "alyric_qq_key_expires_in"), out var parsedLifetime) ? parsedLifetime : 0;
        var refreshAt = typeof(QQMusicApiClient)
            .GetField("_nativeCredentialRefreshAt", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(api);
        Console.WriteLine($"[qqrefreshkey] 已按生产路径落盘: keyCreate={createTime} " +
                          $"lifetime={(expiresIn > 0 ? Hours(expiresIn) : "缺")} 下次续签={refreshAt}");
        Console.WriteLine("[qqrefreshkey] 结论: QQRefreshKey 可以用，续签默认走它，Login 只在被拒时兜底");
        return 0;
    }

    private static string Hours(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Abs(seconds));
        var text = span.TotalDays >= 1 ? $"{span.TotalDays:F1}d" : $"{span.TotalHours:F1}h";
        return seconds < 0 ? $"-{text}" : text;
    }

    private static string PickString(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long? PickLong(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out number) ? number : null;
    }

    private static string? Val(string raw, string name)
    {
        foreach (var part in raw.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return null;
    }
}
