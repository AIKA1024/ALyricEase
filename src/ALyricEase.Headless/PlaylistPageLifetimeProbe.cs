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

/// <summary>单例歌单页离页释放与一次性磁盘快照恢复回归；全程不访问网络。</summary>
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
                "eviction-disk-fallback, restore-no-disk-wait");
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

        Assert(vm.RetainedTrackRowCount == songs.Count, "磁盘快照没有恢复全部原始行");
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
            "返回后页面内容没有从磁盘完整恢复");
        Assert(vm.TryGetPendingScrollRestore(0, out _, out var offset)
               && Math.Abs(offset - 712) < 0.01,
            "返回滚动位置没有恢复");

    }

    /// <summary>内存快照被容量淘汰后,磁盘兜底必须仍能读回(淘汰条目要立即落盘,不能留在延迟窗口里)。</summary>
    private static async Task VerifySnapshotEvictionAsync()
    {
        var cache = ServiceLocator.Get<MusicCacheService>();
        var songs = CreateSongs(30, 950_000);
        var tracks = songs.Select(song => new NavigationPageCacheTrack(song, true)).ToList();
        var trackIds = songs.Select(song => song.Id).ToArray();
        var keys = new List<string>();
        for (var index = 0; index < 10; index++)
        {
            var key = Guid.NewGuid().ToString("N");
            keys.Add(key);
            _ = cache.CachePlaylistPageSnapshotAsync(
                key, new PlaylistPageCacheData(tracks, trackIds, songs.Count, null));
        }

        var restored = await cache.TryTakePlaylistPageSnapshotAsync(keys[0]);
        Assert(restored is not null && restored.Tracks.Count == songs.Count,
            "内存快照被容量淘汰后磁盘兜底失效");
        for (var index = 1; index < keys.Count; index++)
            await cache.DiscardPlaylistPageSnapshotAsync(keys[index]);
    }

    /// <summary>返回恢复不得等待落盘:离页后立刻返回应是毫秒级(修复前要等 0.6~1.3s 写完快照)。</summary>
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
