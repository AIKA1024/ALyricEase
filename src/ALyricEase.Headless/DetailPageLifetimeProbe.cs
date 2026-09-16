using System.Runtime.CompilerServices;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>歌手/专辑详情单例离页释放、磁盘恢复、筛选和真实滚动位置回归。</summary>
internal static class DetailPageLifetimeProbe
{
    public static int Run()
    {
        try
        {
            Task.Run(RunAsync).GetAwaiter().GetResult();

            var vm = ServiceLocator.Get<ArtistSongsPageViewModel>();
            var view = new ArtistSongsPageView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 700, Content = view };
            window.Show();
            for (var index = 0; index < 8; index++) Dispatcher.UIThread.RunJobs();
            var scroller = view.FindControl<ScrollViewer>("PageScroller")
                           ?? throw new InvalidOperationException("找不到歌手歌曲滚动容器");
            Assert(Math.Abs(scroller.Offset.Y - 618) < 1,
                $"布局完成后的实际滚动位置错误：{scroller.Offset.Y:F1}");
            window.Close();
            vm.ReleaseCurrentPageData();
            Task.Run(VerifyMainNavigationChainAsync).GetAwaiter().GetResult();
            VerifyArtistViewNavigationLifetime();

            Console.WriteLine(
                "[detail-lifetime] PASS artist/album/songs/albums released+restored, " +
                "scroll=618, final-back=0, artist-view-collected");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[detail-lifetime] FAIL: {ex}");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        var cache = ServiceLocator.Get<MusicCacheService>();
        var songs = CreateSongs(160);
        var tracks = songs.Select(song => new NavigationPageCacheTrack(song, true)).ToList();
        var albums = Enumerable.Range(1, 80)
            .Select(index => new NavigationPageCacheAlbum(
                70_000 + index, $"生命周期专辑 {index:D3}", $"https://invalid/{index}.jpg", ""))
            .ToList();

        await VerifyArtistAsync(cache, tracks, albums);
        await VerifyAlbumAsync(cache, tracks.Take(48).ToList());
        await VerifyArtistAlbumsAsync(cache, albums);
        await VerifyArtistSongsAsync(cache, tracks);
    }

    private static async Task VerifyArtistAsync(
        MusicCacheService cache,
        IReadOnlyList<NavigationPageCacheTrack> tracks,
        IReadOnlyList<NavigationPageCacheAlbum> albums)
    {
        var vm = ServiceLocator.Get<ArtistViewModel>();
        vm.ReleaseCurrentPageData();
        var snapshot = NewSnapshot(DetailPageKind.Artist, scroll: 240, id: 9101, name: "歌手生命周期");
        await cache.CacheDetailPageSnapshotAsync(
            snapshot.CacheKey,
            new DetailPageCacheData(
                tracks.Take(30).ToList(),
                albums.Take(50).ToList(),
                albums.Skip(50).Take(10).ToList(),
                Name: "歌手生命周期",
                AvatarUrl: "https://invalid/artist.jpg"));
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        Assert(vm.RetainedItemCount == 90, "歌手页磁盘快照恢复数量错误");
        var weak = CaptureArtistAndRelease(vm, out var captured);
        Assert(vm.RetainedItemCount == 0 && !vm.HasRetainedPageData, "歌手页离页后仍保留重数据");
        AssertCollected(weak, "歌手页歌曲行");
        await vm.RestoreNavigationSnapshotAsync(captured);
        Assert(vm.RetainedItemCount == 90, "歌手页返回恢复失败");
        vm.ReleaseCurrentPageData();
    }

    private static async Task VerifyAlbumAsync(
        MusicCacheService cache,
        IReadOnlyList<NavigationPageCacheTrack> tracks)
    {
        var vm = ServiceLocator.Get<AlbumViewModel>();
        vm.ReleaseCurrentPageData();
        var snapshot = NewSnapshot(DetailPageKind.Album, scroll: 360, id: 9201, name: "专辑生命周期");
        await cache.CacheDetailPageSnapshotAsync(
            snapshot.CacheKey,
            new DetailPageCacheData(
                tracks,
                [],
                [],
                Name: "专辑生命周期",
                ArtistName: "测试歌手",
                TrackCountText: $"{tracks.Count} 首",
                Description: "生命周期测试",
                CoverUrl: "https://invalid/album.jpg",
                PrimaryArtist: new NavigationPageCacheArtist(MusicSource.NetEase, 9101, "", "测试歌手")));
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        Assert(vm.RetainedTrackCount == tracks.Count, "专辑页磁盘快照恢复数量错误");
        var weak = CaptureAlbumAndRelease(vm, out var captured);
        Assert(vm.RetainedTrackCount == 0 && !vm.HasRetainedPageData, "专辑页离页后仍保留曲目");
        AssertCollected(weak, "专辑页歌曲行");
        await vm.RestoreNavigationSnapshotAsync(captured);
        Assert(vm.RetainedTrackCount == tracks.Count && vm.PrimaryArtist is not null,
            "专辑页返回恢复失败");
        vm.ReleaseCurrentPageData();
    }

    private static async Task VerifyArtistAlbumsAsync(
        MusicCacheService cache,
        IReadOnlyList<NavigationPageCacheAlbum> albums)
    {
        var vm = ServiceLocator.Get<ArtistAlbumsPageViewModel>();
        vm.ReleaseCurrentPageData();
        var snapshot = NewSnapshot(
            DetailPageKind.ArtistAlbums, scroll: 420, id: 9301, name: "专辑列表生命周期",
            selectedSortIndex: 1, isFilterExpanded: true);
        await cache.CacheDetailPageSnapshotAsync(
            snapshot.CacheKey,
            new DetailPageCacheData(
                [], albums, [], Name: snapshot.Name, Subtitle: $"已加载 {albums.Count} 张专辑",
                Offset: albums.Count, HasMore: false));
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        Assert(vm.RetainedAlbumCount == albums.Count, "歌手全部专辑快照恢复数量错误");
        var weak = CaptureArtistAlbumsAndRelease(vm, out var captured);
        Assert(vm.RetainedAlbumCount == 0 && !vm.HasRetainedPageData,
            "歌手全部专辑离页后仍保留卡片");
        AssertCollected(weak, "歌手全部专辑卡片");
        await vm.RestoreNavigationSnapshotAsync(captured);
        Assert(vm.RetainedAlbumCount == albums.Count
               && vm.Filters.SelectedSortIndex == 1
               && vm.Filters.IsExpanded,
            "歌手全部专辑返回状态恢复失败");
        vm.ReleaseCurrentPageData();
    }

    private static async Task VerifyArtistSongsAsync(
        MusicCacheService cache,
        IReadOnlyList<NavigationPageCacheTrack> tracks)
    {
        var vm = ServiceLocator.Get<ArtistSongsPageViewModel>();
        vm.ReleaseCurrentPageData();
        var snapshot = NewSnapshot(
            DetailPageKind.ArtistSongs, scroll: 580, id: 9401, name: "歌曲列表生命周期",
            selectedSortIndex: 1, isFilterExpanded: true);
        await cache.CacheDetailPageSnapshotAsync(
            snapshot.CacheKey,
            new DetailPageCacheData(
                tracks, [], [], Name: snapshot.Name, Subtitle: $"共 {tracks.Count} 首",
                Offset: tracks.Count, Total: tracks.Count, HasMore: false));
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        Assert(vm.RetainedTrackCount == tracks.Count, "歌手全部歌曲快照恢复数量错误");
        vm.UpdatePageScrollOffset(618);
        var weak = CaptureArtistSongsAndRelease(vm, out var captured);
        Assert(vm.RetainedTrackCount == 0 && !vm.HasRetainedPageData,
            "歌手全部歌曲离页后仍保留曲目");
        AssertCollected(weak, "歌手全部歌曲行");
        await vm.RestoreNavigationSnapshotAsync(captured);
        Assert(vm.RetainedTrackCount == tracks.Count
               && vm.Filters.SelectedSortIndex == 1
               && vm.Filters.IsExpanded,
            "歌手全部歌曲返回状态恢复失败");
        Assert(vm.TryGetPendingScrollRestore(0, out _, out var offset)
               && Math.Abs(offset - 618) < 0.01,
            "歌手全部歌曲滚动位置恢复失败");
    }

    private static async Task VerifyMainNavigationChainAsync()
    {
        var cache = ServiceLocator.Get<MusicCacheService>();
        var vm = ServiceLocator.Get<ArtistSongsPageViewModel>();
        var main = ServiceLocator.Get<MainViewModel>();
        var tracks = CreateSongs(75)
            .Select(song => new NavigationPageCacheTrack(song, true))
            .ToList();
        var seed = NewSnapshot(
            DetailPageKind.ArtistSongs, scroll: 320, id: 9501, name: "导航链生命周期");
        await cache.CacheDetailPageSnapshotAsync(
            seed.CacheKey,
            new DetailPageCacheData(
                tracks, [], [], Name: seed.Name, Subtitle: $"共 {tracks.Count} 首",
                Offset: tracks.Count, Total: tracks.Count, HasMore: false));
        await vm.RestoreNavigationSnapshotAsync(seed);

        main.ActivePage = "ArtistSongs";
        main.ActivePage = "Recommend";
        Assert(vm.RetainedTrackCount == 0,
            "前进离开歌手全部歌曲页时 MainViewModel 没有释放重数据");

        main.GoBackCommand.Execute(null);
        await WaitUntilAsync(() => vm.RetainedTrackCount == tracks.Count);
        Assert(main.ActivePage == "ArtistSongs",
            "第一次返回没有恢复歌手全部歌曲页");

        main.GoBackCommand.Execute(null);
        Assert(main.ActivePage == "Recommend" && vm.RetainedTrackCount == 0,
            "最终返回个性推荐后详情页重数据仍被单例保留");
        Assert(!main.CanGoBack, "导航链返回首页后仍残留历史项");
    }

    private static void VerifyArtistViewNavigationLifetime()
    {
        var main = ServiceLocator.Get<MainViewModel>();
        while (main.CanGoBack) main.GoBackCommand.Execute(null);
        main.ActivePage = "Recommend";

        var shell = new AppShell { DataContext = main };
        var window = new Window { Width = 1200, Height = 760, Content = shell };
        window.Show();
        DrainUi();

        main.ActivePage = "Artist";
        DrainUi();
        var weak = CaptureCurrentArtistView(shell);

        main.GoBackCommand.Execute(null);
        DrainUiFor(TimeSpan.FromMilliseconds(420));
        Assert(!shell.GetVisualDescendants().OfType<ArtistView>().Any(),
            "页面过渡结束后 ArtistView 仍留在视觉树");
        AssertCollected(weak, "歌手页视图");
        window.Close();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureCurrentArtistView(AppShell shell)
    {
        var artistView = shell.GetVisualDescendants().OfType<ArtistView>().FirstOrDefault()
                         ?? throw new InvalidOperationException("真实导航没有创建 ArtistView");
        return new WeakReference(artistView);
    }

    private static void DrainUi()
    {
        for (var index = 0; index < 12; index++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void DrainUiFor(TimeSpan duration)
    {
        var deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            DrainUi();
            Thread.Sleep(10);
        }
        DrainUi();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待详情页导航恢复超时");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static List<Song> CreateSongs(int count) => Enumerable.Range(1, count).Select(index => new Song
    {
        Id = 800_000 + index,
        Source = MusicSource.NetEase,
        Name = $"生命周期歌曲 {index:D3}",
        Artist = "测试歌手",
        Album = "测试专辑",
        DurationMs = 180_000,
        ArtistIds = [9101],
        ArtistNames = ["测试歌手"],
        AlbumId = 9201,
    }).ToList();

    private static DetailNavigationSnapshot NewSnapshot(
        DetailPageKind kind,
        double scroll,
        long id,
        string name,
        int selectedSortIndex = 0,
        bool isFilterExpanded = false) => new(
        Guid.NewGuid().ToString("N"),
        kind,
        MusicSource.NetEase,
        id,
        "",
        name,
        selectedSortIndex,
        "",
        isFilterExpanded,
        scroll);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureArtistAndRelease(
        ArtistViewModel vm, out DetailNavigationSnapshot snapshot)
    {
        var weak = new WeakReference(vm.Songs[0]);
        snapshot = vm.CaptureAndReleaseNavigationSnapshot()
                   ?? throw new InvalidOperationException("歌手页未生成导航快照");
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureAlbumAndRelease(
        AlbumViewModel vm, out DetailNavigationSnapshot snapshot)
    {
        var weak = new WeakReference(vm.Songs[0]);
        snapshot = vm.CaptureAndReleaseNavigationSnapshot()
                   ?? throw new InvalidOperationException("专辑页未生成导航快照");
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureArtistAlbumsAndRelease(
        ArtistAlbumsPageViewModel vm, out DetailNavigationSnapshot snapshot)
    {
        var weak = new WeakReference(vm.Albums[0]);
        snapshot = vm.CaptureAndReleaseNavigationSnapshot()
                   ?? throw new InvalidOperationException("歌手全部专辑页未生成导航快照");
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureArtistSongsAndRelease(
        ArtistSongsPageViewModel vm, out DetailNavigationSnapshot snapshot)
    {
        var weak = new WeakReference(vm.Songs[0]);
        snapshot = vm.CaptureAndReleaseNavigationSnapshot()
                   ?? throw new InvalidOperationException("歌手全部歌曲页未生成导航快照");
        return weak;
    }

    private static void AssertCollected(WeakReference weak, string label)
    {
        for (var index = 0; index < 3 && weak.IsAlive; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert(!weak.IsAlive, $"{label}离页后仍有强引用");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
