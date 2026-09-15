using System.Runtime.CompilerServices;
using AsyncImageLoader.Core.Leases;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>租约缓存容量/取消语义与页面事件退订的无头回归。</summary>
internal static class ImageLifetimeProbe
{
    public static int Run()
    {
        var failures = Task.Run(RunCacheChecksAsync).GetAwaiter().GetResult();
        failures += RunSearchViewLifetimeCheck();
        Console.WriteLine(failures == 0
            ? "[image-lifetime] PASS"
            : $"[image-lifetime] FAIL: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> RunCacheChecksAsync()
    {
        var failures = 0;

        using (var cache = new BoundedImageMemoryCache(maximumBytes: 800, maximumItems: 2))
        {
            var a = new ProbeImage(10, 10);
            var b = new ProbeImage(10, 10);
            var c = new ProbeImage(10, 10);
            (await LoadAsync(cache, "a", a)).Dispose();
            (await LoadAsync(cache, "b", b)).Dispose();
            (await LoadAsync(cache, "c", c)).Dispose();

            var bounded = cache.CachedItemCount == 2 && cache.CachedBytes <= 800 && a.IsDisposed;
            Console.WriteLine($"[image-lifetime] 容量: items={cache.CachedItemCount}/2 " +
                              $"bytes={cache.CachedBytes}/800 oldestDisposed={a.IsDisposed}");
            if (!bounded) failures++;
        }

        using (var cache = new BoundedImageMemoryCache(maximumBytes: 400, maximumItems: 1))
        {
            var visible = new ProbeImage(10, 10);
            var overflow = new ProbeImage(10, 10);
            var visibleLease = await LoadAsync(cache, "visible", visible);
            (await LoadAsync(cache, "overflow", overflow)).Dispose();
            var protectedWhileLeased = !visible.IsDisposed && overflow.IsDisposed;
            visibleLease.Dispose();
            Console.WriteLine($"[image-lifetime] 租约保护: visibleAlive={protectedWhileLeased} items={cache.CachedItemCount}");
            if (!protectedWhileLeased || cache.CachedItemCount > 1 || cache.CachedBytes > 400) failures++;
        }

        using (var cache = new BoundedImageMemoryCache())
        {
            var calls = 0;
            var releaseFactory = new TaskCompletionSource<IImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IImage?> Factory(CancellationToken token)
            {
                Interlocked.Increment(ref calls);
                return await releaseFactory.Task.WaitAsync(token).ConfigureAwait(false);
            }

            var first = cache.GetOrCreateAsync("shared", Factory);
            var second = cache.GetOrCreateAsync("shared", Factory);
            var shared = new ProbeImage(10, 10);
            releaseFactory.SetResult(shared);
            var leases = await Task.WhenAll(first, second);
            var singleFlight = calls == 1 && ReferenceEquals(leases[0]!.Image, leases[1]!.Image);
            foreach (var lease in leases) lease?.Dispose();
            Console.WriteLine($"[image-lifetime] single-flight: calls={calls} shared={singleFlight}");
            if (!singleFlight) failures++;
        }

        using (var cache = new BoundedImageMemoryCache())
        using (var requestCancellation = new CancellationTokenSource())
        {
            var factoryCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = cache.GetOrCreateAsync("cancel", async token =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    factoryCancelled.TrySetResult();
                    throw;
                }
                return null;
            }, requestCancellation.Token);

            requestCancellation.Cancel();
            try { await request.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await factoryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var cancelled = cache.CachedItemCount == 0;
            Console.WriteLine($"[image-lifetime] 最后等待者取消: propagated=True items={cache.CachedItemCount}");
            if (!cancelled) failures++;
        }

        using (var cache = new BoundedImageMemoryCache())
        {
            var image = new ProbeImage(10, 10);
            var lease = await LoadAsync(cache, "leased-clear", image);
            cache.Clear();
            var survivedClear = !image.IsDisposed && cache.CachedItemCount == 0;
            lease.Dispose();
            Console.WriteLine($"[image-lifetime] Clear: leaseProtected={survivedClear} disposedAfterRelease={image.IsDisposed}");
            if (!survivedClear || !image.IsDisposed) failures++;
        }

        return failures;
    }

    private static Task<IImageLease> LoadAsync(BoundedImageMemoryCache cache, string key, IImage image) =>
        LoadCoreAsync(cache, key, image);

    private static async Task<IImageLease> LoadCoreAsync(BoundedImageMemoryCache cache, string key, IImage image) =>
        await cache.GetOrCreateAsync(key, _ => Task.FromResult<IImage?>(image)) ??
        throw new InvalidOperationException("Expected an image lease.");

    private static int RunSearchViewLifetimeCheck()
    {
        var (weak, subscribedWhileAttached, subscribedAfterDetach) = CreateAndDetachSearchView();
        for (var i = 0; i < 3 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Console.WriteLine($"[image-lifetime] SearchView: attached={subscribedWhileAttached} " +
                          $"detached={subscribedAfterDetach} collected={!weak.IsAlive}");
        return subscribedWhileAttached && !subscribedAfterDetach && !weak.IsAlive ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Weak, bool Attached, bool Detached) CreateAndDetachSearchView()
    {
        var view = new SearchView { DataContext = ServiceLocator.Get<SearchViewModel>() };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var attached = view.IsViewModelSubscribed;
        window.Content = null;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        var detached = view.IsViewModelSubscribed;
        return (new WeakReference(view), attached, detached);
    }

    private sealed class ProbeImage(double width, double height) : IImage, IDisposable
    {
        public Size Size { get; } = new(width, height);
        public bool IsDisposed { get; private set; }
        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) { }
        public void Dispose() => IsDisposed = true;
    }
}
