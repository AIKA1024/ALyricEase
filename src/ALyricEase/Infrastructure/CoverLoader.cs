using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ALyricEase.Infrastructure;

/// <summary>远程封面图加载:内存缓存(按解码字节预算,超限按最近使用顺序淘汰)+ 后台解码,失败返回 null(不抛异常)。
/// 返回的 Bitmap 不可变,可跨线程使用。
/// 预算按字节而非张数:播放条/正在播放页的大封面(640px≈1.6MB)也进同一缓存,
/// 若按张数 300 上限会堆到 ~480MB;缓存自身按字节与条目数双重有界。
/// 同一规格 URL 的并发请求共享一个在途任务,避免列表同时实化时重复下载与解码。</summary>
public static class CoverLoader
{
    /// <summary>缓存总解码字节预算(~48MB):歌曲行 100px≈40KB 能存上千张,640px 大封面只留 ~30 张。</summary>
    private const long MaxCacheBytes = 48L * 1024 * 1024;

    /// <summary>条目数兜底:失败项不占像素预算,仍必须限制其 URL/字典节点数量。</summary>
    private const int MaxCacheEntries = 1536;

    /// <summary>失败结果只短暂缓存:防离线/瞬时失败时每行反复请求,同时允许网络恢复后自动重试。</summary>
    private static readonly TimeSpan FailedEntryLifetime = TimeSpan.FromMinutes(2);

    /// <summary>并发下载上限:首页/列表一次会触发几十张封面,不限流会把慢网连接占满、
    /// 拖慢同批 API 请求与封面填图速度(实测 30 并发 vs 6 并发,填满时间差不大但整体稳定)。</summary>
    private const int MaxConcurrentDownloads = 6;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new();
    private static readonly LinkedList<string> LruOrder = new();
    private static readonly Dictionary<string, Task<IImage?>> InFlight = new();
    private static readonly SemaphoreSlim DownloadGate = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private static long _cacheBytes;

    private sealed record CacheEntry(
        IImage? Image,
        long Cost,
        DateTimeOffset? ExpiresAt,
        LinkedListNode<string> Node);

    /// <summary>加载封面。size>0 时请求对应缩略图(param=WxH),列表项务必用 ~100 的小图,避免全尺寸大图撑爆内存。</summary>
    public static Task<IImage?> LoadAsync(string url, int size = 0)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<IImage?>(null);
        if (size > 0) url = BuildSizedUrl(url, size);

        TaskCompletionSource<IImage?>? owner = null;
        Task<IImage?> sharedTask;
        lock (CacheGate)
        {
            if (TryGetCachedLocked(url, out var hit))
                return Task.FromResult(hit);

            if (InFlight.TryGetValue(url, out sharedTask!))
                return sharedTask;

            owner = new TaskCompletionSource<IImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            sharedTask = owner.Task;
            InFlight.Add(url, sharedTask);
        }

        _ = DownloadAndPublishAsync(url, owner!);
        return sharedTask;
    }

    /// <summary>single-flight 的唯一生产者:完成时把同一个 Bitmap 发布给所有等待方。</summary>
    private static async Task DownloadAndPublishAsync(string url, TaskCompletionSource<IImage?> completion)
    {
        IImage? result = null;
        var succeeded = false;
        try
        {
            await DownloadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                var bitmap = await Task.Run(() =>
                {
                    using var ms = new MemoryStream(bytes);
                    return new Bitmap(ms);
                }).ConfigureAwait(false);
                result = bitmap;
                succeeded = true;
            }
            finally
            {
                DownloadGate.Release();
            }
        }
        catch
        {
            // 失败由下方写入短期负缓存并统一发布 null。
        }

        lock (CacheGate)
        {
            if (succeeded && result is not null)
            {
                var cost = EstimateBytes(result);
                // 单张超过预算不进缓存,但本次并发等待方仍共享同一个实例。
                if (cost <= MaxCacheBytes)
                    AddOrReplaceLocked(url, result, cost, expiresAt: null);
            }
            else
            {
                AddOrReplaceLocked(url, image: null, cost: 0,
                    expiresAt: DateTimeOffset.UtcNow + FailedEntryLifetime);
            }

            // Continuation 异步调度,可在锁内先发布结果再移除在途项,杜绝完成/移除之间的新请求窗口。
            completion.TrySetResult(result);
            if (InFlight.TryGetValue(url, out var current) && ReferenceEquals(current, completion.Task))
                InFlight.Remove(url);
        }
    }

    private static bool TryGetCachedLocked(string url, out IImage? image)
    {
        if (!Cache.TryGetValue(url, out var entry))
        {
            image = null;
            return false;
        }

        if (entry.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            RemoveLocked(url, entry);
            image = null;
            return false;
        }

        // 命中即移到末尾,缓存按最近使用顺序淘汰。
        LruOrder.Remove(entry.Node);
        LruOrder.AddLast(entry.Node);
        image = entry.Image;
        return true;
    }

    private static void AddOrReplaceLocked(string url, IImage? image, long cost, DateTimeOffset? expiresAt)
    {
        if (Cache.TryGetValue(url, out var existing))
            RemoveLocked(url, existing);

        var node = LruOrder.AddLast(url);
        Cache.Add(url, new CacheEntry(image, cost, expiresAt, node));
        _cacheBytes += cost;

        while ((_cacheBytes > MaxCacheBytes || Cache.Count > MaxCacheEntries)
               && LruOrder.First is { } oldest)
        {
            var oldestUrl = oldest.Value;
            if (Cache.TryGetValue(oldestUrl, out var entry))
                RemoveLocked(oldestUrl, entry);
            else
                LruOrder.RemoveFirst();
        }
    }

    private static void RemoveLocked(string url, CacheEntry entry)
    {
        Cache.Remove(url);
        LruOrder.Remove(entry.Node);
        _cacheBytes -= entry.Cost;
    }

    /// <summary>解码像素占用估计(StridePixelFormats.Bgra8888 = 4 字节/像素)。</summary>
    private static long EstimateBytes(IImage? img) =>
        img is Bitmap b ? (long)b.PixelSize.Width * b.PixelSize.Height * 4 : 0;

    /// <summary>QQ 图床支持的固定尺寸档位(任意尺寸如 R100x100 会 404),取 ≥ 目标的最小档。</summary>
    private static readonly int[] QqCoverSizes = [150, 300, 500, 800, 1200, 1500];

    /// <summary>按目标尺寸改写封面 URL:网易云追加 param=WxH;
    /// QQ 音乐(y.gtimg.cn)改写路径里的 R{w}x{h} 尺寸段(仅固定档位有效,就近向上取档)。</summary>
    public static string BuildSizedUrl(string url, int size)
    {
        if (url.Contains("y.gtimg.cn", StringComparison.OrdinalIgnoreCase))
        {
            var snapped = QqCoverSizes.FirstOrDefault(s => s >= size);
            return System.Text.RegularExpressions.Regex.Replace(
                url, @"R\d+x\d+M000", $"R{snapped}x{snapped}M000");
        }
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}param={size}y{size}";
    }
}
