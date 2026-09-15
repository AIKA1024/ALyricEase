using AsyncImageLoader.Core.Caching;
using AsyncImageLoader.Core.Leases;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 按解码像素成本和条目数双重限制的租约式 LRU。
/// 可见控件持有租约期间不会被淘汰；同 URL 并发请求共享一次加载；
/// 最后一个等待者取消后会把取消传给网络/解码工厂。
/// </summary>
internal sealed class BoundedImageMemoryCache : IImageMemoryCache
{
    public const long DefaultMaximumBytes = 64L * 1024 * 1024;
    public const int DefaultMaximumItems = 512;

    private readonly object _gate = new();
    private readonly long _maximumBytes;
    private readonly int _maximumItems;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _cachedBytes;
    private bool _disposed;

    public BoundedImageMemoryCache(
        long maximumBytes = DefaultMaximumBytes,
        int maximumItems = DefaultMaximumItems)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumItems <= 0) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        _maximumBytes = maximumBytes;
        _maximumItems = maximumItems;
    }

    internal long CachedBytes
    {
        get { lock (_gate) return _cachedBytes; }
    }

    internal int CachedItemCount
    {
        get { lock (_gate) return _lru.Count; }
    }

    public async Task<IImageLease?> GetOrCreateAsync(
        string key,
        Func<CancellationToken, Task<IImage?>> factory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Cache key cannot be empty.", nameof(key));
        ArgumentNullException.ThrowIfNull(factory);

        Entry entry;
        Task<IImage?> loadingTask;
        var startLoading = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(key, out entry!) && entry.Image is not null)
            {
                TouchLocked(entry);
                entry.LeaseCount++;
                return CreateLease(entry);
            }

            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(key);
                _entries.Add(key, entry);
            }

            if (entry.LoadingTask is null)
            {
                entry.LoadCancellation = new CancellationTokenSource();
                entry.Completion = new TaskCompletionSource<IImage?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                entry.LoadingTask = entry.Completion.Task;
                startLoading = true;
            }

            entry.WaiterCount++;
            loadingTask = entry.LoadingTask;
        }

        if (startLoading) _ = LoadEntryAsync(entry, factory);

        IImage? image;
        try
        {
            image = await loadingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            CancellationTokenSource? cancel = null;
            List<IImage>? dispose = null;
            lock (_gate)
            {
                entry.WaiterCount--;
                if (entry.WaiterCount == 0 && entry.LoadingTask is not null)
                {
                    DetachLocked(entry, ref dispose);
                    cancel = entry.LoadCancellation;
                }
                DisposeDetachedIfUnusedLocked(entry, ref dispose);
            }
            CancelSafely(cancel);
            DisposeImages(dispose);
            throw;
        }

        List<IImage>? evicted = null;
        IImageLease? lease = null;
        lock (_gate)
        {
            entry.WaiterCount--;
            if (_disposed || image is null)
            {
                DetachLocked(entry, ref evicted);
                DisposeDetachedIfUnusedLocked(entry, ref evicted);
            }
            else
            {
                // Clear 可能已把条目从 LRU 摘除，但已有等待者仍可安全取得租约；
                // 它释放后会立即销毁，不重新放回缓存。
                entry.LeaseCount++;
                if (!entry.IsDetached) TouchLocked(entry);
                TrimLocked(ref evicted);
                lease = CreateLease(entry);
            }
        }
        DisposeImages(evicted);
        return lease;
    }

    public void Clear()
    {
        List<IImage>? dispose = null;
        lock (_gate)
        {
            foreach (var entry in _entries.Values.ToArray())
                DetachLocked(entry, ref dispose);
        }
        DisposeImages(dispose);
    }

    public void Dispose()
    {
        List<IImage>? dispose = null;
        List<CancellationTokenSource>? cancel = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values.ToArray())
            {
                DetachLocked(entry, ref dispose);
                if (entry.LoadCancellation is not null)
                    (cancel ??= []).Add(entry.LoadCancellation);
            }
        }
        if (cancel is not null)
            foreach (var source in cancel) CancelSafely(source);
        DisposeImages(dispose);
    }

    private async Task LoadEntryAsync(Entry entry, Func<CancellationToken, Task<IImage?>> factory)
    {
        var completion = entry.Completion!;
        var cancellation = entry.LoadCancellation!;
        try
        {
            var image = await factory(cancellation.Token).ConfigureAwait(false);
            List<IImage>? dispose = null;
            lock (_gate)
            {
                entry.LoadingTask = null;
                entry.Completion = null;
                entry.LoadCancellation = null;
                entry.Image = image;
                if (image is not null && !entry.IsDetached && !_disposed)
                {
                    entry.Cost = EstimateBytes(image);
                    _cachedBytes += entry.Cost;
                    entry.LruNode = _lru.AddLast(entry);
                    TrimLocked(ref dispose);
                }
                else if (image is null)
                {
                    DetachLocked(entry, ref dispose);
                }
                else
                {
                    entry.IsDetached = true;
                }
                DisposeDetachedIfUnusedLocked(entry, ref dispose);
            }
            completion.TrySetResult(image);
            DisposeImages(dispose);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            List<IImage>? dispose = null;
            lock (_gate)
            {
                entry.LoadingTask = null;
                entry.Completion = null;
                entry.LoadCancellation = null;
                DetachLocked(entry, ref dispose);
                DisposeDetachedIfUnusedLocked(entry, ref dispose);
            }
            completion.TrySetCanceled(cancellation.Token);
            DisposeImages(dispose);
        }
        catch (Exception exception)
        {
            List<IImage>? dispose = null;
            lock (_gate)
            {
                entry.LoadingTask = null;
                entry.Completion = null;
                entry.LoadCancellation = null;
                DetachLocked(entry, ref dispose);
                DisposeDetachedIfUnusedLocked(entry, ref dispose);
            }
            completion.TrySetException(exception);
            DisposeImages(dispose);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private IImageLease CreateLease(Entry entry) =>
        ImageLease.Create(entry.Image!, () => Release(entry));

    private void Release(Entry entry)
    {
        List<IImage>? dispose = null;
        lock (_gate)
        {
            if (entry.LeaseCount > 0) entry.LeaseCount--;
            DisposeDetachedIfUnusedLocked(entry, ref dispose);
            if (!entry.IsDetached) TrimLocked(ref dispose);
        }
        DisposeImages(dispose);
    }

    private void TouchLocked(Entry entry)
    {
        if (entry.LruNode is null) return;
        _lru.Remove(entry.LruNode);
        _lru.AddLast(entry.LruNode);
    }

    private void TrimLocked(ref List<IImage>? dispose)
    {
        while (_cachedBytes > _maximumBytes || _lru.Count > _maximumItems)
        {
            var node = _lru.First;
            while (node is not null && (node.Value.LeaseCount != 0 || node.Value.WaiterCount != 0))
                node = node.Next;
            if (node is null) return; // 全部正在显示，允许临时超限；最后一个租约释放时继续淘汰。
            DetachLocked(node.Value, ref dispose);
        }
    }

    private void DetachLocked(Entry entry, ref List<IImage>? dispose)
    {
        if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            _entries.Remove(entry.Key);
        if (entry.LruNode is not null)
        {
            _lru.Remove(entry.LruNode);
            entry.LruNode = null;
            _cachedBytes -= entry.Cost;
            entry.Cost = 0;
        }
        entry.IsDetached = true;
        DisposeDetachedIfUnusedLocked(entry, ref dispose);
    }

    private static void DisposeDetachedIfUnusedLocked(Entry entry, ref List<IImage>? dispose)
    {
        if (!entry.IsDetached || entry.LeaseCount != 0 || entry.WaiterCount != 0
            || entry.LoadingTask is not null || entry.Image is null) return;
        (dispose ??= []).Add(entry.Image);
        entry.Image = null;
    }

    private static long EstimateBytes(IImage image)
    {
        var size = image is Bitmap bitmap
            ? bitmap.PixelSize.ToSize(1)
            : image.Size;
        return Math.Max(1, checked((long)Math.Ceiling(size.Width) * (long)Math.Ceiling(size.Height) * 4));
    }

    private static void CancelSafely(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void DisposeImages(List<IImage>? images)
    {
        if (images is null) return;
        foreach (var image in images) (image as IDisposable)?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BoundedImageMemoryCache));
    }

    private sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public IImage? Image;
        public long Cost;
        public Task<IImage?>? LoadingTask;
        public TaskCompletionSource<IImage?>? Completion;
        public CancellationTokenSource? LoadCancellation;
        public int WaiterCount;
        public int LeaseCount;
        public bool IsDetached;
        public LinkedListNode<Entry>? LruNode;
    }
}
