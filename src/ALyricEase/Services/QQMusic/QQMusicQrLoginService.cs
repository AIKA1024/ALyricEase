using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Services.QQMusic;

public enum QQMusicQrLoginStage
{
    Preparing,
    Waiting,
    Scanned,
    Exchanging,
}

public sealed record QQMusicQrLoginUpdate(QQMusicQrLoginStage Stage, byte[]? QrPng = null);

public sealed record QQMusicQrCredential(
    string MusicId,
    string MusicKey,
    int LoginType,
    string Nickname,
    string AvatarUrl);

public enum QQMusicPhoneCodeStage
{
    Sent,
    CaptchaRequired,
    FrequencyLimited,
}

public sealed record QQMusicPhoneCodeResult(QQMusicPhoneCodeStage Stage, string? SecurityUrl = null);

/// <summary>
/// QQ 音乐客户端原生扫码登录。流程与 Android 客户端一致：QIMEI → GetSession →
/// CreateQRCode → MQTT over WSS → LoginServer/Login。协议实现移植自 MIT 项目
/// yakult-green-tea/qq-music-api，不经过网页 Cookie，也不记录二维码 token/musickey。
/// </summary>
internal sealed class QQMusicQrLoginService : IDisposable
{
    private const string MusicuUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string QimeiUrl = "https://api.tencentmusic.com/tme/trpc/proxy";
    private const string MqttHost = "mu.y.qq.com";
    private const string MqttInitialPath = "/ws/handshake";
    private const string ChannelId = "10003505";
    private const string QimeiSecret = "ZdJqM15EeO2zWc08";
    private const string QimeiAppKey = "0AND0HD6FE4HY80F";
    private const int ClientVersion = 14090008;
    private static readonly TimeSpan QimeiLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan QrLifetime = TimeSpan.FromMinutes(3);
    private const string QimeiPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDEIxgwoutfwoJxcGQeedgP7FG9qaIuS0qzfR8gWkrkTZKM2iWHn2ajQpBR
        ZjMSoSf6+KJGvar2ORhBfpDXyVtZCKpqLQ+FLkpncClKVIrBwv6PHyUvuCb0rIarmgDnzkfQAqVufEtR64iazGDKatvJ9y6B
        9NMbHddGSAUmRTCrHQIDAQAB
        -----END PUBLIC KEY-----
        """;

    private readonly HttpClient _http;
    private readonly string _devicePath;
    private AndroidDevice _device;

    public QQMusicQrLoginService(CookieStore cookie)
    {
        _devicePath = Path.Combine(cookie.ConfigDirectory, "qq-device.json");
        _device = LoadDevice() ?? CreateDevice();
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
    }

    public async Task<QQMusicQrCredential> LoginAsync(
        IProgress<QQMusicQrLoginUpdate> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new QQMusicQrLoginUpdate(QQMusicQrLoginStage.Preparing));
        await PrepareAndroidSessionAsync(cancellationToken).ConfigureAwait(false);

        var (qrcodeId, png, expiresIn) = await CreateNativeQrAsync(cancellationToken).ConfigureAwait(false);
        var timeout = expiresIn > 0
            ? TimeSpan.FromSeconds(Math.Min(expiresIn, QrLifetime.TotalSeconds))
            : QrLifetime;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var mqtt = await ConnectMqttAsync(qrcodeId, deadline.Token).ConfigureAwait(false);
        await SubscribeAsync(mqtt, qrcodeId, deadline.Token).ConfigureAwait(false);
        progress.Report(new QQMusicQrLoginUpdate(QQMusicQrLoginStage.Waiting, png));

        var keepAlive = KeepAliveAsync(mqtt, deadline.Token);
        try
        {
            while (true)
            {
                var packet = await mqtt.Reader.ReadPacketAsync(deadline.Token).ConfigureAwait(false);
                if (packet.Length == 0 || packet[0] >> 4 != 3) continue;
                var message = ParsePublish(packet);
                switch (message.Type)
                {
                    case "waiting":
                        progress.Report(new QQMusicQrLoginUpdate(QQMusicQrLoginStage.Waiting, png));
                        break;
                    case "scanned":
                        progress.Report(new QQMusicQrLoginUpdate(QQMusicQrLoginStage.Scanned, png));
                        break;
                    case "cookies":
                    case "authorized":
                        progress.Report(new QQMusicQrLoginUpdate(QQMusicQrLoginStage.Exchanging, png));
                        var (musicId, token) = ParseMqttCredential(message.Payload);
                        var credential = await ExchangeCredentialAsync(
                            qrcodeId, musicId, token, deadline.Token).ConfigureAwait(false);
                        return await ValidateCredentialAsync(credential, deadline.Token).ConfigureAwait(false);
                    case "canceled":
                        throw new ApiException("已在手机上取消登录", -1);
                    case "timeout":
                        throw new ApiException("二维码已过期，请刷新后重试", -1);
                    case "loginFailed":
                        throw new ApiException("QQ 音乐扫码登录失败", -1);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException("二维码已过期，请刷新后重试", -1);
        }
        finally
        {
            deadline.Cancel();
            try { await keepAlive.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* 连接关闭时的 keepalive 失败不覆盖登录结果 */ }
        }
    }

    /// <summary>使用 Android 原生登录协议向指定手机号发送验证码。</summary>
    public async Task<QQMusicPhoneCodeResult> SendPhoneCodeAsync(
        string phone, string countryCode, CancellationToken ct)
    {
        ValidatePhone(phone, countryCode);
        await PrepareAndroidSessionAsync(ct).ConfigureAwait(false);
        var result = await CallMusicuResultAsync("music.login.LoginServer", "SendPhoneAuthCode", w =>
        {
            w.WriteString("tmeAppid", "qqmusic");
            w.WriteString("areaCode", countryCode);
            w.WriteString("phoneNo", phone);
        }, null, new CommOverrides(LoginMethod: 3), ct).ConfigureAwait(false);

        return result.Code switch
        {
            0 => new QQMusicPhoneCodeResult(QQMusicPhoneCodeStage.Sent),
            20276 => new QQMusicPhoneCodeResult(QQMusicPhoneCodeStage.CaptchaRequired,
                FirstNonEmpty(GetString(result.Data, "securityURL"), GetString(result.Data, "securityUrl"))),
            100001 => new QQMusicPhoneCodeResult(QQMusicPhoneCodeStage.FrequencyLimited),
            _ => throw CreatePhoneLoginException("发送验证码", result.Code, result.Data),
        };
    }

    /// <summary>使用手机号与 6 位验证码换取原生 musicid/musickey，并立即验证凭证。</summary>
    public async Task<QQMusicQrCredential> LoginWithPhoneCodeAsync(
        string phone, string countryCode, string authCode, CancellationToken ct)
    {
        ValidatePhone(phone, countryCode);
        if (authCode.Length != 6 || !authCode.All(char.IsDigit))
            throw new ApiException("验证码必须是 6 位数字", 20271);

        await PrepareAndroidSessionAsync(ct).ConfigureAwait(false);
        var result = await CallMusicuResultAsync("music.login.LoginServer", "Login", w =>
        {
            w.WriteString("phoneNo", phone);
            w.WriteString("code", authCode);
            w.WriteNumber("loginMode", 1);
        }, null, new CommOverrides(LoginType: 0, LoginMethod: 3), ct).ConfigureAwait(false);
        if (result.Code != 0)
            throw CreatePhoneLoginException("手机号登录", result.Code, result.Data);

        var credential = CredentialFrom(result.Data, 2);
        return await ValidateCredentialAsync(credential, ct).ConfigureAwait(false);
    }

    private async Task PrepareAndroidSessionAsync(CancellationToken ct)
    {
        await EnsureQimeiAsync(ct).ConfigureAwait(false);
        await RefreshSessionAsync(ct).ConfigureAwait(false);
        SaveDevice();
    }

    private static void ValidatePhone(string phone, string countryCode)
    {
        if (countryCode.Length is < 1 or > 4 || !countryCode.All(char.IsDigit))
            throw new ApiException("国家或地区代码格式不正确", -1);
        if (phone.Length is < 5 or > 15 || !phone.All(char.IsDigit))
            throw new ApiException("请输入有效的手机号", -1);
        if (countryCode == "86" && (phone.Length != 11 || phone[0] != '1'))
            throw new ApiException("请输入 11 位中国大陆手机号", -1);
    }

    private static ApiException CreatePhoneLoginException(string action, int code, JsonElement data)
    {
        var serverMessage = FirstNonEmpty(GetString(data, "errMsg"), GetString(data, "msg"),
            GetString(data, "message"));
        var message = code switch
        {
            1000 or 104400 or 104401 => "本次验证码已失效，请重新获取",
            20261 => "登录参数无效，请重新获取验证码",
            20271 => "验证码错误，请检查后重试",
            20272 => "该手机号的账号绑定状态异常",
            20274 => "该手机号尚未绑定 QQ 音乐账号",
            20277 or 20278 => "账号受限，暂时无法登录",
            20279 => "登录设备数已达上限，请先在 QQ 音乐中移除设备",
            20450 => "账号已被封禁，暂时无法登录",
            104604 => "操作过于频繁，请稍后再试",
            _ when serverMessage.Length > 0 => serverMessage,
            _ => $"{action}失败(code={code})",
        };
        return new ApiException(message, code);
    }

    /// <summary>应用重启后用已保存的原生设备上下文验证扫码凭证。</summary>
    public async Task<bool> ValidateStoredCredentialAsync(
        string musicId, string musicKey, int loginType, CancellationToken ct)
    {
        try
        {
            await ValidateCredentialAsync(new Credential(musicId, musicKey, loginType, default), ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (ApiException ex) when (ex.Code is 1000 or 104400 or 104401)
        {
            return false;
        }
    }

    private async Task EnsureQimeiAsync(CancellationToken ct)
    {
        if (_device.Qimei is { Length: > 0 } && _device.Qimei36 is { Length: > 0 } &&
            DateTimeOffset.UtcNow - _device.QimeiSavedAt < QimeiLifetime)
            return;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cryptKey = RandomHex(16);
        var nonce = RandomHex(16);
        var keyBytes = Encoding.UTF8.GetBytes(cryptKey);
        byte[] encryptedKey;
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(QimeiPublicKey);
            encryptedKey = rsa.Encrypt(keyBytes, RSAEncryptionPadding.Pkcs1);
        }

        var payload = BuildQimeiPayload();
        byte[] encryptedPayload;
        using (var aes = Aes.Create())
        {
            aes.Key = keyBytes;
            aes.IV = keyBytes;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            encryptedPayload = encryptor.TransformFinalBlock(payload, 0, payload.Length);
        }

        var key = Convert.ToBase64String(encryptedKey);
        var parameters = Convert.ToBase64String(encryptedPayload);
        const string extra = "{\"appKey\":\"0AND0HD6FE4HY80F\"}";
        var body = BuildJson(w =>
        {
            w.WriteNumber("app", 0);
            w.WriteNumber("os", 1);
            w.WriteStartObject("qimeiParams");
            w.WriteString("key", key);
            w.WriteString("params", parameters);
            w.WriteString("time", timestamp.ToString(CultureInfo.InvariantCulture));
            w.WriteString("nonce", nonce);
            w.WriteString("sign", Md5(key, parameters,
                (timestamp * 1000).ToString(CultureInfo.InvariantCulture), nonce, QimeiSecret, extra));
            w.WriteString("extra", extra);
            w.WriteEndObject();
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, QimeiUrl)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Host = "api.tencentmusic.com";
        request.Headers.TryAddWithoutValidation("method", "GetQimei");
        request.Headers.TryAddWithoutValidation("service", "trpc.tme_datasvr.qimeiproxy.QimeiProxy");
        request.Headers.TryAddWithoutValidation("appid", "qimei_qq_android");
        request.Headers.TryAddWithoutValidation("sign", Md5("qimei_qq_androidpzAuCmaFAaFaHrdakPjLIEqKrGnSOOvH",
            timestamp.ToString(CultureInfo.InvariantCulture)));
        request.Headers.TryAddWithoutValidation("user-agent", "QQMusic");
        request.Headers.TryAddWithoutValidation("timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"QQ 设备初始化失败 HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        using var outerDoc = JsonDocument.Parse(text);
        var outer = outerDoc.RootElement;
        var inner = ParseObjectValue(GetProperty(outer, "data"));
        var data = ParseObjectValue(GetProperty(inner, "data"));
        _device.Qimei = GetString(data, "q16");
        _device.Qimei36 = GetString(data, "q36");
        if (_device.Qimei.Length == 0 || _device.Qimei36.Length == 0)
            throw new ApiException("QQ 设备初始化未返回 QIMEI", GetInt(inner, "code") ?? -1);
        _device.QimeiSavedAt = DateTimeOffset.UtcNow;
        SaveDevice();
    }

    private byte[] BuildQimeiPayload()
    {
        var now = DateTimeOffset.UtcNow;
        var uptime = now.AddSeconds(-RandomInt(14401)).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var reserved = Encoding.UTF8.GetString(BuildJson(w =>
        {
            w.WriteString("harmony", "0"); w.WriteString("clone", "0"); w.WriteString("containe", "");
            w.WriteString("oz", "UhYmelwouA+V2nPWbOvLTgN2/m8jwGB+yUB5v9tysQg=");
            w.WriteString("oo", "Xecjt+9S1+f8Pz2VLSxgpw=="); w.WriteString("kelong", "0");
            w.WriteString("uptimes", uptime); w.WriteString("multiUser", "0");
            w.WriteString("bod", _device.Brand); w.WriteString("dv", _device.Device);
            w.WriteString("firstLevel", ""); w.WriteString("manufact", _device.Brand);
            w.WriteString("name", _device.Model); w.WriteString("host", "se.infra");
            w.WriteString("kernel", _device.ProcVersion);
        }));
        return BuildJson(w =>
        {
            w.WriteString("androidId", _device.AndroidId); w.WriteNumber("platformId", 1);
            w.WriteString("appKey", QimeiAppKey); w.WriteString("appVersion", "14.9.0.8");
            w.WriteString("beaconIdSrc", RandomBeaconId(now)); w.WriteString("brand", _device.Brand);
            w.WriteString("channelId", ChannelId); w.WriteString("cid", ""); w.WriteString("imei", _device.Imei);
            w.WriteString("imsi", ""); w.WriteString("mac", ""); w.WriteString("model", _device.Model);
            w.WriteString("networkType", "unknown"); w.WriteString("oaid", "");
            w.WriteString("osVersion", $"Android {_device.OsRelease},level {_device.Sdk}");
            w.WriteString("qimei", ""); w.WriteString("qimei36", ""); w.WriteString("sdkVersion", "1.2.13.6");
            w.WriteString("targetSdkVersion", "33"); w.WriteString("audit", ""); w.WriteString("userId", "{}");
            w.WriteString("packageId", "com.tencent.qqmusic"); w.WriteString("deviceType", "Phone");
            w.WriteString("sdkName", ""); w.WriteString("reserved", reserved);
        });
    }

    private async Task RefreshSessionAsync(CancellationToken ct)
    {
        var data = await CallMusicuAsync("music.getSession.session", "GetSession", w =>
        {
            w.WriteString("uid", _device.SessionUid ?? "");
            w.WriteNumber("vkey", 0);
            w.WriteNumber("caller", 0);
        }, null, null, ct).ConfigureAwait(false);
        var session = GetProperty(data, "session");
        _device.SessionUid = GetString(session, "uid");
        _device.SessionSid = GetString(session, "sid");
        if (_device.SessionUid.Length == 0 || _device.SessionSid.Length == 0)
            throw new ApiException("QQ 会话初始化失败", -1);
    }

    private async Task<(string Id, byte[] Png, int ExpiresIn)> CreateNativeQrAsync(CancellationToken ct)
    {
        var data = await CallMusicuAsync("music.login.LoginServer", "CreateQRCode", w =>
        {
            w.WriteString("tmeAppID", "qqmusic");
            w.WriteNumber("ct", 11);
            w.WriteNumber("cv", ClientVersion);
        }, null, new CommOverrides(23, 0), ct).ConfigureAwait(false);
        var id = GetString(data, "qrcodeID");
        var encoded = GetString(data, "qrcode");
        var comma = encoded.LastIndexOf(',');
        if (comma >= 0) encoded = encoded[(comma + 1)..];
        byte[] png;
        try { png = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new ApiException("QQ 音乐返回了无效二维码", -1); }
        if (id.Length == 0 || png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            throw new ApiException("QQ 音乐未返回有效二维码", -1);
        return (id, png, GetInt(data, "expiresIn") ?? 0);
    }

    private async Task<Credential> ExchangeCredentialAsync(
        string qrId, string musicId, string token, CancellationToken ct)
    {
        try
        {
            var data = await CallMusicuAsync("music.login.LoginServer", "Login", w =>
            {
                if (long.TryParse(musicId, out var id)) w.WriteNumber("musicid", id);
                else w.WriteString("musicid", musicId);
                w.WriteString("qrCodeID", qrId);
                w.WriteString("token", token);
            }, null, new CommOverrides(LoginType: 6), ct).ConfigureAwait(false);
            return CredentialFrom(data, 6);
        }
        catch (ApiException)
        {
            // 部分账号的 MQTT token 本身就是可用 musickey；按参考实现验证后再接受。
            return new Credential(musicId, token, 6, default);
        }
    }

    private async Task<QQMusicQrCredential> ValidateCredentialAsync(Credential credential, CancellationToken ct)
    {
        var data = await CallMusicuAsync("music.UserInfo.userInfoServer", "GetLoginUserInfo",
            _ => { }, credential, null, ct).ConfigureAwait(false);
        var info = data.TryGetProperty("info", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested : data;
        var nickname = FirstNonEmpty(GetString(info, "nick"), GetString(data, "nickname"),
            GetString(credential.Raw, "nick"), GetString(credential.Raw, "nickname"));
        var avatar = FirstNonEmpty(GetString(info, "logo"), GetString(data, "avatarUrl"),
            GetString(credential.Raw, "logo"), GetString(credential.Raw, "avatarUrl"));
        return new QQMusicQrCredential(credential.MusicId, credential.MusicKey,
            credential.LoginType, nickname, avatar);
    }

    private async Task<JsonElement> CallMusicuAsync(
        string module,
        string method,
        Action<Utf8JsonWriter> writeParam,
        Credential? credential,
        CommOverrides? overrides,
        CancellationToken ct)
    {
        var result = await CallMusicuResultAsync(module, method, writeParam, credential, overrides, ct)
            .ConfigureAwait(false);
        if (result.Code != 0)
            throw new ApiException($"QQ 登录协议失败({method}, {result.GlobalCode}/{result.ItemCode})", result.Code);
        return result.Data;
    }

    private async Task<ApiCallResult> CallMusicuResultAsync(
        string module,
        string method,
        Action<Utf8JsonWriter> writeParam,
        Credential? credential,
        CommOverrides? overrides,
        CancellationToken ct)
    {
        var body = BuildJson(w =>
        {
            w.WriteStartObject("comm");
            WriteAndroidComm(w, credential, overrides);
            w.WriteEndObject();
            w.WriteStartObject("req_0");
            w.WriteString("module", module);
            w.WriteString("method", method);
            w.WriteStartObject("param"); writeParam(w); w.WriteEndObject();
            w.WriteEndObject();
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, MusicuUrl)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"QQMusic {ClientVersion}(android {_device.OsRelease})");
        if (credential is not null)
            request.Headers.TryAddWithoutValidation("Cookie", CredentialCookie(credential));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ApiException($"QQ 登录请求失败 HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var globalCode = GetInt(root, "code") ?? 0;
        if (!root.TryGetProperty("req_0", out var item))
            throw new ApiException("QQ 登录响应缺少 req_0", globalCode);
        var code = GetInt(item, "code") ?? 0;
        var data = GetProperty(item, "data").Clone();
        return new ApiCallResult(code != 0 ? code : globalCode, globalCode, code, data);
    }

    private void WriteAndroidComm(Utf8JsonWriter w, Credential? credential,
        CommOverrides? overrides)
    {
        w.WriteNumber("ct", overrides?.Ct ?? 11); w.WriteNumber("cv", overrides?.Cv ?? ClientVersion);
        w.WriteNumber("v", ClientVersion); w.WriteString("chid", ChannelId); w.WriteString("tmeAppID", "qqmusic");
        w.WriteString("QIMEI", _device.Qimei); w.WriteString("QIMEI36", _device.Qimei36);
        w.WriteString("OpenUDID", _device.OpenUdid); w.WriteString("udid", _device.OpenUdid);
        w.WriteString("OpenUDID2", _device.OpenUdid); w.WriteString("aid", _device.AndroidId);
        w.WriteString("os_ver", _device.OsRelease); w.WriteString("phonetype", _device.Model);
        w.WriteString("devicelevel", _device.Sdk.ToString(CultureInfo.InvariantCulture));
        w.WriteString("newdevicelevel", _device.Sdk.ToString(CultureInfo.InvariantCulture));
        w.WriteString("rom", _device.Fingerprint);
        if (!string.IsNullOrEmpty(_device.SessionUid)) w.WriteString("uid", _device.SessionUid);
        if (!string.IsNullOrEmpty(_device.SessionSid)) w.WriteString("sid", _device.SessionSid);
        if (credential is not null)
        {
            w.WriteString("qq", credential.MusicId); w.WriteString("authst", credential.MusicKey);
            w.WriteNumber("tmeLoginType", overrides?.LoginType ?? credential.LoginType);
        }
        else if (overrides?.LoginType is { } loginType)
            w.WriteNumber("tmeLoginType", loginType);
        if (overrides?.LoginMethod is { } loginMethod)
            w.WriteNumber("tmeLoginMethod", loginMethod);
    }

    private static Credential CredentialFrom(JsonElement data, int fallbackLoginType)
    {
        var musicId = FirstNonEmpty(GetString(data, "str_musicid"), GetString(data, "musicid"));
        var key = GetString(data, "musickey");
        if (musicId.Length == 0 || key.Length == 0)
            throw new ApiException("QQ 登录响应缺少账号凭证", -1);
        return new Credential(musicId, key, GetInt(data, "loginType") ?? fallbackLoginType, data.Clone());
    }

    private static string CredentialCookie(Credential credential) =>
        $"uin={credential.MusicId}; qqmusic_uin={credential.MusicId}; qm_keyst={credential.MusicKey}; qqmusic_key={credential.MusicKey}";

    public void Dispose() => _http.Dispose();

    private sealed record Credential(string MusicId, string MusicKey, int LoginType, JsonElement Raw);

    private sealed record CommOverrides(
        int? Ct = null, int? Cv = null, int? LoginType = null, int? LoginMethod = null);

    private sealed record ApiCallResult(int Code, int GlobalCode, int ItemCode, JsonElement Data);

    private sealed record MqttMessage(string? Type, string Payload);

    private sealed class AndroidDevice
    {
        public string Display { get; set; } = ""; public string Product { get; set; } = "";
        public string Device { get; set; } = ""; public string Board { get; set; } = "";
        public string Model { get; set; } = ""; public string Fingerprint { get; set; } = "";
        public string ProcVersion { get; set; } = ""; public string Imei { get; set; } = "";
        public string Brand { get; set; } = ""; public string AndroidId { get; set; } = "";
        public string OpenUdid { get; set; } = ""; public string OsRelease { get; set; } = "10";
        public int Sdk { get; set; } = 29; public string Qimei { get; set; } = "";
        public string Qimei36 { get; set; } = ""; public DateTimeOffset QimeiSavedAt { get; set; }
        public string? SessionUid { get; set; } public string? SessionSid { get; set; }
    }

    private AndroidDevice? LoadDevice()
    {
        try
        {
            if (!File.Exists(_devicePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(_devicePath));
            var root = doc.RootElement;
            var device = new AndroidDevice
            {
                Display = GetString(root, "display"), Product = GetString(root, "product"),
                Device = GetString(root, "device"), Board = GetString(root, "board"),
                Model = GetString(root, "model"), Fingerprint = GetString(root, "fingerprint"),
                ProcVersion = GetString(root, "procVersion"), Imei = GetString(root, "imei"),
                Brand = GetString(root, "brand"), AndroidId = GetString(root, "androidId"),
                OpenUdid = GetString(root, "openUdid"), OsRelease = GetString(root, "osRelease"),
                Sdk = GetInt(root, "sdk") ?? 29, Qimei = GetString(root, "qimei"),
                Qimei36 = GetString(root, "qimei36"), SessionUid = NullIfEmpty(GetString(root, "sessionUid")),
                SessionSid = NullIfEmpty(GetString(root, "sessionSid")),
            };
            if (root.TryGetProperty("qimeiSavedAt", out var saved) &&
                saved.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(saved.GetString(), out var parsed))
                device.QimeiSavedAt = parsed;
            return RequiredDeviceFieldsValid(device) ? device : null;
        }
        catch { return null; }
    }

    private void SaveDevice()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_devicePath)!);
            var bytes = BuildJson(w =>
            {
                w.WriteString("display", _device.Display); w.WriteString("product", _device.Product);
                w.WriteString("device", _device.Device); w.WriteString("board", _device.Board);
                w.WriteString("model", _device.Model); w.WriteString("fingerprint", _device.Fingerprint);
                w.WriteString("procVersion", _device.ProcVersion); w.WriteString("imei", _device.Imei);
                w.WriteString("brand", _device.Brand); w.WriteString("androidId", _device.AndroidId);
                w.WriteString("openUdid", _device.OpenUdid); w.WriteString("osRelease", _device.OsRelease);
                w.WriteNumber("sdk", _device.Sdk); w.WriteString("qimei", _device.Qimei);
                w.WriteString("qimei36", _device.Qimei36);
                w.WriteString("qimeiSavedAt", _device.QimeiSavedAt.ToString("O", CultureInfo.InvariantCulture));
                if (_device.SessionUid is not null) w.WriteString("sessionUid", _device.SessionUid);
                if (_device.SessionSid is not null) w.WriteString("sessionSid", _device.SessionSid);
            });
            var temp = _devicePath + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _devicePath, true);
        }
        catch { /* 下次登录重新初始化即可 */ }
    }

    private static AndroidDevice CreateDevice() => new()
    {
        Display = $"QMAPI.{RandomDigits(6)}.001", Product = "iarim", Device = "sagit", Board = "eomam",
        Model = "MI 6", Fingerprint = $"xiaomi/iarim/sagit:10/eomam.200122.001/{RandomDigits(7)}:user/release-keys",
        ProcVersion = $"Linux 5.4.0-54-generic-{RandomHex(8)} (android-build@google.com)",
        Imei = RandomImei(), Brand = "Xiaomi", AndroidId = RandomHex(16), OpenUdid = RandomHex(32),
        OsRelease = "10", Sdk = 29,
    };

    private static bool RequiredDeviceFieldsValid(AndroidDevice value) =>
        new[] { value.Display, value.Product, value.Device, value.Board, value.Model, value.Fingerprint,
            value.ProcVersion, value.Imei, value.Brand, value.AndroidId, value.OpenUdid, value.OsRelease }
        .All(v => !string.IsNullOrWhiteSpace(v));

    private static string RandomBeaconId(DateTimeOffset now)
    {
        var month = now.UtcDateTime.ToString("yyyy-MM-01", CultureInfo.InvariantCulture);
        var first = RandomDigits(6); var second = RandomDigits(9);
        int[] dated = [1, 2, 13, 14, 17, 18, 21, 22, 25, 26, 29, 30, 33, 34, 37, 38];
        var builder = new StringBuilder(600);
        for (var key = 1; key <= 40; key++)
        {
            builder.Append('k').Append(key).Append(':');
            if (dated.Contains(key)) builder.Append(month).Append(first).Append('.').Append(second);
            else if (key == 3) builder.Append("0000000000000000");
            else if (key == 4) builder.Append(RandomHex(16).Replace('0', '1'));
            else builder.Append(RandomInt(10000));
            builder.Append(';');
        }
        return builder.ToString();
    }

    private static string RandomImei()
    {
        var digits = RandomDigits(14).Select(c => c - '0').ToList();
        var sum = 0;
        for (var i = 0; i < digits.Count; i++)
        {
            var digit = digits[i];
            if (i % 2 == 1) digit = digit * 2 > 9 ? digit * 2 - 9 : digit * 2;
            sum += digit;
        }
        digits.Add((10 - sum % 10) % 10);
        return string.Concat(digits);
    }

    private static int RandomInt(int maxExclusive) => RandomNumberGenerator.GetInt32(maxExclusive);
    private static string RandomDigits(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++) chars[i] = (char)('0' + RandomInt(10));
        return new string(chars);
    }
    private static string RandomHex(int length) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes((length + 1) / 2)).ToLowerInvariant()[..length];
    private static string Md5(params string[] values) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(string.Concat(values)))).ToLowerInvariant();

    private static byte[] BuildJson(Action<Utf8JsonWriter> writeBody)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writeBody(writer); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string GetString(JsonElement element, string name) => GetString(GetProperty(element, name));
    private static string GetString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => "",
    };
    private static int? GetInt(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }
    private static JsonElement ParseObjectValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object) return element.Clone();
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString())) return default;
        using var doc = JsonDocument.Parse(element.GetString()!);
        return doc.RootElement.Clone();
    }
    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static (string MusicId, string Token) ParseMqttCredential(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var cookies = GetProperty(doc.RootElement, "cookies");
            var musicId = GetString(GetProperty(cookies, "qqmusic_uin"), "value");
            var token = GetString(GetProperty(cookies, "qqmusic_key"), "value");
            if (musicId.Length > 0 && token.Length > 0) return (musicId, token);
        }
        catch (JsonException) { }
        throw new ApiException("扫码结果缺少 QQ 音乐登录凭证", -1);
    }

    // ---- MQTT 5.0 over WebSocket ---------------------------------------------------------------

    private async Task<MqttConnection> ConnectMqttAsync(string qrId, CancellationToken ct)
    {
        var path = MqttInitialPath;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("mqtt");
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                await socket.ConnectAsync(new Uri($"wss://{MqttHost}{path}"), ct).ConfigureAwait(false);
                var connection = new MqttConnection(socket);
                await connection.SendAsync(BuildConnectPacket(
                    $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{RandomDigits(4)}", qrId), ct).ConfigureAwait(false);
                var packet = await connection.Reader.ReadPacketAsync(ct).ConfigureAwait(false);
                var (reason, reference) = ParseConnack(packet);
                if (reason == 0) return connection;
                await connection.DisposeAsync().ConfigureAwait(false);
                if ((reason != 0x9c && reason != 0x9d) || reference.Length == 0 || redirects == 3)
                    throw new ApiException($"QQ 扫码连接被拒绝(0x{reason:x2})", reason);
                path = RedirectPath(path, reference);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new ApiException("QQ 扫码连接重定向次数过多", -1);
    }

    private static async Task SubscribeAsync(MqttConnection mqtt, string qrId, CancellationToken ct)
    {
        await mqtt.SendAsync(BuildSubscribePacket(qrId), ct).ConfigureAwait(false);
        while (true)
        {
            var packet = await mqtt.Reader.ReadPacketAsync(ct).ConfigureAwait(false);
            if (packet.Length == 0 || packet[0] >> 4 != 9) continue;
            var reason = packet[^1];
            if (reason >= 0x80) throw new ApiException($"QQ 扫码订阅被拒绝(0x{reason:x2})", reason);
            return;
        }
    }

    private static async Task KeepAliveAsync(MqttConnection mqtt, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            await mqtt.SendAsync([0xc0, 0x00], ct).ConfigureAwait(false);
    }

    private static byte[] BuildConnectPacket(string clientId, string qrId)
    {
        var properties = EncodeProperties("pass",
        [
            ("tmeAppID", "qqmusic"), ("business", "management"), ("hashTag", qrId),
            ("clientTag", "management.user"), ("userID", qrId),
        ]);
        using var body = new MemoryStream();
        WriteUtf8(body, "MQTT"); body.WriteByte(0x05); body.WriteByte(0x02);
        body.WriteByte(0x00); body.WriteByte(45); body.Write(properties);
        WriteUtf8(body, clientId);
        return WrapPacket(0x10, body.ToArray());
    }

    private static byte[] BuildSubscribePacket(string qrId)
    {
        var properties = EncodeProperties(null, [("authorization", "tmelogin"), ("pubsub", "unicast")]);
        using var body = new MemoryStream();
        body.WriteByte(0x00); body.WriteByte(0x01); body.Write(properties);
        WriteUtf8(body, $"management.qrcode_login/{qrId}"); body.WriteByte(0x00);
        return WrapPacket(0x82, body.ToArray());
    }

    private static byte[] EncodeProperties(string? authMethod, (string Key, string Value)[] properties)
    {
        using var data = new MemoryStream();
        if (authMethod is not null) { data.WriteByte(0x15); WriteUtf8(data, authMethod); }
        foreach (var (key, value) in properties)
        {
            data.WriteByte(0x26); WriteUtf8(data, key); WriteUtf8(data, value);
        }
        var bytes = data.ToArray();
        return [.. EncodeVariableInteger(bytes.Length), .. bytes];
    }

    private static byte[] WrapPacket(byte header, byte[] body) =>
        [header, .. EncodeVariableInteger(body.Length), .. body];

    private static byte[] EncodeVariableInteger(int value)
    {
        var bytes = new List<byte>(4);
        do
        {
            var digit = value % 128; value /= 128;
            if (value > 0) digit |= 0x80;
            bytes.Add((byte)digit);
        } while (value > 0);
        return bytes.ToArray();
    }

    private static (int Value, int Bytes)? DecodeVariableInteger(ReadOnlySpan<byte> bytes, int offset)
    {
        var multiplier = 1; var value = 0;
        for (var count = 0; count < 4; count++)
        {
            if (offset + count >= bytes.Length) return null;
            var digit = bytes[offset + count];
            value += (digit & 0x7f) * multiplier;
            if ((digit & 0x80) == 0) return (value, count + 1);
            multiplier *= 128;
        }
        throw new ApiException("MQTT 长度字段无效", -1);
    }

    private static void WriteUtf8(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        stream.Write(length); stream.Write(bytes);
    }

    private static (string Value, int Next) ReadUtf8(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset + 2 > bytes.Length) throw new ApiException("MQTT 字符串被截断", -1);
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
        var start = offset + 2;
        if (start + length > bytes.Length) throw new ApiException("MQTT 字符串被截断", -1);
        return (Encoding.UTF8.GetString(bytes.Slice(start, length)), start + length);
    }

    private static (int Next, string ServerReference, string? EventType) ParseProperties(
        ReadOnlySpan<byte> bytes, int offset)
    {
        var length = DecodeVariableInteger(bytes, offset) ?? throw new ApiException("MQTT 属性被截断", -1);
        var cursor = offset + length.Bytes; var end = cursor + length.Value;
        if (end > bytes.Length) throw new ApiException("MQTT 属性被截断", -1);
        var serverReference = ""; string? eventType = null;
        while (cursor < end)
        {
            var id = bytes[cursor++];
            if (id == 0x26)
            {
                var key = ReadUtf8(bytes, cursor); var value = ReadUtf8(bytes, key.Next);
                if (key.Value == "type") eventType = value.Value;
                cursor = value.Next;
            }
            else if (id is 0x1c or 0x1f)
            {
                var value = ReadUtf8(bytes, cursor);
                if (id == 0x1c) serverReference = value.Value;
                cursor = value.Next;
            }
            else cursor = SkipProperty(bytes, cursor, id);
        }
        return (end, serverReference, eventType);
    }

    private static int SkipProperty(ReadOnlySpan<byte> bytes, int offset, byte id)
    {
        if (id is 0x03 or 0x08 or 0x12 or 0x15 or 0x1a) return ReadUtf8(bytes, offset).Next;
        if (id is 0x13 or 0x21 or 0x22 or 0x23) return offset + 2;
        if (id is 0x02 or 0x11 or 0x18 or 0x27) return offset + 4;
        if (id is 0x01 or 0x17 or 0x19 or 0x24 or 0x25 or 0x28 or 0x29 or 0x2a) return offset + 1;
        if (id is 0x09 or 0x16)
        {
            if (offset + 2 > bytes.Length) throw new ApiException("MQTT 二进制属性被截断", -1);
            return offset + 2 + BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
        }
        if (id == 0x0b)
        {
            var value = DecodeVariableInteger(bytes, offset) ?? throw new ApiException("MQTT 属性被截断", -1);
            return offset + value.Bytes;
        }
        throw new ApiException($"暂不支持 MQTT 属性 0x{id:x2}", -1);
    }

    private static (int Reason, string Reference) ParseConnack(byte[] packet)
    {
        if (packet.Length < 4 || packet[0] >> 4 != 2) throw new ApiException("QQ 扫码连接响应无效", -1);
        var remaining = DecodeVariableInteger(packet, 1) ?? throw new ApiException("QQ 扫码连接响应被截断", -1);
        var offset = 1 + remaining.Bytes;
        var properties = ParseProperties(packet, offset + 2);
        return (packet[offset + 1], properties.ServerReference);
    }

    private static MqttMessage ParsePublish(byte[] packet)
    {
        var remaining = DecodeVariableInteger(packet, 1) ?? throw new ApiException("QQ 扫码消息被截断", -1);
        var cursor = 1 + remaining.Bytes;
        cursor = ReadUtf8(packet, cursor).Next;
        if (((packet[0] >> 1) & 0x03) > 0) cursor += 2;
        var properties = ParseProperties(packet, cursor);
        return new MqttMessage(properties.EventType,
            Encoding.UTF8.GetString(packet, properties.Next, packet.Length - properties.Next));
    }

    private static string RedirectPath(string path, string reference)
    {
        var parts = path.TrimEnd('/').Split('/').ToList();
        if (parts.Count > 0 && parts[^1].Contains(':')) parts[^1] = reference;
        else parts.Add(reference);
        return string.Join('/', parts);
    }

    private sealed class MqttConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        public MqttPacketReader Reader { get; }
        public MqttConnection(ClientWebSocket socket) { _socket = socket; Reader = new MqttPacketReader(socket); }
        public async Task SendAsync(byte[] bytes, CancellationToken ct)
        {
            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try { await _socket.SendAsync(bytes, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false); }
            finally { _sendGate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            _socket.Dispose(); _sendGate.Dispose();
        }
    }

    private sealed class MqttPacketReader
    {
        private readonly ClientWebSocket _socket;
        private readonly List<byte> _buffer = new(4096);
        public MqttPacketReader(ClientWebSocket socket) => _socket = socket;
        public async Task<byte[]> ReadPacketAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_buffer.Count >= 2)
                {
                    var current = _buffer.ToArray();
                    var remaining = DecodeVariableInteger(current, 1);
                    if (remaining is { } length)
                    {
                        var total = 1 + length.Bytes + length.Value;
                        if (current.Length >= total)
                        {
                            var packet = current[..total]; _buffer.RemoveRange(0, total); return packet;
                        }
                    }
                }
                var chunk = new byte[8192];
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(chunk, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new ApiException("QQ 扫码连接已关闭", -1);
                    if (result.MessageType == WebSocketMessageType.Binary)
                        _buffer.AddRange(chunk.AsSpan(0, result.Count).ToArray());
                } while (!result.EndOfMessage);
            }
        }
    }
}
