using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;

namespace ALyricEase.Headless;

/// <summary>
/// 封面落盘成本探针(--cache-write-cost):量"每写一张封面"到底花了多少时间,以及这个时间
/// 是否随缓存目录里的文件数放大。
///
/// 背景(用户报告):歌单页快速滚到底后停止滚动,"封面加载特别慢,CPU 一直 10% 左右"。
/// 读代码可见写路径 <c>StoreBytesAsync → EnsureSpaceFor</c> 里 **无条件** 调用
/// <c>GetEvictionCandidates()</c>,而它会枚举整个缓存目录、为每个文件建 FileInfo、
/// 再按 LastWriteTimeUtc 排序 —— 真实用户缓存目录有上万个文件(本机实测一万三千余个),
/// 也就是说**每张封面落盘都要 stat 整个缓存目录两遍**。这条路径全程持 <c>_mutationGate</c>,
/// 于是并发的封面下载被串行化 ⇒ 封面出得慢 + 后台线程 CPU 恒定占用。
///
/// 本探针用**临时目录**(不碰用户真实缓存,避免触发 LRU 删除用户的音频缓存)构造
/// "少量文件"与"上万个文件"两个同口径环境,量同一批封面写入的耗时差。
///
/// ALY_CACHE_PROBE_FILES 控制大目录文件数(默认 13000),ALY_CACHE_PROBE_WRITES 控制写入张数(默认 30)。
/// </summary>
internal static class CacheWriteCostProbe
{
    public static async Task<int> RunAsync()
    {
        var largeCount = EnvInt("ALY_CACHE_PROBE_FILES", 13_000);
        var writes = EnvInt("ALY_CACHE_PROBE_WRITES", 30);
        var smallCount = 50;

        Log($"[cache-write] 口径: 目录文件数 {smallCount} vs {largeCount}, 每条目录写 {writes} 张封面(每张约 40KB)");
        ReportRealCacheContext();

        var smallRoot = CreateSparseCacheDirectory(smallCount);
        var largeRoot = CreateSparseCacheDirectory(largeCount);
        try
        {
            using var http = new HttpClient();
            var small = new MusicCacheService(512, smallRoot, http);
            var large = new MusicCacheService(512, largeRoot, http);

            var smallResult = await MeasureAsync(small, smallCount, writes);
            var largeResult = await MeasureAsync(large, largeCount, writes);

            Log($"[cache-write] 顺序写入: {smallCount} 文件目录 中位数={smallResult.MedianPerWriteMs:F1}ms " +
                $"合计={smallResult.TotalMs:F0}ms");
            Log($"[cache-write] 顺序写入: {largeCount} 文件目录 中位数={largeResult.MedianPerWriteMs:F1}ms " +
                $"合计={largeResult.TotalMs:F0}ms");
            var ratio = smallResult.MedianPerWriteMs > 0
                ? largeResult.MedianPerWriteMs / smallResult.MedianPerWriteMs
                : double.NaN;
            Log($"[cache-write] 判定: 每写一张封面慢 {ratio:F0} 倍 " +
                $"⇒ {(ratio > 10 ? "写路径成本随目录规模放大(EnsureSpaceFor 全目录扫描)" : "写路径与目录规模无关")}");

            // 并发 6 路 = 封面管线的真实并发度(MaxConnectionsPerServer=6)。写路径持全局锁,
            // 所以并发本就不会超过串行 —— 有意义的是"单次写多大",它决定了这段串行的总时长。
            var concurrent = await MeasureConcurrentAsync(large, largeCount, writes);
            var serialExpected = writes * largeResult.MedianPerWriteMs;
            Log($"[cache-write] 并发 6 路写入 {largeCount} 文件目录: 合计={concurrent:F0}ms " +
                $"(串行预期≈{serialExpected:F0}ms) " +
                $"⇒ {(largeResult.MedianPerWriteMs > 20 ? "写锁让并发退化为串行,总时长由单次写成本决定" : "单次写已足够快,串行不影响体验")}");

            // 读路径对照:封面命中缓存时不该付这份成本(读已脱离 _mutationGate)。
            var readWatch = Stopwatch.StartNew();
            for (var index = 0; index < writes; index++)
                _ = await large.TryGetCoverAsync($"https://p1.music.126.net/probe-read-{index}.jpg?param=100y100");
            readWatch.Stop();
            Log($"[cache-write] 读路径对照: {writes} 次未命中读取合计={readWatch.ElapsedMilliseconds}ms " +
                $"(未命中应是纯 File.Exists,≈0)");

            await VerifyAccountingAsync(writes);
            return 0;
        }
        finally
        {
            TryDelete(smallRoot);
            TryDelete(largeRoot);
        }
    }

    /// <summary>快路径的正确性不能只看"变快了":工作副本一旦与真实目录偏离,快路径就失效
    /// (偏小会让缓存超上限、偏大只是多扫一次)。这里逐条核对:
    ///   1) 写入后 工作副本 == 真实扫描值;
    ///   2) 覆盖写(p-*.json 这类会被反复重写的列表文件)之后仍然相等;
    ///   3) 接近上限时**仍然真的会淘汰** —— 真实目录不得超过容量上限。</summary>
    private static async Task VerifyAccountingAsync(int writes)
    {
        using var http = new HttpClient();
        var root = Path.Combine(Path.GetTempPath(), $"aly-cache-account-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 铺底:130 个 1MB 文件,把 128MB(允许的最小上限)填到临界,逼出"必须淘汰"的慢路径。
            var filler = new byte[1024 * 1024];
            var filled = 0;
            for (var index = 0; index < 130; index++)
            {
                File.WriteAllBytes(Path.Combine(root, $"a-fill-{index:D4}-99.mp3"), filler);
                filled++;
            }
            var cache = new MusicCacheService(128, root, http);
            await Task.Delay(300); // 等构造函数里那次后台裁剪落地并校准

            var payload = new byte[40 * 1024];
            Random.Shared.NextBytes(payload);
            for (var index = 0; index < writes; index++)
                await cache.CacheCoverAsync($"https://p1.music.126.net/probe-acc-{index}.jpg?param=100y100", payload);

            var tracked = TrackedCacheBytes(cache);
            var actual = cache.GetCurrentSizeBytes();
            var toleranceKb = 8;
            Log($"[cache-write] 记账核对(铺底 {filled}MB): 工作副本={tracked / 1024}KB 真实={actual / 1024}KB " +
                $"⇒ {((Math.Abs(tracked - actual) <= toleranceKb * 1024) ? "一致" : "❌ 偏离")}");

            var maximum = 128L * 1024 * 1024;
            Log($"[cache-write] 淘汰仍然生效核对: 目录={actual / 1024 / 1024}MB 上限={maximum / 1024 / 1024}MB " +
                $"⇒ {(actual <= maximum ? "未超上限" : "❌ 超上限")}");

            // 覆盖写(p-*.tracks 这类会被反复重写的列表文件)必须按"新长度 - 旧长度"记账,
            // 而不是整份新长度累加 —— 否则工作副本会随每次重写一路膨胀。
            var playlist = new Playlist { Id = 4242, Source = MusicSource.NetEase, Name = "记账探针" };
            await cache.CachePlaylistTracksAsync(playlist, MakeSongs(10, 990_000));
            await cache.CachePlaylistTracksAsync(playlist, MakeSongs(40, 990_000));
            var trackedAfterRewrite = TrackedCacheBytes(cache);
            var actualAfterRewrite = cache.GetCurrentSizeBytes();
            Log($"[cache-write] 覆盖写后核对: 工作副本={trackedAfterRewrite / 1024}KB 真实={actualAfterRewrite / 1024}KB " +
                $"⇒ {(Math.Abs(trackedAfterRewrite - actualAfterRewrite) <= toleranceKb * 1024 ? "一致" : "❌ 偏离")}");

            // 清空后必须回到"未知"(被钉住的文件仍在磁盘上),随后被真实扫描重新校准。
            await cache.ClearAsync();
            Log($"[cache-write] 清空后工作副本={TrackedCacheBytes(cache)}(期望 -1 = 未知), " +
                $"真实={cache.GetCurrentSizeBytes() / 1024}KB");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<Sample> MeasureAsync(MusicCacheService cache, int fileCount, int writes)
    {
        var samples = new double[writes];
        var payload = new byte[40 * 1024];
        Random.Shared.NextBytes(payload);
        var total = Stopwatch.StartNew();
        for (var index = 0; index < writes; index++)
        {
            var watch = Stopwatch.StartNew();
            await cache.CacheCoverAsync($"https://p1.music.126.net/probe-seq-{index}.jpg?param=100y100", payload);
            watch.Stop();
            samples[index] = watch.Elapsed.TotalMilliseconds;
        }
        total.Stop();
        Log($"[cache-write] {fileCount,6} 文件目录: " +
            string.Join(" ", samples.Take(6).Select(value => value.ToString("F0"))) + " ms …");
        return new Sample(samples, total.Elapsed.TotalMilliseconds);
    }

    private static async Task<double> MeasureConcurrentAsync(MusicCacheService cache, int fileCount, int writes)
    {
        var payload = new byte[40 * 1024];
        Random.Shared.NextBytes(payload);
        var watch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, writes).Select(index => cache.CacheCoverAsync(
            $"https://p1.music.126.net/probe-conc-{index}.jpg?param=100y100", payload)).ToArray();
        await Task.WhenAll(tasks);
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>读只读地报出用户真实缓存目录的规模 —— 本探针绝不写入它(写会触发 LRU 删除真实音频缓存)。</summary>
    private static void ReportRealCacheContext()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ALyricEase", "cache", "music");
            if (!Directory.Exists(directory))
            {
                Log("[cache-write] 真实缓存目录不存在(未登录/未使用过)");
                return;
            }
            var files = Directory.EnumerateFiles(directory).ToList();
            long bytes = 0;
            foreach (var file in files)
            {
                try { bytes += new FileInfo(file).Length; } catch { }
            }
            Log($"[cache-write] 真实缓存目录: {files.Count} 个文件 / {bytes / 1024.0 / 1024.0:F0}MB " +
                $"(只读统计,本探针不写入该目录)");
        }
        catch (Exception ex)
        {
            Log($"[cache-write] 真实缓存目录统计失败: {ex.Message}");
        }
    }

    private static string CreateSparseCacheDirectory(int fileCount)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-cache-cost-{fileCount}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var payload = new byte[20 * 1024];
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < fileCount; index++)
        {
            var path = Path.Combine(root, $"a-probe-{index:D6}-99.mp3");
            File.WriteAllBytes(path, payload);
            // 打散 mtime,让 EnsureSpaceFor 的排序不会退化成"已经有序"这种对自己有利的假象。
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-index)); } catch { }
        }
        watch.Stop();
        Log($"[cache-write] 构造 {fileCount} 文件目录耗时={watch.ElapsedMilliseconds}ms ({root})");
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>读 MusicCacheService 内部的"工作副本"字段。用反射而不是 internal 属性:
    /// 这样同一个探针在**修复前/修复后两个版本**上都能跑(修复前没有这个字段,返回 -1),
    /// A/B 对照可以直接用同一份二进制口径。</summary>
    private static long TrackedCacheBytes(MusicCacheService cache)
    {
        var field = typeof(MusicCacheService).GetField(
            "_knownCacheBytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(cache) is long value ? value : -1;
    }

    private static List<Song> MakeSongs(int count, int baseId) =>
        Enumerable.Range(1, count).Select(index => new Song
        {
            Id = baseId + index,
            Source = MusicSource.NetEase,
            Name = $"记账歌曲 {index:D3}",
            Artist = "探针歌手",
            Album = "探针专辑",
            DurationMs = 180_000,
        }).ToList();

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { }
    }

    private readonly record struct Sample(double[] PerWriteMs, double TotalMs)
    {
        public double MedianPerWriteMs
        {
            get
            {
                var sorted = (double[])PerWriteMs.Clone();
                Array.Sort(sorted);
                return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
            }
        }
    }
}
