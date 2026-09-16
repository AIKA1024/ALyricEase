using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Services;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ALyricEase.Infrastructure;

/// <summary>封面图加载:优先读取统一媒体磁盘缓存，再走网络；解码后另有内存 LRU，失败返回 null(不抛异常)。
/// 返回的 Bitmap 不可变,可跨线程使用。
/// 预算按字节而非张数:当前仅保留播放器等需要直接 IImage 的低频大图；
/// 页面列表与头像统一由 ManagedCoverImage 的租约缓存管理。
/// 同一规格 URL 的并发请求共享一个在途任务,避免列表同时实化时重复下载与解码。</summary>
public static class CoverLoader
{
    /// <summary>低频直接位图缓存预算(桌面 ~16MB / Android 按堆缩放)：约可保留 10 张 640px 大封面。
    /// 与 ManagedCoverImage 的租约缓存共用 ImageMemoryBudget,避免两处预算各写一份。</summary>
    private static readonly long MaxCacheBytes = ImageMemoryBudget.DirectCacheBytes;

    /// <summary>条目数兜底:失败项不占像素预算,仍必须限制其 URL/字典节点数量。</summary>
    private static readonly int MaxCacheEntries = ImageMemoryBudget.DirectCacheItems;

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
    private static int _cacheGeneration;

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

        _ = DownloadAndPublishAsync(url, owner!, Volatile.Read(ref _cacheGeneration));
        return sharedTask;
    }

    /// <summary>读取内存中同一原图的任意已解码尺寸；播放器先用它即时占位，再异步升级大图。</summary>
    public static IImage? TryGetLoadedVariant(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        lock (CacheGate)
            return TryGetVariantFallbackLocked(url, out var image) ? image : null;
    }

    /// <summary>single-flight 的唯一生产者:完成时把同一个 Bitmap 发布给所有等待方。</summary>
    private static async Task DownloadAndPublishAsync(
        string url,
        TaskCompletionSource<IImage?> completion,
        int cacheGeneration)
    {
        IImage? result = null;
        var succeeded = false;
        var usedVariantFallback = false;
        try
        {
            await DownloadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var persistentCache = GetPersistentCache();
                var bytes = persistentCache is null
                    ? null
                    : await persistentCache.TryGetCoverAsync(url).ConfigureAwait(false);

                if (bytes is not null)
                {
                    try
                    {
                        result = await DecodeAsync(bytes).ConfigureAwait(false);
                    }
                    catch
                    {
                        await persistentCache!.RemoveCoverAsync(url).ConfigureAwait(false);
                        bytes = null;
                    }
                }

                if (bytes is null)
                {
                    bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                    result = await DecodeAsync(bytes).ConfigureAwait(false);
                    if (persistentCache is not null)
                        _ = persistentCache.CacheCoverAsync(url, bytes);
                }
                succeeded = true;
            }
            finally
            {
                DownloadGate.Release();
            }
        }
        catch
        {
            // 播放器请求 640px、歌单行请求 100px，尺寸 URL 不同。断网时大图取不到，
            // 回退复用同一原始封面的已解码尺寸，至少保证播放条和详情页不空白。
            lock (CacheGate)
            {
                if (TryGetVariantFallbackLocked(url, out var fallback))
                {
                    result = fallback;
                    succeeded = true;
                    usedVariantFallback = true;
                }
            }
        }

        lock (CacheGate)
        {
            if (succeeded
                && result is not null
                && cacheGeneration == Volatile.Read(ref _cacheGeneration))
            {
                // 变体回退与原缓存项共享同一 Bitmap，不重复计入解码内存预算。
                var cost = usedVariantFallback ? 0 : EstimateBytes(result);
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

    /// <summary>清空解码后的内存封面；视图仍持有的图片对象不会被主动释放。</summary>
    public static void ClearMemoryCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        lock (CacheGate)
        {
            Cache.Clear();
            LruOrder.Clear();
            _cacheBytes = 0;
        }
    }

    private static Task<Bitmap> DecodeAsync(byte[] bytes) => Task.Run(() =>
    {
        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    });

    private static MusicCacheService? GetPersistentCache()
    {
        try
        {
            return ServiceLocator.Provider is null
                ? null
                : ServiceLocator.Get<MusicCacheService>();
        }
        catch
        {
            return null;
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

    private static bool TryGetVariantFallbackLocked(string requestedUrl, out IImage? image)
    {
        var family = GetCoverFamilyKey(requestedUrl);
        KeyValuePair<string, CacheEntry>? best = null;
        foreach (var candidate in Cache)
        {
            if (candidate.Value.Image is null
                || !string.Equals(GetCoverFamilyKey(candidate.Key), family, StringComparison.Ordinal))
                continue;
            if (best is null || candidate.Value.Cost > best.Value.Value.Cost)
                best = candidate;
        }

        if (best is not { } hit)
        {
            image = null;
            return false;
        }

        LruOrder.Remove(hit.Value.Node);
        LruOrder.AddLast(hit.Value.Node);
        image = hit.Value.Image;
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
    private static readonly int[] QqCoverSizes = [90, 120, 150, 300, 500, 800, 1200, 1500];

    /// <summary>按目标尺寸改写封面 URL:网易云追加 param=WxH;
    /// QQ 音乐(y.gtimg.cn)改写路径里的 R{w}x{h} 尺寸段(仅固定档位有效,就近向上取档)。</summary>
    public static string BuildSizedUrl(string url, int size)
    {
        if (url.Contains("y.gtimg.cn", StringComparison.OrdinalIgnoreCase))
        {
            var snapped = QqCoverSizes.FirstOrDefault(s => s >= size);
            return Regex.Replace(
                url, @"R\d+x\d+M000", $"R{snapped}x{snapped}M000");
        }
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}param={size}y{size}";
    }

    /// <summary>移除仅代表缩略图尺寸的部分，识别同一封面的不同请求规格。</summary>
    internal static string GetCoverFamilyKey(string url)
    {
        if (url.Contains("y.gtimg.cn", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(url, @"R\d+x\d+M000", "R0x0M000", RegexOptions.IgnoreCase);

        try
        {
            var uri = new Uri(url);
            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.StartsWith("param=", StringComparison.OrdinalIgnoreCase));
            var builder = new UriBuilder(uri) { Query = string.Join('&', query) };
            return builder.Uri.AbsoluteUri;
        }
        catch
        {
            return Regex.Replace(url, @"([?&])param=\d+y\d+(&|$)", "$1",
                RegexOptions.IgnoreCase).TrimEnd('?', '&');
        }
    }
}
