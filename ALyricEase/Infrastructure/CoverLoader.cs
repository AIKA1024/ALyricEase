using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ALyricEase.Infrastructure;

/// <summary>远程封面图加载:内存缓存(按解码字节预算,超限按插入顺序淘汰最旧)+ 后台解码,失败返回 null(不抛异常)。
/// 返回的 Bitmap 不可变,可跨线程使用。
/// 预算按字节而非张数:播放条/正在播放页的大封面(640px≈1.6MB)也进同一缓存,
/// 若按张数 300 上限会堆到 ~480MB;按字节预算则总量恒定有界。</summary>
public static class CoverLoader
{
    /// <summary>缓存总解码字节预算(~48MB):歌曲行 100px≈40KB 能存上千张,640px 大封面只留 ~30 张。</summary>
    private const long MaxCacheBytes = 48L * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, IImage?> Cache = new();
    private static readonly Queue<string> Order = new();
    private static long _cacheBytes;

    /// <summary>加载封面。size>0 时请求对应缩略图(param=WxH),列表项务必用 ~100 的小图,避免全尺寸大图撑爆内存。</summary>
    public static async Task<IImage?> LoadAsync(string url, int size = 0)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (size > 0) url = BuildSizedUrl(url, size);
        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var hit)) return hit;
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            var bitmap = await Task.Run(() =>
            {
                using var ms = new MemoryStream(bytes);
                return new Bitmap(ms);
            }).ConfigureAwait(false);
            lock (Cache)
            {
                var cost = EstimateBytes(bitmap);
                // 单张超过预算 → 不进缓存(避免一张超超大图独占预算),直接用
                if (cost > MaxCacheBytes) return bitmap;
                while (_cacheBytes + cost > MaxCacheBytes && Order.Count > 0)
                {
                    var oldest = Order.Dequeue();
                    if (Cache.Remove(oldest, out var old))
                        _cacheBytes -= EstimateBytes(old);
                }
                Cache[url] = bitmap;
                _cacheBytes += cost;
                Order.Enqueue(url);
            }
            return bitmap;
        }
        catch
        {
            lock (Cache) Cache[url] = null;
            return null;
        }
    }

    /// <summary>解码像素占用估计(StridePixelFormats.Bgra8888 = 4 字节/像素)。</summary>
    private static long EstimateBytes(IImage? img) =>
        img is Bitmap b ? (long)b.PixelSize.Width * b.PixelSize.Height * 4 : 0;

    /// <summary>给网易封面 URL 追加缩略参数(param=WxH)。</summary>
    public static string BuildSizedUrl(string url, int size)
    {
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}param={size}y{size}";
    }
}
