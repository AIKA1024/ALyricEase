using System.Diagnostics;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Runtime.CompilerServices;

namespace ALyricEase.Headless;

/// <summary>单例歌单页离页释放与一次性内存快照恢复回归；全程不访问网络、不写磁盘快照。</summary>
internal static class PlaylistPageLifetimeProbe
{
    public static int Run()
    {
        try
        {
            // SetupWithoutStarting 不泵 UI Dispatcher；放到线程池可避免 await 捕获其同步上下文。
            Task.Run(RunAsync).GetAwaiter().GetResult();

            var vm = ServiceLocator.Get<PlaylistViewModel>();
            var view = new PlaylistView { DataContext = vm };
            var window = new Window { Width = 1000, Height = 700, Content = view };
            window.Show();
            for (var index = 0; index < 8; index++) Dispatcher.UIThread.RunJobs();
            var scroller = view.FindControl<ScrollViewer>("PageScroller")
                           ?? throw new InvalidOperationException("找不到歌单滚动容器");
            Assert(Math.Abs(scroller.Offset.Y - 712) < 1,
                $"布局完成后的实际滚动位置错误：{scroller.Offset.Y:F1}");
            window.Close();
            vm.ReleaseCurrentPageData();

            // 这两项会改动单例 VM 状态,必须放在上面"离页后滚动恢复"的检查之后。
            Task.Run(VerifySnapshotEvictionAsync).GetAwaiter().GetResult();
            Task.Run(VerifyRestoreDoesNotWaitForDiskAsync).GetAwaiter().GetResult();
            Console.WriteLine(
                "[playlist-lifetime] PASS rows=240->0->240, queue/index=0 after leave, scroll=712, " +
                "eviction-drops-oldest, restore-no-disk-wait");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[playlist-lifetime] FAIL: {ex}");
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        var vm = ServiceLocator.Get<PlaylistViewModel>();
        var cache = ServiceLocator.Get<MusicCacheService>();
        vm.ReleaseCurrentPageData();

        var songs = Enumerable.Range(1, 240).Select(index => new Song
        {
            Id = 900_000 + index,
            Source = MusicSource.NetEase,
            Name = $"生命周期歌曲 {index:D3}",
            Artist = "测试歌手",
            Album = "测试专辑",
            DurationMs = 180_000,
        }).ToList();
        var key = Guid.NewGuid().ToString("N");
        await cache.CachePlaylistPageSnapshotAsync(
            key,
            new PlaylistPageCacheData(
                songs.Select(song => new NavigationPageCacheTrack(song, true, true)).ToList(),
                songs.Select(song => song.Id).ToArray(),
                songs.Count,
                null));

        var initial = new PlaylistNavigationSnapshot(
            key,
            PlaylistPageKind.NetEase,
            new Playlist
            {
                Id = 77,
                Source = MusicSource.NetEase,
                Name = "超大歌单生命周期探针",
                TrackCount = songs.Count,
            },
            null,
            "超大歌单生命周期探针",
            "测试用户",
            1,
            "",
            true,
            640);
        await vm.RestoreNavigationSnapshotAsync(initial);

        Assert(vm.RetainedTrackRowCount == songs.Count, "内存快照没有恢复全部原始行");
        Assert(vm.Tracks.Count == songs.Count, "排序投影恢复后内容数量丢失");
        Assert(vm.RetainedQueueSongCount == songs.Count, "播放队列没有恢复");
        Assert(vm.RetainedTrackIdCount == songs.Count, "懒加载索引没有恢复");
        Assert(vm.Filters.SelectedSortIndex == 1 && vm.Filters.IsExpanded, "筛选 UI 状态没有恢复");

        vm.UpdatePageScrollOffset(712);
        var rowReference = CaptureAndRelease(vm, out var captured);
        Assert(vm.RetainedTrackRowCount == 0 && vm.Tracks.Count == 0,
            "离页后歌曲行仍被单例 ViewModel 保留");
        Assert(vm.RetainedQueueSongCount == 0 && vm.RetainedTrackIdCount == 0,
            "离页后播放队列或索引仍被保留");
        Assert(vm.SelectedPlaylist is null, "离页后歌单头部仍被保留");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert(!rowReference.IsAlive, "离页后歌曲行对象仍有强引用，无法被 GC 回收");

        await vm.RestoreNavigationSnapshotAsync(captured);
        Assert(vm.RetainedTrackRowCount == songs.Count && vm.Tracks.Count == songs.Count,
            "返回后页面内容没有从快照完整恢复");
        Assert(vm.TryGetPendingScrollRestore(0, out _, out var offset)
               && Math.Abs(offset - 712) < 0.01,
            "返回滚动位置没有恢复");

    }

    /// <summary>内存快照容量行为:最近 MaxInMemorySnapshots 层必须全部命中(返回是 LIFO 的),
    /// 更旧的层被直接丢弃 —— 取不到就让调用方回退常规加载,而不是去磁盘找。
    /// 同时断言全程不产生任何快照文件(快照只驻内存)。</summary>
    private static async Task VerifySnapshotEvictionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-snapshot-evict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 用独立实例 + 临时目录,避免把测试数据写进真实用户缓存。
            using var http = new HttpClient();
            var cache = new MusicCacheService(128, root, http);
            var songs = CreateSongs(30, 950_000);
            var tracks = songs.Select(song => new NavigationPageCacheTrack(song, true)).ToList();
            var trackIds = songs.Select(song => song.Id).ToArray();
            var capacity = MusicCacheService.MaxInMemorySnapshots;
            var total = capacity + 8;
            var keys = new List<string>();
            for (var index = 0; index < total; index++)
            {
                var key = Guid.NewGuid().ToString("N");
                keys.Add(key);
                await cache.CachePlaylistPageSnapshotAsync(
                    key, new PlaylistPageCacheData(tracks, trackIds, songs.Count, null));
            }

            for (var index = total - capacity; index < total; index++)
            {
                var hit = await cache.TryTakePlaylistPageSnapshotAsync(keys[index]);
                Assert(hit is not null && hit.Tracks.Count == songs.Count,
                    $"容量内的快照丢失(第 {index} 层,容量 {capacity})");
            }

            for (var index = 0; index < total - capacity; index++)
                Assert(await cache.TryTakePlaylistPageSnapshotAsync(keys[index]) is null,
                    $"被淘汰的快照仍能取回(第 {index} 层)");

            Assert(!Directory.EnumerateFiles(root, "n-*.snapshot").Any(),
                "快照仍在落盘(应只驻内存)");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>返回恢复必须是毫秒级、且与"在别处停留多久"无关:捕获快照时同步完成、不派发任何后台写入,
    /// 所以返回耗时里不该出现任何 I/O 等待(修复前这里要等 0.6~1.3s 才拿到行)。</summary>
    private static async Task VerifyRestoreDoesNotWaitForDiskAsync()
    {
        var cache = ServiceLocator.Get<MusicCacheService>();
        var vm = ServiceLocator.Get<PlaylistViewModel>();
        vm.ReleaseCurrentPageData();

        var songs = CreateSongs(600, 960_000);
        var key = Guid.NewGuid().ToString("N");
        _ = cache.CachePlaylistPageSnapshotAsync(key, new PlaylistPageCacheData(
            songs.Select(song => new NavigationPageCacheTrack(song, true, true)).ToList(),
            songs.Select(song => song.Id).ToArray(),
            songs.Count,
            null));
        var snapshot = new PlaylistNavigationSnapshot(
            key,
            PlaylistPageKind.NetEase,
            new Playlist
            {
                Id = 91,
                Source = MusicSource.NetEase,
                Name = "返回不等落盘",
                TrackCount = songs.Count,
            },
            null,
            "返回不等落盘",
            "测试用户",
            0,
            "",
            false,
            0);
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        vm.UpdatePageScrollOffset(900);

        var captured = vm.CaptureAndReleaseNavigationSnapshot()
                       ?? throw new InvalidOperationException("未生成导航快照");
        var watch = Stopwatch.StartNew();
        await vm.RestoreNavigationSnapshotAsync(captured);
        watch.Stop();

        Assert(vm.RetainedTrackRowCount == songs.Count, "返回恢复行数错误");
        Assert(watch.ElapsedMilliseconds < 100,
            $"歌单页返回等了 {watch.ElapsedMilliseconds}ms 磁盘 I/O(应 < 100ms)");
        vm.ReleaseCurrentPageData();
    }

    private static List<Song> CreateSongs(int count, int baseId) =>
        Enumerable.Range(1, count).Select(index => new Song
        {
            Id = baseId + index,
            Source = MusicSource.NetEase,
            Name = $"生命周期歌曲 {index:D4}",
            Artist = "测试歌手",
            Album = "测试专辑",
            DurationMs = 180_000,
        }).ToList();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureAndRelease(
        PlaylistViewModel vm, out PlaylistNavigationSnapshot snapshot)
    {
        var rowReference = new WeakReference(vm.Tracks[0]);
        snapshot = vm.CaptureAndReleaseNavigationSnapshot()
                   ?? throw new InvalidOperationException("未生成轻量导航快照");
        return rowReference;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
