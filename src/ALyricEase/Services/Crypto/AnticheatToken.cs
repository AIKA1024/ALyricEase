using System.Security.Cryptography;
using System.Text;

namespace ALyricEase.Services.Crypto;

/// <summary>网易云 x-anticheattoken 防作弊 token(官方 PC 客户端写类端点校验,算法移植自
/// multiPlatformMusicApi 的 checkToken.js):16B 缓冲 E 由 8B 随机数与 8B 大端时间戳按
/// 半字节交错拼成,E 取 hex 拼盐后 MD5 得 8B 校验头,base64 拼接为 b;JSON{r,d,b} 按
/// 6B 密钥逐字节 XOR 取补(256-v)输出小写 hex。</summary>
public static class AnticheatToken
{
    private const string Salt = "dAWsBhCqtOaNLLJ25hBzWbqWXwiK99Wd";
    private const string DeviceWeapi = "dyRmOyIM4HJEUVQUVBLGhjpWetksmiyy";
    private const string DeviceEapi = "jiEABHxUxtVBFBVEQFKSgnoWYHsMEqAV";
    private const long K32 = 4294967296L; // 2^32

    private static readonly byte[] Key = [31, 125, 244, 60, 32, 48];

    /// <summary>生成 token:type 为 "weapi" 或 "eapi"(仅设备指纹 d 字段不同)。</summary>
    public static string Generate(string type)
    {
        var d = type == "eapi" ? DeviceEapi : DeviceWeapi;
        // 与 Node JSON.stringify({"r":1,"d":d,"b":bc()}) 逐字节一致(紧凑、键序固定)
        var json = "{\"r\":1,\"d\":\"" + d + "\",\"b\":\"" + BuildB() + "\"}";
        return Transform(json);
    }

    /// <summary>b = base64(md5(hex(E)+salt) 前 8B || E),去尾部 '='。</summary>
    private static string BuildB()
    {
        var e = BuildE(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RandomNumberGenerator.GetBytes(8));
        var hexE = Convert.ToHexString(e).ToLowerInvariant();
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(hexE + Salt));
        var combined = new byte[8 + e.Length];
        Buffer.BlockCopy(md5, 0, combined, 0, 8);
        Buffer.BlockCopy(e, 0, combined, 8, e.Length);
        return Convert.ToBase64String(combined).TrimEnd('=');
    }

    /// <summary>16B E:偶数位放随机/时间戳字节的高位半字节,奇数位放低位半字节(g = l/2 对应第 g 字节)。</summary>
    private static byte[] BuildE(long now, byte[] rand)
    {
        var hi = (uint)(now / K32);
        var lo = (uint)(now % K32);
        var c = new byte[8];
        c[0] = (byte)(hi >> 24); c[1] = (byte)(hi >> 16); c[2] = (byte)(hi >> 8); c[3] = (byte)hi;
        c[4] = (byte)(lo >> 24); c[5] = (byte)(lo >> 16); c[6] = (byte)(lo >> 8); c[7] = (byte)lo;

        var e = new byte[16];
        for (var l = 0; l < 16; l++)
        {
            var g = l >> 1;
            e[l] = (l & 1) == 0
                ? (byte)(
                    ((rand[g] & 0x10) >> 4) | ((rand[g] & 0x20) >> 3) | ((rand[g] & 0x40) >> 2) | ((rand[g] & 0x80) >> 1) |
                    ((c[g] & 0x10) >> 3) | ((c[g] & 0x20) >> 2) | ((c[g] & 0x40) >> 1) | ((c[g] & 0x80) >> 0))
                : (byte)(
                    ((rand[g] & 0x01) << 0) | ((rand[g] & 0x02) << 1) | ((rand[g] & 0x04) << 2) | ((rand[g] & 0x08) << 3) |
                    ((c[g] & 0x01) << 1) | ((c[g] & 0x02) << 2) | ((c[g] & 0x04) << 3) | ((c[g] & 0x08) << 4));
        }
        return e;
    }

    /// <summary>逐字节 XOR 密钥后取 8 位环绕相反数(256-v),输出小写 hex(输入恒为 ASCII,无 %xx 转义)。</summary>
    private static string Transform(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var outb = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var v1 = (byte)(bytes[i] ^ Key[i % Key.Length]);
            outb[i] = (byte)((256 - v1) & 0xFF);
        }
        return Convert.ToHexString(outb).ToLowerInvariant();
    }
}
