using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;

namespace ALyricEase.Headless;

/// <summary>
/// 档位上限记忆探针(--quality-ceiling)。
///
/// 背景:用户设置 Hi-Res 而歌曲最高只有 320k 时,每次播放都要走
/// hires → lossless → exhigh 的串行降级链(本机实测 500-900ms),问完之后又因为
/// "服务端档位不高于已有缓存"改播本地文件 —— 那一秒完全是白花的。
///
/// 记忆的语义是"**这首歌在服务端最多只肯给到哪一档**",只在观察到
/// "请求更高档、返回更低档"时写入。本探针覆盖:
///   1. 无记忆时请求档原样透传(旧行为不变)
///   2. 降级证据落盘、跨实例仍在
///   3. 夹档后直接命中本地缓存(核心收益:零取址往返)
///   4. 两条反例:请求档 == 返回档、返回档更高,都不能写成上限
///   5. QQ 的字母档位
///   6. TTL 过期、换号清空、音频淘汰后的孤儿记忆清理
/// </summary>
internal static class QualityCeilingProbe
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aly-quality-ceiling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            Log($"[quality-ceiling] {(ok ? "OK  " : "FAIL")} {name}" +
                (detail.Length > 0 ? " — " + detail : ""));
        }

        try
        {
            using var http = new HttpClient();
            var netEaseSong = new Song { Id = 2611744362, Source = MusicSource.NetEase, Name = "探针" };
            var qqSong = new Song { Id = 0, Mid = "003probe", Source = MusicSource.QQ, Name = "探针QQ" };

            var netEaseKey = SongKey(netEaseSong);
            var audioPath = Path.Combine(root, $"a-{netEaseKey}-03.mp3");
            await File.WriteAllBytesAsync(audioPath, new byte[64]);

            // --- 1. 没有记忆:旧行为原样保留 ---
            var cache = new MusicCacheService(512, root, http);
            Check("无记忆时请求档原样透传",
                cache.ClampToServedQualityCeiling(netEaseSong, "hires") == "hires");
            Check("无记忆时 Hi-Res 请求打不中 320k 缓存",
                cache.TryAcquire(netEaseSong, "hires") is null,
                "这就是每次播放要串行问三遍的直接原因");

            // --- 2. 服务端降级 → 记账并落盘 ---
            cache.NoteServedQualityCeiling(netEaseSong, "hires", "exhigh");
            await Task.Delay(200); // 落盘是 fire-and-forget
            Check("降级后立即可读到上限", cache.GetServedQualityCeiling(netEaseSong) == "exhigh");
            Check("记忆已落盘",
                File.Exists(Path.Combine(root, $"q-{netEaseKey}.probe")));

            // --- 3. 夹档语义 ---
            Check("Hi-Res 请求被夹到 exhigh",
                cache.ClampToServedQualityCeiling(netEaseSong, "hires") == "exhigh");
            Check("无损请求被夹到 exhigh",
                cache.ClampToServedQualityCeiling(netEaseSong, "lossless") == "exhigh");
            Check("标准请求不被夹",
                cache.ClampToServedQualityCeiling(netEaseSong, "standard") == "standard");

            // --- 4. 核心收益:夹档后直接命中本地缓存,本次起播零取址往返 ---
            var lease = cache.TryAcquire(netEaseSong,
                cache.ClampToServedQualityCeiling(netEaseSong, "hires"));
            Check("夹档后 320k 缓存直接命中", lease is not null, "起播不再发任何取址请求");
            lease?.Dispose();

            // --- 5. 重启(新实例)后记忆仍在 ---
            var reloaded = new MusicCacheService(512, root, http);
            Check("新实例仍读得到上限", reloaded.GetServedQualityCeiling(netEaseSong) == "exhigh");

            // --- 6. 反例一:请求档 == 返回档,什么也证明不了 ---
            reloaded.NoteServedQualityCeiling(qqSong, "M800", "M800");
            Check("同档不算上限证据 → 不写", reloaded.GetServedQualityCeiling(qqSong) is null);

            // --- 7. 反例二:服务端返回了更高的档 ---
            reloaded.NoteServedQualityCeiling(qqSong, "M500", "F000");
            Check("返回更高档不算上限 → 不写", reloaded.GetServedQualityCeiling(qqSong) is null);

            // --- 7b. 档位未知(level 空、br 0)不能记成"降到标准" ---
            reloaded.NoteServedQualityCeiling(qqSong, "RS01", "");
            Check("空档位不记账", reloaded.GetServedQualityCeiling(qqSong) is null);

            // --- 7c. 反推档位必须落在取址降级链认识的档位里 ---
            // jymaster 不在 NetEase 的 QualityFallbacks 里,会掉进 `_ => ["standard"]` 只问标准档。
            Check("rank 6 反推回落到 hires",
                MusicCacheService.GetQualityLevelForRank(MusicSource.NetEase, 6) == "hires");
            Check("rank 5 反推是 hires",
                MusicCacheService.GetQualityLevelForRank(MusicSource.NetEase, 5) == "hires");
            Check("QQ rank 5 反推是 RS01",
                MusicCacheService.GetQualityLevelForRank(MusicSource.QQ, 5) == "RS01");

            // --- 8. QQ 字母档位 ---
            reloaded.NoteServedQualityCeiling(qqSong, "RS01", "M800");
            await Task.Delay(200);
            var qqReloaded = new MusicCacheService(512, root, http);
            Check("QQ 档位记忆可落盘", qqReloaded.GetServedQualityCeiling(qqSong) == "M800");
            Check("QQ 夹档生效", qqReloaded.ClampToServedQualityCeiling(qqSong, "RS01") == "M800");

            // --- 9. TTL 过期后失效(会员到期/版权变动靠它兜底) ---
            var probePath = Path.Combine(root, $"q-{netEaseKey}.probe");
            var expiredAt = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            await File.WriteAllTextAsync(probePath, $"exhigh|{expiredAt}");
            var expiredCache = new MusicCacheService(512, root, http);
            Check("过期记忆不再夹档",
                expiredCache.GetServedQualityCeiling(netEaseSong) is null);

            // --- 10. 换号清空 ---
            expiredCache.ClearServedQualityCeilings();
            Check("换号后 q 文件被删除", !File.Exists(probePath));

            // --- 11. 裁剪只清过期记忆;**不能**按"有没有音频文件"清 ---
            // 记忆的价值场景之一正是"这首歌没缓存过、但已知上限",按音频存在与否删会让这种记忆
            // 写下就消失(本探针第一版就踩到了:QQ 那首没有音频,记忆被下一次构造裁剪直接删掉)。
            var orphanSong = new Song { Id = 1001, Source = MusicSource.NetEase };
            var orphanKey = SongKey(orphanSong);
            var orphanCache = new MusicCacheService(512, root, http);
            orphanCache.NoteServedQualityCeiling(orphanSong, "hires", "exhigh");
            await Task.Delay(200);
            var liveProbe = Path.Combine(root, $"q-{orphanKey}.probe");
            Check("前置:无音频文件的记忆已写入", File.Exists(liveProbe));
            await orphanCache.SetMaximumSizeMbAsync(512); // force trim
            Check("无音频但未过期 → 记忆保留", File.Exists(liveProbe),
                "否则冷启动后的第一次探测等于白写");

            var staleSong = new Song { Id = 1002, Source = MusicSource.NetEase };
            var staleKey = SongKey(staleSong);
            var staleProbe = Path.Combine(root, $"q-{staleKey}.probe");
            await File.WriteAllTextAsync(staleProbe,
                $"exhigh|{DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()}");
            await orphanCache.SetMaximumSizeMbAsync(512);
            Check("过期记忆被裁剪清掉", !File.Exists(staleProbe));

            Log($"[quality-ceiling] 结论: {(failures == 0 ? "全部通过" : $"{failures} 项失败")}");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>与 MusicCacheService.BuildSongKey 同构,否则记忆和缓存文件对不上。</summary>
    private static string SongKey(Song song) => Hash(song.Id != 0
        ? $"{(int)song.Source}:id:{song.Id}"
        : $"{(int)song.Source}:mid:{song.Mid}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Log(string message) => Console.WriteLine(message);
}
