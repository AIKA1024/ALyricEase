using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ALyricEase.Services.Crypto;

/// <summary>网易云 weapi/eapi 加密。算法与各语言移植实现(参考 wwh1004/NeteaseCloudMusicApi)一致:
/// weapi = 双 AES-128-CBC 嵌套 + 自定义 RSA;eapi = AES-128-ECB + nobody 前缀。</summary>
public sealed class CryptoService
{
    private const string PresetKey = "0CoJUm6Qyw8W8jud"; // 16 字节 = AES-128
    private const string Iv = "0102030405060708";
    private const string EapiKey = "e82ckenh8dichen8";

    private static readonly char[] SecretKeyChars =
        "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

    /// <summary>weapi 加密,返回 (params, encSecKey),配合 form-urlencoded 提交。</summary>
    public (string Params, string EncSecKey) EncryptWeapi(IReadOnlyDictionary<string, object?> payload)
    {
        var text = SerializeObject(payload);
        var secretKey = CreateSecretKey(16);
        return EncryptWeapiWithKey(text, secretKey);
    }

    /// <summary>临时诊断:固定 secretKey 加密,便于与参考实现做字节级对比。</summary>
    public (string Params, string EncSecKey) EncryptWeapiWithKey(string jsonText, string secretKey)
    {
        // params = base64(aes_cbc(base64(aes_cbc(text, presetKey)), secretKey))
        var inner = AesCbcEncryptToBase64(jsonText, PresetKey, Iv);
        var @params = AesCbcEncryptToBase64(inner, secretKey, Iv);

        // encSecKey = rsa(反转(secretKey))
        var encSecKey = RsaPadding.Encrypt(Reverse(secretKey));
        return (@params, encSecKey);
    }

    /// <summary>eapi 加密,返回单个 params(参考实现 Cirrus/NeteaseCloudMusicApi 的三段式格式)。
    /// urlPath 为明文里的 /api/... 路径,如 /api/song/enhance/player/url/v1。
    /// payload 是接口参数(客户端身份 header 作为其中的 "header" 键一并放入)。
    /// 旧实现把 {method,url,params,header} 包一层再拼 "nobody{use}{this}" 前缀,与真实客户端
    /// 抓包格式(nobody{url}use{json}md5forencrypt 签名 + {url}-36cd479b6b5-{json}-36cd479b6b5-{md5})
    /// 对不上,实测被服务端风控拦成空响应 —— 换成标准格式后同一网络直接 200。</summary>
    public string EncryptEapi(string urlPath, IReadOnlyDictionary<string, object?> payload)
    {
        var text = SerializeEapiPayload(payload);
        var message = $"nobody{urlPath}use{text}md5forencrypt";
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        var body = $"{urlPath}-36cd479b6b5-{text}-36cd479b6b5-{digest}";
        return AesEcbEncryptToHex(body, EapiKey);
    }

    /// <summary>把 eapi 接口参数(含内嵌 header)序列化为紧凑 JSON。
    /// 手写序列化与 weapi 一致:NativeAOT 兼容、字典项恒写、值仅基本类型/嵌套字典。</summary>
    public string SerializeEapiPayload(IReadOnlyDictionary<string, object?> payload)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteObject(writer, payload);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>手写紧凑 JSON 序列化 Dictionary(NativeAOT 兼容,替代 JsonSerializer 反射;payload 值仅基本类型)。
    /// 输出与 Node JSON.stringify / 原 JsonSerializer 一致:紧凑无空白、字典项恒写。</summary>
    private static string SerializeObject(IReadOnlyDictionary<string, object?> obj)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in obj)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteObject(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> obj)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in obj)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case double d: writer.WriteNumberValue(d); break;
            case IReadOnlyDictionary<string, object?> nested: writer.WriteStartObject(); WriteNested(writer, nested); writer.WriteEndObject(); break;
            default: throw new InvalidOperationException($"payload 不支持的值类型 {value.GetType().Name}");
        }
    }

    private static void WriteNested(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> obj)
    {
        foreach (var (key, value) in obj)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }
    }

    private static string AesCbcEncryptToBase64(string text, string key, string iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Encoding.UTF8.GetBytes(iv);
        using var enc = aes.CreateEncryptor();
        var data = Encoding.UTF8.GetBytes(text);
        var result = enc.TransformFinalBlock(data, 0, data.Length);
        return Convert.ToBase64String(result);
    }

    private static string AesEcbEncryptToHex(string text, string key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(key);
        using var enc = aes.CreateEncryptor();
        var data = Encoding.UTF8.GetBytes(text);
        var result = enc.TransformFinalBlock(data, 0, data.Length);
        // eapi 请求体是大写十六进制(与真实客户端一致)
        return Convert.ToHexString(result);
    }

    private static string CreateSecretKey(int length)
    {
        var span = new char[length];
        Random.Shared.GetItems(SecretKeyChars, span);
        return new string(span);
    }

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}
