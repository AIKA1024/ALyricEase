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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 与 Node 的 JSON.stringify 对齐:紧凑输出、数字/字符串原样
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>weapi 加密,返回 (params, encSecKey),配合 form-urlencoded 提交。</summary>
    public (string Params, string EncSecKey) EncryptWeapi(IReadOnlyDictionary<string, object?> payload)
    {
        var text = JsonSerializer.Serialize(payload, JsonOpts);
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

    /// <summary>eapi 加密,返回单个 params(encSecKey 空串)。
    /// urlPath 为明文里的 /api/... 路径,如 /api/song/enhance/player/url/v1。</summary>
    public string EncryptEapi(string urlPath, IReadOnlyDictionary<string, object?> payload)
    {
        var body = new { method = "POST", url = urlPath, @params = payload };
        var text = JsonSerializer.Serialize(body, JsonOpts);
        var message = "nobody{use}{this}" + text;
        return AesEcbEncryptToHex(message, EapiKey);
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
        return Convert.ToHexString(result).ToLowerInvariant();
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
