using System.Diagnostics;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>歌单页"返回时卡一会才出歌曲"真窗口计时:UI 冻结峰值 + 恢复各阶段耗时。
/// ALY_PL_ROWS 控制曲目规模(默认 600,模拟滚动到底部后的大歌单),ALY_PL_ROUNDS 控制轮数。</summary>
internal static class PlaylistReturnLatencyProbe
{
    private static readonly Stopwatch Clock = new();
    private static readonly List<(long At, string Stage)> Stages = new();
    private static readonly List<long> Heartbeats = new();

    public static async Task<int> RunRealAsync()
    {
        var rows = EnvInt("ALY_PL_ROWS", 600);
        var rounds = EnvInt("ALY_PL_ROUNDS", 3);
        var dwells = (Environment.GetEnvironmentVariable("ALY_PL_DWELL") ?? "0,400,2000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? Math.Max(0, parsed) : 0)
            .ToArray();
        if (dwells.Length == 0) dwells = [0];

        var cache = ServiceLocator.Get<MusicCacheService>();
        var vm = ServiceLocator.Get<PlaylistViewModel>();
        vm.ReleaseCurrentPageData();
        PlaylistViewModel.RestoreTimingTrace = message => Stages.Add((Clock.ElapsedMilliseconds, message));

        var view = new PlaylistView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        await DrainUiAsync(300);
        var scroller = view.FindControl<ScrollViewer>("PageScroller")
                       ?? throw new InvalidOperationException("找不到歌单滚动容器");

        // 量一下内存快照的驻留成本(歌曲对象本来就要为播放队列/歌词保留,这里只算新增驻留的上界)。
        Collect();
        var beforeSongs = GC.GetTotalMemory(true);
        var songs = CreateSongs(rows);
        Collect();
        var songBytes = GC.GetTotalMemory(true) - beforeSongs;

        Log($"[pl-latency] 规模: {rows} 首, {rounds} 轮, 在页停留: {string.Join('/', dwells)}ms");
        Log($"[pl-latency] 内存快照驻留成本: {songBytes / 1024.0:F0}KB " +
            $"(单首约 {songBytes / 1024.0 / rows:F2}KB,上限 {8} 个页面)");

        // 诊断:落盘耗时里有多少是序列化、多少是容量裁剪的目录扫描(缓存目录通常上万文件)。
        if (Environment.GetEnvironmentVariable("ALY_PL_WRITE_PROBE") == "1")
        {
            foreach (var probeRows in new[] { 0, rows })
            {
                var probeSongs = CreateSongs(probeRows);
                var probeKey = Guid.NewGuid().ToString("N");
                var probePayload = new PlaylistPageCacheData(
                    probeSongs.Select(song => new NavigationPageCacheTrack(song, true)).ToList(),
                    probeSongs.Select(song => song.Id).ToArray(),
                    probeRows,
                    null);
                var probeWatch = Stopwatch.StartNew();
                await cache.CachePlaylistPageSnapshotAsync(probeKey, probePayload);
                var total = probeWatch.ElapsedMilliseconds;
                Log($"[pl-latency] 落盘诊断 {probeRows} 首: 总 {total}ms " +
                    $"(扣掉 3s 延迟窗口 = 实际落盘约 {Math.Max(0, total - 3000)}ms)");
                await cache.DiscardPlaylistPageSnapshotAsync(probeKey);
            }
        }

        for (var round = 1; round <= rounds; round++)
        {
            var dwell = dwells[(round - 1) % dwells.Length];
            var key = Guid.NewGuid().ToString("N");
            var payload = new PlaylistPageCacheData(
                songs.Select(song => new NavigationPageCacheTrack(song, true, true)).ToList(),
                songs.Select(song => song.Id).ToArray(),
                songs.Count,
                null);

            // 与生产一致:派发后不等待(内存副本立刻可用,落盘是延后的兜底)。
            _ = cache.CachePlaylistPageSnapshotAsync(key, payload);

            var snapshot = new PlaylistNavigationSnapshot(
                key,
                PlaylistPageKind.NetEase,
                new Playlist
                {
                    Id = 77,
                    Source = MusicSource.NetEase,
                    Name = "歌单返回延迟探针",
                    TrackCount = songs.Count,
                },
                null,
                "歌单返回延迟探针",
                "测试用户",
                0,
                "",
                false,
                0);

            // 铺底:恢复成"已滚动到底部"的状态
            await vm.RestoreNavigationSnapshotAsync(snapshot);
            await DrainUiAsync(400);
            var extent = scroller.Extent.Height;
            var viewport = scroller.Viewport.Height;
            var bottom = Math.Max(0, extent - viewport);
            scroller.Offset = new(0, bottom);
            await DrainUiAsync(200);
            vm.UpdatePageScrollOffset(scroller.Offset.Y);

            // 离页:捕获快照并释放重数据(与点歌手名离开时同一路径)
            Stages.Clear();
            Heartbeats.Clear();
            Clock.Restart();
            StartHeartbeat();

            var captured = vm.CaptureAndReleaseNavigationSnapshot()
                           ?? throw new InvalidOperationException("未生成导航快照");
            var releasedAt = Clock.ElapsedMilliseconds;

            // 模拟"在歌手页停留":写入是在离页那一刻派发的,停留越久越可能已经写完。
            if (dwell > 0) await Task.Delay(dwell);
            var returningAt = Clock.ElapsedMilliseconds;

            // 返回:UI 线程上发起恢复(与 MainViewModel.RestoreNavigation 一致)
            var restoreStartedAt = Clock.ElapsedMilliseconds;
            _ = vm.RestoreNavigationSnapshotAsync(captured);
            await WaitUntilAsync(() => vm.Tracks.Count == rows, TimeSpan.FromSeconds(10));
            var rowsAt = Clock.ElapsedMilliseconds;

            await WaitUntilAsync(
                () => Math.Abs(scroller.Offset.Y - Math.Min(bottom, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height))) < 1,
                TimeSpan.FromSeconds(5));
            var scrolledAt = Clock.ElapsedMilliseconds;
            await DrainUiAsync(200);
            StopHeartbeat();

            var peakFreeze = 0L;
            for (var index = 1; index < Heartbeats.Count; index++)
                peakFreeze = Math.Max(peakFreeze, Heartbeats[index] - Heartbeats[index - 1]);

            Log($"[pl-latency] 第 {round} 轮(停留={dwell}ms): " +
                $"| 离页释放={releasedAt}ms 返回发起={returningAt}ms " +
                $"→有行={rowsAt - restoreStartedAt}ms →滚动到位={scrolledAt - restoreStartedAt}ms " +
                $"| UI 冻结峰值={peakFreeze}ms | 心跳={Heartbeats.Count}");
            foreach (var stage in Stages)
                Log($"[pl-latency]      {stage.At,5}ms  {stage.Stage}");

            vm.ReleaseCurrentPageData();
            await DrainUiAsync(200);
        }

        window.Close();
        // 等过落盘延迟窗口:每轮的快照都已被返回消费,理论上不该留下任何磁盘文件。
        await Task.Delay(4000);
        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALyricEase", "cache", "music");
        var leftovers = Directory.Exists(cacheDirectory)
            ? Directory.GetFiles(cacheDirectory, "n-*.snapshot").Length
            : 0;
        Log($"[pl-latency] 完成: 遗留快照文件={leftovers} 个(本会话应只保留未被消费的)");
        return 0;
    }

    private static DispatcherTimer? _beatTimer;

    private static void StartHeartbeat()
    {
        _beatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Background,
            (_, _) => Heartbeats.Add(Clock.ElapsedMilliseconds));
        _beatTimer.Start();
    }

    private static void StopHeartbeat()
    {
        _beatTimer?.Stop();
        _beatTimer = null;
    }

    private static void Collect()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) return;
            await Task.Delay(1);
        }
    }

    private static async Task DrainUiAsync(int milliseconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    private static List<Song> CreateSongs(int count) => Enumerable.Range(1, count).Select(index => new Song
    {
        Id = 700_000 + index,
        Source = MusicSource.NetEase,
        Name = $"返回延迟歌曲 {index:D4}",
        Artist = "返回延迟歌手",
        Album = "返回延迟专辑",
        DurationMs = 180_000,
        ArtistIds = [9101],
        ArtistNames = ["返回延迟歌手"],
        AlbumId = 9201,
    }).ToList();

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { }
    }
}
