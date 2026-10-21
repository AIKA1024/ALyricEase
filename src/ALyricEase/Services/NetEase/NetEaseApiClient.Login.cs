using System.Text.Json;
using ALyricEase.Models.Dtos;
using Net.Codecrete.QrCodeGenerator;

namespace ALyricEase.Services.NetEase;

public enum NetEaseQrLoginStage
{
    Waiting,
    Scanned,
    Authorized,
    Expired,
}

public sealed record NetEaseQrLoginSession(string Key, byte[] QrPng);

public sealed record NetEaseQrLoginStatus(NetEaseQrLoginStage Stage, UserProfile? Profile = null);

/// <summary>网易云扫码 / 手机验证码登录。两条链路都走 eapi(interface 网关):
/// 实测本机网络下 weapi 登录端点被风控拦成空响应,eapi 可正常返回。
/// 登录成功后的 Set-Cookie(MUSIC_U)仍交给既有 <see cref="SetMusicUCookie"/> 持久化,
/// 再用资料接口二次验证,失败即回滚 —— 与代理/Cookie 登录同一出口。</summary>
public sealed partial class NetEaseApiClient
{
    private const string QrLoginUrlPrefix = "https://music.163.com/login?codekey=";

    /// <summary>生成扫码登录 key,并渲染本地二维码图片(网易云只返回 key,不返回图片)。</summary>
    public async Task<NetEaseQrLoginSession> CreateQrLoginAsync(CancellationToken ct = default)
    {
        // 二维码 key/check 带匿名 MUSIC_A 更稳(NCM 的 anonymous_token):缺它时扫码确认后
        // 服务端可能回 8821。但匿名注册本身会随机返回 400,拿不到就继续 —— key 请求还有机会
        // 由服务端直接下发 MUSIC_A,不能让登录按钮卡在"注册失败"。
        await TryEnsureAnonymousAsync(ct).ConfigureAwait(false);
        var payload = new Dictionary<string, object?> { ["type"] = 3 };
        var (doc, _) = await PostEapiLoginJsonAsync("/api/login/qrcode/unikey", payload, ct)
            .ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            var code = ReadCode(root);
            if (code != 200)
                throw new ApiException(ReadLoginError(root, code, "生成登录二维码失败"), code, ReadVerifyUrl(root));

            var key = ReadString(root, "unikey");
            if (key is null && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                key = ReadString(data, "unikey");
            if (string.IsNullOrWhiteSpace(key))
                throw new ApiException("登录二维码生成失败:响应缺少 unikey", code);

            var url = QrLoginUrlPrefix + Uri.EscapeDataString(key);
            var png = QrCode.EncodeText(url, QrCode.Ecc.Medium).ToPngBitmap(border: 3, scale: 8);
            // 部分节点在 key 响应里直接下发 MUSIC_A,存下来省掉后续的匿名注册。
            AdoptAnonymousMusicAFromContainer();
            return new NetEaseQrLoginSession(key, png);
        }
    }

    /// <summary>轮询扫码状态:801 等待、802 已扫码、803 已授权(此时写入并验证登录态)、800 过期。</summary>
    public async Task<NetEaseQrLoginStatus> CheckQrLoginAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ApiException("登录二维码已失效,请刷新", -1);
        var payload = new Dictionary<string, object?>
        {
            ["key"] = key,
            ["type"] = 3,
        };
        var (doc, musicU) = await PostEapiLoginJsonAsync("/api/login/qrcode/client/login", payload, ct)
            .ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            var code = ReadCode(root);
            switch (code)
            {
                case 801:
                    return new NetEaseQrLoginStatus(NetEaseQrLoginStage.Waiting);
                case 802:
                    return new NetEaseQrLoginStatus(NetEaseQrLoginStage.Scanned);
                case 803:
                    var profile = await CompleteLoginAsync(musicU, ct).ConfigureAwait(false);
                    return new NetEaseQrLoginStatus(NetEaseQrLoginStage.Authorized, profile);
                case 800:
                    return new NetEaseQrLoginStatus(NetEaseQrLoginStage.Expired);
                default:
                    throw new ApiException(ReadLoginError(root, code, "扫码登录失败"), code, ReadVerifyUrl(root));
            }
        }
    }

    /// <summary>向手机号发送短信验证码。失败(号码格式/频率限制/风控)抛 <see cref="ApiException"/>,
    /// 消息优先使用服务端返回的 message/msg。</summary>
    public async Task SendPhoneCodeAsync(string phone, string countryCode, CancellationToken ct = default)
    {
        NormalizePhone(phone, countryCode, out var normalizedCode, out var normalizedPhone);
        var payload = new Dictionary<string, object?>
        {
            ["ctcode"] = normalizedCode,
            ["secrete"] = "music_middleuser_pclogin",
            ["cellphone"] = normalizedPhone,
        };
        var (doc, _) = await PostEapiLoginJsonAsync("/api/sms/captcha/sent", payload, ct)
            .ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            var code = ReadCode(root);
            if (code != 200)
                throw new ApiException(ReadLoginError(root, code, "验证码发送失败"), code);
        }
    }

    /// <summary>手机号 + 短信验证码登录,成功后持久化凭证并返回服务端资料。</summary>
    public async Task<UserProfile> LoginWithPhoneCodeAsync(
        string phone, string countryCode, string captcha, CancellationToken ct = default)
    {
        NormalizePhone(phone, countryCode, out var normalizedCode, out var normalizedPhone);
        captcha = captcha.Trim();
        // 网易云的短信验证码位数不固定(实测有 4 位,也有 6 位),这里只做基本格式校验,
        // 具体位数交给服务端判断 —— 写死 6 位会把 4 位码的账号直接挡在本地。
        if (captcha.Length is < 4 or > 8 || !captcha.All(char.IsDigit))
            throw new ApiException("验证码格式不正确", -1);

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "1",
            ["https"] = "true",
            ["phone"] = normalizedPhone,
            ["countrycode"] = normalizedCode,
            ["captcha"] = captcha,
            ["remember"] = "true",
            ["secureCaptcha"] = "",
        };
        var (doc, musicU) = await PostEapiLoginJsonAsync("/api/w/login/cellphone", payload, ct)
            .ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            var code = ReadCode(root);
            if (code != 200)
                throw new ApiException(ReadLoginError(root, code, "手机号登录失败"), code);
        }
        return await CompleteLoginAsync(musicU, ct).ConfigureAwait(false);
    }

    /// <summary>eapi 登录请求:Android 客户端身份(播放/登录共用) + 从 Set-Cookie 提取 MUSIC_U。</summary>
    private async Task<(JsonDocument Document, string? MusicU)> PostEapiLoginJsonAsync(
        string apiPath, IReadOnlyDictionary<string, object?> payload, CancellationToken ct)
    {
        var payloadWithHeader = new Dictionary<string, object?>(payload)
        {
            ["header"] = BuildEapiIdentityHeader(),
        };
        using var req = CreateEapiRequest(apiPath, payloadWithHeader, includeRealIp: false,
            useInterfaceHost: true);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var musicU = ExtractMusicUCookie(resp);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            throw new ApiException("响应为空(加密通道被风控拦截)", BlockedCode);
        try
        {
            return (JsonDocument.Parse(body), musicU);
        }
        catch (JsonException)
        {
            throw new ApiException("响应不是有效 JSON", -2);
        }
    }

    /// <summary>写入登录凭证并调资料接口验证;验证不通过立即回滚,避免留下半残登录态。</summary>
    private async Task<UserProfile> CompleteLoginAsync(string? musicU, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(musicU))
            throw new ApiException("登录成功但未获取到网易云登录凭证", -1);
        SetMusicUCookie(musicU);
        try
        {
            return await GetUserProfileAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { ClearCookie(); } catch { /* 回滚失败不覆盖原始错误 */ }
            throw;
        }
    }

    private static string? ExtractMusicUCookie(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        foreach (var header in values)
        {
            foreach (var part in header.Split(';'))
            {
                var item = part.Trim();
                if (!item.StartsWith("MUSIC_U=", StringComparison.Ordinal)) continue;
                var value = item["MUSIC_U=".Length..];
                if (value.Length == 0 || string.Equals(value, "deleted", StringComparison.OrdinalIgnoreCase))
                    continue;
                return value;
            }
        }
        return null;
    }

    private static int ReadCode(JsonElement root)
        => root.TryGetProperty("code", out var node) && node.TryGetInt32(out var code) ? code : -1;

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static string ReadLoginError(JsonElement root, int code, string fallback)
    {
        var message = ReadString(root, "message") ?? ReadString(root, "msg");
        if (!string.IsNullOrWhiteSpace(message)) return message;
        return code switch
        {
            -462 => "网易要求完成验证操作,请稍后重试或更换网络",
            -460 or 8810 => "检测到网络环境存在风险,请更换网络后重试",
            400 => "手机号或登录参数格式不正确",
            405 or 509 => "操作过于频繁,请稍后再试",
            501 => "验证码错误,请检查后重试",
            503 => "验证码错误或已失效,请重新获取",
            8821 => "请切换其他登录方式或升级新版本再试",
            _ => $"{fallback}(code={code})",
        };
    }

    /// <summary>风控响应里的行为验证页:data.url 是带 params/verifyToken 的完整地址,
    /// data.verifyUrl 只有页面根地址(-462 实测两种都可能有)。</summary>
    private static string? ReadVerifyUrl(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var url = ReadString(data, "url");
            if (!string.IsNullOrWhiteSpace(url)) return url;
            var verifyUrl = ReadString(data, "verifyUrl");
            if (!string.IsNullOrWhiteSpace(verifyUrl)) return verifyUrl;
        }
        return ReadString(root, "url") ?? ReadString(root, "verifyUrl");
    }

    private static void NormalizePhone(string phone, string countryCode, out string normalizedCode,
        out string normalizedPhone)
    {
        normalizedCode = new string(countryCode.Where(char.IsDigit).ToArray());
        normalizedPhone = new string(phone.Where(char.IsDigit).ToArray());
        if (normalizedCode.Length is < 1 or > 4)
            throw new ApiException("国家或地区代码格式不正确", -1);
        if (normalizedPhone.Length is < 5 or > 15)
            throw new ApiException("请输入有效的手机号", -1);
        if (normalizedCode == "86" && (normalizedPhone.Length != 11 || normalizedPhone[0] != '1'))
            throw new ApiException("请输入 11 位中国大陆手机号", -1);
    }
}
