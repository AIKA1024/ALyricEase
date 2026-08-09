using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ALyricEase.Infrastructure;

/// <summary>远程封面图加载:内存缓存(上限淘汰)+ 后台解码,失败返回 null(不抛异常)。
/// 返回的 Bitmap 不可变,可跨线程使用。缓存超过上限按插入顺序淘汰最旧项,防歌单上千首封面无限占内存。</summary>
public static class CoverLoader
{
    private const int MaxCacheEntries = 300;

    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, IImage?> Cache = new();
    private static readonly Queue<string> Order = new();

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
                if (Cache.Count >= MaxCacheEntries && Order.Count > 0)
                {
                    var oldest = Order.Dequeue();
                    Cache.Remove(oldest);
                }
                Cache[url] = bitmap;
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

    /// <summary>给网易封面 URL 追加缩略参数(param=WxH)。</summary>
    public static string BuildSizedUrl(string url, int size)
    {
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}param={size}y{size}";
    }
}
