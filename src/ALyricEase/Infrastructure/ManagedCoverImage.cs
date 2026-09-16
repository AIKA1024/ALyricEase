using AsyncImageLoader.Core.Caching;
using AsyncImageLoader.Core.Pipeline;
using Avalonia;
using Avalonia.Controls;
using ALyricEase.Services;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 将原始封面 URL 转成对应像素规格后交给租约式异步加载器。
/// Image 离开可视树时加载器会取消请求并释放位图租约，避免页面 VM 长期持有 Bitmap。
/// </summary>
public static class ManagedCoverImage
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(ManagedCoverImage));

    public static readonly AttachedProperty<int> DecodeSizeProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("DecodeSize", typeof(ManagedCoverImage), 100);

    static ManagedCoverImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>(OnRequestChanged);
        DecodeSizeProperty.Changed.AddClassHandler<Image>(OnRequestChanged);
    }

    public static string? GetSource(Image image) => image.GetValue(SourceProperty);

    public static void SetSource(Image image, string? value) => image.SetValue(SourceProperty, value);

    public static int GetDecodeSize(Image image) => image.GetValue(DecodeSizeProperty);

    public static void SetDecodeSize(Image image, int value) => image.SetValue(DecodeSizeProperty, value);

    private static void OnRequestChanged(Image image, AvaloniaPropertyChangedEventArgs args)
    {
        var source = GetSource(image);
        var size = GetDecodeSize(image);
        AsyncImageLoader.ImageLoader.SetSource(
            image,
            string.IsNullOrWhiteSpace(source)
                ? null
                : size > 0 ? CoverLoader.BuildSizedUrl(source, size) : source);
    }
}

/// <summary>
/// 应用级封面管线：解码图由带租约的有界 LRU 缓存在 RAM 中；编码字节复用统一磁盘缓存。
/// 切页时保留热点解码图，超出预算且无人租用的条目才会被淘汰并释放。
/// </summary>
public static class CoverImagePipeline
{
    private static readonly object Gate = new();
    private static ImageLoaderPipeline? _pipeline;
    private static BoundedImageMemoryCache? _memoryCache;

    /// <summary>性能回归探针使用:当前解码图缓存(字节数/条目数)。
    /// 多次页面往返后应受 ImageMemoryBudget 的预算约束并趋于稳定,而不是无界增长。</summary>
    internal static (long Bytes, int Items) MemoryCacheStats
    {
        get
        {
            lock (Gate)
            {
                var cache = _memoryCache;
                return cache is null ? (0, 0) : (cache.CachedBytes, cache.CachedItemCount);
            }
        }
    }

    public static void Configure(MusicCacheService cache)
    {
        lock (Gate)
        {
            var previous = AsyncImageLoader.ImageLoader.AsyncImageLoader;
            var http = new HttpClient(new SocketsHttpHandler
            {
                // 首页会同时实化多个横向区块，限制同站连接数，避免封面挤占音乐 API。
                MaxConnectionsPerServer = 6,
            })
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            var memoryCache = new BoundedImageMemoryCache(
                ImageMemoryBudget.LeaseCacheBytes, ImageMemoryBudget.LeaseCacheItems);
            var pipeline = ImageLoaderPipelineBuilder
                .Uncached()
                .UseMemoryCache(memoryCache)
                .UseByteCache(new MusicCoverByteCache(cache))
                .UseHttpClient(http, disposeHttpClient: true)
                .Build();

            AsyncImageLoader.ImageLoader.AsyncImageLoader = pipeline;
            _pipeline = pipeline;
            _memoryCache = memoryCache;
            if (!ReferenceEquals(previous, pipeline)) previous.Dispose();
        }
    }

    /// <summary>立刻移除未被可见 Image 租用的解码图；在途页面过渡仍可安全完成。</summary>
    public static void ClearMemoryCache()
    {
        lock (Gate) _pipeline?.ClearMemoryCache();
    }

    private sealed class MusicCoverByteCache(MusicCacheService cache) : IImageByteCache
    {
        public async Task<Stream?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            var bytes = await cache.TryGetCoverAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : new MemoryStream(bytes, writable: false);
        }

        public async Task SetAsync(string key, Stream data, CancellationToken cancellationToken = default)
        {
            byte[] bytes;
            if (data is MemoryStream memory && memory.Position == 0)
            {
                // 加载器已经把响应缓冲为 MemoryStream，直接复制最终字节，避免再建一层中间缓冲。
                bytes = memory.ToArray();
            }
            else
            {
                using var buffer = new MemoryStream();
                await data.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                bytes = buffer.ToArray();
            }
            await cache.CacheCoverAsync(key, bytes).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            cache.RemoveCoverAsync(key).WaitAsync(cancellationToken);

        // 统一缓存由设置页的 MusicCacheService.ClearAsync 一次性清理，避免两套索引失配。
        public void Clear() { }
    }
}
