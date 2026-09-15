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
            Console.WriteLine(
                "[playlist-lifetime] PASS rows=240->0->240, queue/index=0 after leave, scroll=712");
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
                songs.Select(song => new PlaylistPageCacheTrack(song, true, true)).ToList(),
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
