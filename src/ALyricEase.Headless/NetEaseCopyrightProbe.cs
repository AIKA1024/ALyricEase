using System;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

/// <summary>专辑可播性预判验证探针(--necopyright [albumId]):走生产路径 GetAlbumAsync,
/// 打印映射后每首 Song 的 IsNoCopyright/IsPlayable 预判。验证"无版权专辑(如周杰伦)进页即灰"。
/// 对照样本用 18893(依然范特西,全灰);不给 id 时默认它。</summary>
public static class NetEaseCopyrightProbe
{
    public static async Task<int> RunAsync(long albumId)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new NetEaseApiClient(new ALyricEase.Services.Crypto.CryptoService(), new CnIpPool(), new CookieStore());
        var detail = await api.GetAlbumAsync(albumId);
        var songs = detail.Songs;
        Console.WriteLine($"[necopyright] 专辑 [{detail.Info.Name}] id={albumId}: {songs.Count} 首");
        var blocked = 0;
        foreach (var s in songs)
        {
            var gray = s.IsNoCopyright;
            if (gray) blocked++;
            Console.WriteLine($"  {s.Id,-12} IsNoCopyright={gray,-6} Fee={s.Fee,-3} {s.Name}");
        }
        Console.WriteLine($"[necopyright] 无版权(进页即灰): {blocked}/{songs.Count}" +
                          (albumId == 18893 ? $" (期望: {songs.Count}/{songs.Count})" : ""));
        if (albumId == 18893)
            return blocked == songs.Count && songs.Count > 0 ? 0 : 1;
        return 0;
    }
}
