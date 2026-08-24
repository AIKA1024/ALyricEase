using System.Globalization;
using System.Numerics;
using System.Text;

namespace ALyricEase.Services.Crypto;

/// <summary>网易云 weapi 的自定义 RSA:非 PKCS#1/OAEP,而是把明文字节当大整数做
/// 模幂后左侧补 0 到 128 字节输出 hex。标准 RSACryptoServiceProvider 不支持,
/// 必须 BigInteger.ModPow 手写。</summary>
internal static class RsaPadding
{
    public static readonly BigInteger E = BigInteger.Parse("010001", NumberStyles.AllowHexSpecifier);

    public static readonly BigInteger N = BigInteger.Parse(
        "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b" +
        "3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda925" +
        "57c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3" +
        "e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8",
        NumberStyles.AllowHexSpecifier);

    /// <summary>把 UTF-8 文本(已是反转后的 secretKey)加密为 256 位小写 hex(128 字节补 0)。</summary>
    public static string Encrypt(string text)
    {
        var m = new BigInteger(Encoding.UTF8.GetBytes(text), isUnsigned: true, isBigEndian: true);
        var c = BigInteger.ModPow(m, E, N);
        return c.ToString("x2").PadLeft(256, '0');
    }
}
