using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.Lrc;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

/// <summary>逐字歌词端到端验证(--yrc [songId...] [--after-search]):走真实 NetEaseApiClient 的
/// weapi/song/lyric/v1,检查 yrc 是否返回、能否解析、字级时间能否按行贴到 LRC 行上。
/// 实测有逐字的样本:1901371647 孤勇者 / 2057534370 笼 / 1974443814 我记得 / 1811921555 星辰大海。
/// 判据:有 yrc 的歌,合并后的 HasWordTiming 行数 &gt; 0 且没有 End&lt;Start;没有 yrc 只算覆盖情况,不算失败。
///
/// <para>`--after-search` 先跑一次 <c>SearchAsync</c> 再取歌词,复现真实 App 的顺序:
/// 搜索会置位全局 <c>_wafBlocked</c>,而逐字只有加密通道给。2026-11-18 的 "孤勇者没效果"
/// 就是这个组合造成的,回归时必须带上这个开关跑一遍。</para></summary>
public static class YrcProbe
{
    private static readonly long[] DefaultIds = [1901371647, 2057534370, 1974443814, 1811921555];

    public static async Task<int> RunAsync(string[] rawArgs)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var afterSearch = rawArgs.Contains("--after-search");
        var rawIds = rawArgs.Where(a => a != "--after-search").ToArray();
        var ids = rawIds.Length > 0
            ? rawIds.Select(long.Parse).ToArray()
            : DefaultIds;

        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), new CookieStore());
        if (afterSearch)
        {
            // 走真实 App 的顺序:先搜索(会把全局 _wafBlocked 置位),再取歌词。
            var warm = await api.SearchAsync("孤勇者", 3).ConfigureAwait(false);
            Console.WriteLine($"[yrc] 预热搜索返回 {warm.Count} 首");
        }
        var proxy = ALyricEase.Services.Network.NetworkProxy.Instance.GetProxy(new Uri("https://music.163.com"));
        Console.WriteLine($"[yrc] proxy={(proxy is null ? "direct" : proxy.ToString())}");
        var failed = 0;
        var withYrc = 0;

        foreach (var id in ids)
        {
            Console.WriteLine($"\n[yrc] ===== id={id} =====");
            try
            {
                var lrc = await api.GetLyricAsync(id).ConfigureAwait(false);
                if (lrc is null || string.IsNullOrEmpty(lrc.Original))
                {
                    Console.WriteLine("[yrc] 没有歌词");
                    failed++;
                    continue;
                }

                var yrcLines = YrcParser.Parse(lrc.Yrc);
                var doc = LrcParser.Parse(lrc.Original, lrc.Translation, lrc.Yrc);
                var timed = doc.Lines.Where(l => l.HasWordTiming).ToList();
                var wordCount = timed.Sum(l => l.Words!.Count);

                Console.WriteLine(
                    $"[yrc] lrc={doc.Lines.Count} 行, yrc={yrcLines.Count} 行, " +
                    $"合并成功={timed.Count} 行 / {wordCount} 字");
                if (yrcLines.Count > 0) withYrc++;

                if (timed.Count == 0) continue;

                var sample = timed[0];
                Console.WriteLine($"[yrc] 样本行 “{sample.Original}”");
                foreach (var w in sample.Words!.Take(6))
                    Console.WriteLine($"[yrc]    ({w.StartMs,6}→{w.EndMs,6}) “{w.Text}”");

                var bad = timed.SelectMany(l => l.Words!).Count(w => w.EndMs < w.StartMs);
                Console.WriteLine(bad == 0
                    ? "[yrc] PASS: 字级时间全部有效"
                    : $"[yrc] FAIL: {bad} 个字 End<Start");
                if (bad > 0) failed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[yrc] FAIL: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine($"\n[yrc] 汇总: {ids.Length} 首, 有 yrc {withYrc} 首, 失败 {failed}");
        return failed == 0 ? 0 : 1;
    }
}
