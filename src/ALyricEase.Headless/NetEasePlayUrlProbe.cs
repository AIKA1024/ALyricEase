using System.Globalization;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

/// <summary>eapi 播放地址端到端验证(--neplayurl [songId] [level]):走真实 NetEaseApiClient
/// (三段式 eapi + Android 身份 + interface 网关),打印服务端实际返回的音质档位。
/// 用于核对"请求 lossless 是否拿到 lossless"——即加密通道与客户端权益是否都通了。</summary>
public static class NetEasePlayUrlProbe
{
    public static async Task<int> RunAsync(long songId, string level)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var store = new CookieStore();
        Console.WriteLine(
            $"[neplayurl] id={songId} level={level} MUSIC_U={CookieStore.Mask(store.MusicU)}");
        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), store);
        try
        {
            var item = await api.GetPlayUrlAsync(songId, level).ConfigureAwait(false);
            if (item is null || string.IsNullOrEmpty(item.Url))
            {
                Console.Error.WriteLine("[neplayurl] FAIL: 未取到播放地址");
                return 1;
            }

            Console.WriteLine(
                $"[neplayurl] 实际 level={item.Level} br={item.Br} fee={item.Fee} " +
                $"isTrial={item.IsTrial}");
            Console.WriteLine(
                $"[neplayurl] url={item.Url[..Math.Min(80, item.Url.Length)]}...");
            var servedRank = MusicCacheService.GetQualityRank(MusicSource.NetEase, item.Level);
            var wantedRank = MusicCacheService.GetQualityRank(MusicSource.NetEase, level);
            Console.WriteLine(
                servedRank >= wantedRank
                    ? $"[neplayurl] PASS: 拿到目标档位(rank {servedRank} >= {wantedRank})"
                    : $"[neplayurl] WARN: 服务端只给了更低档位(rank {servedRank} < {wantedRank})" +
                      " —— 通道已通,权益不足(常见原因:VIP 到期)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[neplayurl] FAIL: {ex.Message}");
            return 1;
        }
    }
}
