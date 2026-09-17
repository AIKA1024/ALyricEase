using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// 歌单页后台 CPU 复现(--pl-cpu-real):用户报告"打开过歌单页之后,窗口最小化仍有 ~0.8% 占用;
/// 在歌单页快速滚到底后停止滚动,封面加载很慢且 CPU 一直 ~10%"。
///
/// 探针要回答的是**归因**,不是"重现一个数字":
///   1) 同一窗口在"空页"与"真实 PlaylistView"之间切换,窗口状态(可见/最小化)与列表位置
///      (顶部/底部)作为自变量 —— 找出 0.8% 是"歌单页挂在树上"带来的,还是"滚到底"带来的;
///   2) 把"滚到底"场景的**实化行控件数 / 视觉树节点数 / 解码图缓存**一起打出来 ——
///      如果行控件没有虚拟化(600 首全实化),它同时解释"封面慢"(UI 线程被布局占满,
///      解码结果排不进 UI 队列)与"CPU 10%";
///   3) 按**线程**拆 CPU:UI 线程占多数 ⇒ 每帧的 UI 工作(动画/布局/失效);
///      线程池占多数 ⇒ 后台循环(解码/磁盘/网络)。这一条决定了下一步该查哪里。
///
/// 口径与 --shell-cpu-real 保持一致:单核百分比(不除核数),窗口 6 秒,ALY_PL_ROWS 控制规模。
/// </summary>
internal static class PlaylistPageCpuProbe
{
    /// <summary>单个测量窗口(毫秒)。</summary>
    private const int SampleMs = 6000;

    /// <summary>状态切换后的沉降时间:等布局、封面请求与过渡动画落地。</summary>
    private const int SettleMs = 900;

    private static uint _uiThreadId;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[pl-cpu] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };

        _uiThreadId = GetCurrentThreadId();
        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[pl-cpu] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        var process = Process.GetCurrentProcess();
        var rows = EnvInt("ALY_PL_ROWS", 600);
        var rounds = EnvInt("ALY_PL_ROUNDS", 1);
        var realCovers = Env("ALY_PL_REAL_COVERS") == "1";
        var cacheFiles = EnvInt("ALY_PL_CACHE_FILES", realCovers ? 13_000 : 0);

        var vm = ServiceLocator.Get<PlaylistViewModel>();
        var cache = ServiceLocator.Get<MusicCacheService>();
        vm.ReleaseCurrentPageData();

        var host = new Grid();
        var window = new Window
        {
            Width = 1400,
            Height = 900,
            Content = host,
            ShowActivated = false,
        };
        window.Show();
        await DrainAsync(600);

        var view = new PlaylistView { DataContext = vm };
        Log($"[pl-cpu] 装配: 规模={rows} 首, 轮数={rounds}, 窗口={window.ClientSize.Width:F0}x{window.ClientSize.Height:F0}, " +
            $"UI 线程={_uiThreadId}, 图片预算={ImageMemoryBudget.Describe()}");

        // 真实封面场景:把封面管线指向一个"文件数量级与真实用户缓存一致"的临时目录。
        // 为什么不直接用真实缓存目录:写会触发 LRU 淘汰,可能删掉用户已缓存的音频 ——
        // 这里只复制"目录里有上万个文件"这个自变量,用户的数据一个字节都不动。
        string? tempCacheRoot = null;
        if (cacheFiles > 0)
        {
            tempCacheRoot = CreateDummyCacheDirectory(cacheFiles);
            CoverImagePipeline.Configure(new MusicCacheService(512, tempCacheRoot, new HttpClient()));
            Log($"[pl-cpu] 封面管线已切到临时缓存目录(上限 512MB,与用户同量级的 {cacheFiles} 个文件)");
        }

        var covers = realCovers ? await FetchRealCoverUrlsAsync(40) : [];
        Log($"[pl-cpu] 真实封面 URL: {covers.Count} 个" +
            (realCovers && covers.Count == 0 ? "(取不到 — 后面的封面场景会退化成无封面)" : ""));

        var songs = CreateSongs(rows, covers);
        var key = Guid.NewGuid().ToString("N");
        var payload = new PlaylistPageCacheData(
            songs.Select(song => new NavigationPageCacheTrack(song, true, true)).ToList(),
            songs.Select(song => song.Id).ToArray(),
            songs.Count,
            null);
        _ = cache.CachePlaylistPageSnapshotAsync(key, payload);
        var snapshot = new PlaylistNavigationSnapshot(
            key,
            PlaylistPageKind.NetEase,
            new Playlist
            {
                Id = 77,
                Source = MusicSource.NetEase,
                Name = "歌单页后台 CPU 探针",
                TrackCount = songs.Count,
            },
            null,
            "歌单页后台 CPU 探针",
            "测试用户",
            0,
            "",
            false,
            0);
        await vm.RestoreNavigationSnapshotAsync(snapshot);
        Log($"[pl-cpu] 快照已恢复: 行={vm.RetainedTrackRowCount} 队列={vm.RetainedQueueSongCount} " +
            $"索引={vm.RetainedTrackIdCount}");

        var flatRows = new List<Row>();

        for (var round = 1; round <= rounds; round++)
        {
            // ---------- 场景 1/2:空页(基线)。整个窗口只有宿主 Grid,量"平台静息 + 窗口不可见"底座 ----------
            AttachAsync(host, null, view);
            flatRows.Add(await CaptureAsync(process, window, view, "空页", false, round));
            flatRows.Add(await CaptureAsync(process, window, view, "空页", true, round));

            // ---------- 场景 3/4:歌单页(顶部)。此时所有行都在 Tracks 里,但视口只实化前若干行 ----------
            AttachAsync(host, view, view);
            await DrainAsync(SettleMs);
            var scroller = view.FindControl<ScrollViewer>("PageScroller")
                           ?? throw new InvalidOperationException("找不到歌单滚动容器");
            scroller.Offset = default;
            await DrainAsync(400);
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·顶部", false, round));
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·顶部", true, round));

            // ---------- 场景 5:歌单页(滚到底)—— 用户报告的那一态 ----------
            var extent = scroller.Extent.Height;
            var viewport = scroller.Viewport.Height;
            var bottom = Math.Max(0, extent - viewport);
            scroller.Offset = new(0, bottom);
            await WaitUntilAsync(() => Math.Abs(scroller.Offset.Y - Math.Max(
                0, scroller.Extent.Height - scroller.Viewport.Height)) < 1, TimeSpan.FromSeconds(5));
            await DrainAsync(SettleMs);
            Log($"[pl-cpu] 已滚到底: offset={scroller.Offset.Y:F0} extent={scroller.Extent.Height:F0} " +
                $"viewport={scroller.Viewport.Height:F0} 行={vm.Tracks.Count} 补页中={vm.IsLoadingMore}");

            // 封面出齐耗时(用户症状:"图片加载特别慢")。只有真封面场景才有意义。
            if (covers.Count > 0)
                flatRows.Add(await CaptureCoverFillAsync(process, window, view, round));

            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·底部", false, round));
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·底部", true, round));

            // ---------- 场景 5a:滚到"接近底部但不在补页区"—— 单变量切分 ----------
            // 底部那一态有两个自变量:①滚得远(实化行更多);②remaining<500,OnTracksScrollChanged
            // 会打一次 LoadMoreAsync。把位置退到 remaining>1200 再量,就能把②单独摘出来看。
            var nearBottom = Math.Max(0, bottom - 1700);
            scroller.Offset = new(0, nearBottom);
            await DrainAsync(SettleMs);
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·近底(不补页)", false, round));
            Log($"[pl-cpu] 近底位置: offset={scroller.Offset.Y:F0} " +
                $"剩余={scroller.Extent.Height - scroller.Offset.Y - scroller.Viewport.Height:F0}px " +
                $"补页中={vm.IsLoadingMore} (>500 才不会触发补页分支)");

            scroller.Offset = new(0, bottom);
            await DrainAsync(SettleMs);

            // ---------- 场景 5b:静止稳定性 —— "什么都不做"时页面到底在不在变 ----------
            // 可见页面的 CPU 不是 0 时只有两种可能:①内容在动(两帧像素差 > 0,去找动画源);
            // ②空转重绘(像素完全一致却仍在出帧,去找无谓的失效源)。两条路的修法完全不同,
            // 所以先把这一刀切开,别拿 CPU 读数直接猜动画。
            await LogStabilityAsync(window, scroller, view, vm);

            // ---------- 场景 6:补页提示条(IsIndeterminate 的 ProgressBar)----------
            // 这条动画只要控件可见就会一直跑,与"窗口在不在渲染"无关,是"最小化后仍有零点几个
            // 百分点"的候选解释之一。单独量一次,避免与封面写盘的开销混为一谈。
            vm.IsLoadingMore = true;
            await DrainAsync(600);
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·补页提示", false, round));
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·补页提示", true, round));

            // ---------- 场景 6-对照:同一次运行内绕过门控 ----------
            // 手动把提示条改回 IsIndeterminate=true(等价"未门控"的旧版本)。这样上面一条与这条
            // 的差异只剩"门控开/关"这一个变量,比跨构建对比更硬。窗口在最小化时改状态不会触发
            // 门控重算(WindowState 没变化),所以强行设的值能稳定存活到采样结束。
            var gatedBar = view.GetVisualDescendants().OfType<ProgressBar>()
                .FirstOrDefault(bar => IndeterminateAnimationGate.GetIsActive(bar));
            if (gatedBar is not null)
            {
                gatedBar.IsIndeterminate = true;
                flatRows.Add(await CaptureAsync(process, window, view, "歌单页·补页提示·绕过门控", true, round));
                gatedBar.IsIndeterminate = false;
            }
            else
            {
                Log("[pl-cpu] ⚠ 没找到挂了 IndeterminateAnimationGate 的 ProgressBar,对照组跳过" +
                    "(检查 axaml 里的 infra:IndeterminateAnimationGate.IsActive)");
            }

            // ---------- 场景 6b:视图仍实化、但被隐藏(切到别的页面的常态)----------
            // 窗口保持可见,只把页面 IsVisible=false。这决定门控要不要看"控件自身(含祖先)可见性":
            // 若这一态仍然有开销,说明只看 TopLevel 状态不够,得再补一条 IsEffectivelyVisible 判据。
            view.IsVisible = false;
            await DrainAsync(600);
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·隐藏·补页提示", false, round));
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页·隐藏·补页提示", true, round));
            view.IsVisible = true;
            vm.IsLoadingMore = false;
            await DrainAsync(300);

            // ---------- 场景 7:把歌单页摘出视觉树,窗口保持最小化 ----------
            // 判据:如果这一态仍不归零,说明成本不在页面控件上,而在单例 VM / 后台任务 / 平台层面。
            AttachAsync(host, null, view);
            await DrainAsync(SettleMs);
            flatRows.Add(await CaptureAsync(process, window, view, "歌单页已离屏", true, round));

            if (round == rounds && tempCacheRoot is not null)
                Log($"[pl-cpu] 临时缓存目录写入量: {DescribeCache(tempCacheRoot)}");
        }

        Report(flatRows);
        window.Close();
        await DrainAsync(300);
        if (tempCacheRoot is not null)
        {
            try { Directory.Delete(tempCacheRoot, true); } catch { }
        }
        Log("[pl-cpu] 完成");
        return 0;
    }

    /// <summary>
    /// 静止稳定性:在"歌单页·底部"这一态上连采 2 秒,看滚动状态/实化行数/视觉节点数有没有漂移,
    /// 以及首尾两帧的像素差。三者合起来能把"有 CPU"拆成:布局自激(状态在漂)、真动画(像素差大)、
    /// 空转重绘(状态与像素都不变,却仍在出帧)。
    /// ⚠ 依赖抓屏,窗口必须先置顶 —— 被别的窗口盖住时 BitBlt 抓到的是遮挡物,像素差无意义。
    /// </summary>
    private static async Task LogStabilityAsync(
        Window window, ScrollViewer scroller, PlaylistView view, PlaylistViewModel vm)
    {
        window.Topmost = true;
        window.Activate();
        await DrainAsync(400);

        // 数 ScrollChanged:滚动位置静止时它本不该触发。若这里在 2 秒内刷出几十上百次,
        // 说明"页面自身在改布局"——PlaylistView 的处理器会在 remaining<500 时打 LoadMoreAsync,
        // 而 LoadMore 收尾会切 IsLoadingMore(±40px 布局) ⇒ 又能触发下一次 ScrollChanged。
        var scrollEvents = 0;
        var scrollSamples = new List<string>();
        void OnScroll(object? sender, ScrollChangedEventArgs args)
        {
            scrollEvents++;
            if (scrollSamples.Count < 6)
                scrollSamples.Add($"Δoffset={args.OffsetDelta.Y:+0.0;-0.0;0} " +
                                  $"Δextent={args.ExtentDelta.Y:+0.0;-0.0;0} " +
                                  $"Δviewport={args.ViewportDelta.Y:+0.0;-0.0;0}");
        }

        scroller.ScrollChanged += OnScroll;
        var offsets = new List<double>();
        var extents = new List<double>();
        var maximums = new List<double>();
        var rows = new List<int>();
        var nodes = new List<int>();
        for (var index = 0; index < 10; index++)
        {
            offsets.Add(scroller.Offset.Y);
            extents.Add(scroller.Extent.Height);
            maximums.Add(scroller.ScrollBarMaximum.Y);
            rows.Add(CountRows(view));
            nodes.Add(CountNodes(view));
            await DrainAsync(200);
        }

        scroller.ScrollChanged -= OnScroll;
        Log($"[pl-cpu] 静止稳定性(2s): ScrollChanged {scrollEvents} 次" +
            (scrollSamples.Count > 0 ? " 前几次: " + string.Join(" | ", scrollSamples) : "") +
            (scrollEvents > 20 ? " ⇒ 页面自己在反复改布局(滚轮静止却一直在滚)" : " ⇒ 无自激"));

        var first = ScreenCapture.GrabClient(window);
        await DrainAsync(500);
        var second = ScreenCapture.GrabClient(window);
        var diff = ScreenCapture.DiffRatio(first, second);

        Log($"[pl-cpu] 静止稳定性(2s): offset {Spread(offsets)} extent {Spread(extents)} " +
            $"最大值 {Spread(maximums)} 实化行 {SpreadInt(rows)} 节点 {SpreadInt(nodes)}");
        Log($"[pl-cpu] 静止稳定性(2s): 两帧像素差={diff:F4} 画布={first.Width}x{first.Height} " +
            $"⇒ {(double.IsNaN(diff) ? "抓屏失败" : diff <= 0.0005
                ? "像素完全一致 ⇒ 空转重绘(有东西在请求帧,但没有可见变化)"
                : "像素在变 ⇒ 有可见动画在跑")} 行={vm.Tracks.Count} 补页中={vm.IsLoadingMore}");

        window.Topmost = false;
    }

    private static string Spread(List<double> values) =>
        $"{values.Min():F1}~{values.Max():F1}(Δ{values.Max() - values.Min():F2})";

    private static string SpreadInt(List<int> values) =>
        $"{values.Min()}~{values.Max()}(Δ{values.Max() - values.Min()})";

    /// <summary>
    /// 封面出齐窗口:滚到底后窗口保持可见,每 500ms 采一次"已出图的 Image 数 / 图片总数"与解码图缓存,
    /// 记录**第一个全部出齐的时刻**以及这段时间的 CPU。
    /// 这是用户描述的直接指标 —— 修复前每条封面落盘要全目录扫描(上万文件 ≈ 550ms)且持全局写锁,
    /// 封面填图会被拖到几十秒量级。
    /// </summary>
    private static async Task<Row> CaptureCoverFillAsync(
        Process process, Window window, PlaylistView view, int round)
    {
        if (!window.IsVisible) window.Show();
        window.WindowState = WindowState.Normal;
        await DrainAsync(400);

        var images = CountImages(view);
        var watch = Stopwatch.StartNew();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var loaded = 0;
        var filledAt = -1L;
        // ⚠️ 必须分成"上次计数"和"上次变化时刻"两个变量。曾把计数直接当时间戳用
        // （`loaded != lastProgressAt` 再 `now - lastProgressAt`），计数 13 与 13000ms 永远不等
        // ⇒ else 分支永不执行 ⇒ 收敛判定失效，每次都跑满 deadline。
        var lastProgressCount = -1;
        var lastProgressMs = 0L;
        var nextLogAt = 0L;
        var deadline = TimeSpan.FromSeconds(EnvInt("ALY_PL_COVER_BUDGET_S", 90));

        while (watch.Elapsed < deadline)
        {
            loaded = CountLoadedImages(view);
            var now = watch.ElapsedMilliseconds;
            if (loaded >= images && images > 0)
            {
                filledAt = now;
                break;
            }
            if (now >= nextLogAt)
            {
                nextLogAt = now + 1000;
                var cacheStats = CoverImagePipeline.MemoryCacheStats;
                Log($"[pl-cpu]   封面填图 {now,6}ms: 已出图={loaded,3}/{images} " +
                    $"解码图={cacheStats.Items,3}项 {cacheStats.Bytes / 1024.0 / 1024.0:F1}MB");
            }
            // 连续 5 秒没有新图 ⇒ 认为已经收敛(可能有个别 URL 本来就失败)。
            if (loaded != lastProgressCount) { lastProgressCount = loaded; lastProgressMs = now; }
            else if (now - lastProgressMs > 5000) break;
            await Task.Delay(250);
        }
        watch.Stop();
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        var seconds = watch.Elapsed.TotalSeconds;
        var percent = seconds > 0 ? cpu / seconds * 100 : 0;
        var cacheAfter = CoverImagePipeline.MemoryCacheStats;

        Log($"[pl-cpu] R{round} 封面出齐 已出图={CountLoadedImages(view),3}/{images} " +
            $"耗时={(filledAt >= 0 ? filledAt : watch.ElapsedMilliseconds)}ms " +
            $"窗口 CPU={percent:F2}% 解码图={cacheAfter.Items,3}项/{cacheAfter.Bytes / 1024.0 / 1024.0:F1}MB");
        return new Row("重置前", false, round, percent, 0, 0, 0, images,
            cacheAfter.Items, cacheAfter.Bytes, $"fill={filledAt}", $"封面耗时 {watch.ElapsedMilliseconds}ms");
    }

    private static int CountLoadedImages(PlaylistView view)
    {
        try { return view.GetVisualDescendants().OfType<Image>().Count(image => image.Source is not null); }
        catch { return -1; }
    }

    private static async Task<List<string>> FetchRealCoverUrlsAsync(int wanted)
    {
        var result = new List<string>();
        try
        {
            var api = ServiceLocator.Get<NetEaseApiClient>();
            foreach (var keyword in new[] { "周杰伦", "林俊杰", "陈奕迅", "五月天", "邓紫棋" })
            {
                var songs = await api.SearchAsync(keyword, 30);
                foreach (var song in songs)
                    if (song.CoverUrl.Length > 0 && !result.Contains(song.CoverUrl))
                        result.Add(song.CoverUrl);
                if (result.Count >= wanted) break;
            }
        }
        catch (Exception ex)
        {
            Log($"[pl-cpu] 真实封面 URL 获取失败(网络?): {ex.Message}");
        }
        return result.Take(wanted).ToList();
    }

    private static string CreateDummyCacheDirectory(int fileCount)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aly-pl-cpu-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var payload = new byte[20 * 1024];
        for (var index = 0; index < fileCount; index++)
            File.WriteAllBytes(Path.Combine(root, $"a-probe-{index:D6}-99.mp3"), payload);
        return root;
    }

    private static string DescribeCache(string root)
    {
        try
        {
            var files = Directory.EnumerateFiles(root).ToList();
            var bytes = files.Sum(path => new FileInfo(path).Length);
            return $"{files.Count} 个文件 / {bytes / 1024.0 / 1024.0:F1}MB";
        }
        catch
        {
            return "(统计失败)";
        }
    }

    /// <summary>把 view 挂到/摘下宿主容器。window.Content 不变,避免换窗口引入额外变量。</summary>
    private static void AttachAsync(Grid host, Control? control, PlaylistView view)
    {
        host.Children.Clear();
        if (control is null)
        {
            view.DataContext = null; // 离屏时断开绑定,等价于切页销毁视图
        }
        else
        {
            view.DataContext = ServiceLocator.Get<PlaylistViewModel>();
            host.Children.Add(control);
        }

        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<Row> CaptureAsync(
        Process process,
        Window window,
        PlaylistView view,
        string scene,
        bool minimized,
        int round)
    {
        if (!window.IsVisible) window.Show();
        window.WindowState = minimized ? WindowState.Minimized : WindowState.Normal;
        await DrainAsync(SettleMs);

        // 结构读数在测量窗口之前采:它们是"这一态长什么样",不是代价本身。
        var total = ServiceLocator.Get<PlaylistViewModel>().Tracks.Count;
        var realizedRows = CountRows(view);
        var nodes = CountNodes(view);
        var images = CountImages(view);
        var cacheStats = CoverImagePipeline.MemoryCacheStats;
        var state = $"state={window.WindowState},visible={window.IsVisible}";

        var before = SnapshotThreads(process);
        var gcCounts = GcCounts();
        var allocatedStart = GC.GetTotalAllocatedBytes(false);
        var cpuStart = process.TotalProcessorTime;
        var wallStart = Stopwatch.GetTimestamp();
        await DrainAsync(SampleMs);
        var wall = Stopwatch.GetElapsedTime(wallStart).TotalSeconds;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        var allocated = (GC.GetTotalAllocatedBytes(false) - allocatedStart) / 1024.0 / 1024.0;
        var gcDelta = GcDelta(gcCounts);
        var after = SnapshotThreads(process);

        var threads = DescribeThreads(before, after, wall);
        var row = new Row(scene, minimized, round, wall > 0 ? cpu / wall * 100 : 0,
            realizedRows, total, nodes, images, cacheStats.Items, cacheStats.Bytes, state, threads);
        Log($"[pl-cpu] R{round} {scene,-14} {(minimized ? "最小化" : "可见 "),-4} " +
            $"CPU={row.Cpu,6:F2}% | 实化行={realizedRows,4}/{total} 视觉节点={nodes,6} 图片={images,4} " +
            $"解码图={cacheStats.Items,4}项/{cacheStats.Bytes / 1024.0 / 1024.0:F1}MB " +
            $"| 分配={allocated,6:F1}MB GC={gcDelta} | {state}");
        Log($"[pl-cpu]      线程: {threads}");
        return row;
    }

    private static int CountRows(PlaylistView view)
    {
        try { return view.GetVisualDescendants().OfType<TrackRow>().Count(); }
        catch { return -1; }
    }

    private static int CountNodes(PlaylistView view)
    {
        try { return view.GetVisualDescendants().Count(); }
        catch { return -1; }
    }

    private static int CountImages(PlaylistView view)
    {
        try { return view.GetVisualDescendants().OfType<Image>().Count(); }
        catch { return -1; }
    }

    /// <summary>当前三代 GC 的收集次数。用来把"窗口 CPU 高"拆成"真在干活"还是"垃圾堆着、GC 来收"。</summary>
    private static (int Gen0, int Gen1, int Gen2) GcCounts() =>
        (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    private static string GcDelta((int Gen0, int Gen1, int Gen2) before)
    {
        var now = GcCounts();
        return $"0:+{now.Gen0 - before.Gen0} 1:+{now.Gen1 - before.Gen1} 2:+{now.Gen2 - before.Gen2}";
    }

    private static Dictionary<int, TimeSpan> SnapshotThreads(Process process)
    {
        var map = new Dictionary<int, TimeSpan>();
        try { process.Refresh(); } catch { }
        foreach (ProcessThread thread in process.Threads)
        {
            try { map[thread.Id] = thread.TotalProcessorTime; }
            catch { /* 线程已退出 */ }
        }
        return map;
    }

    /// <summary>按线程拆 CPU —— 归因的关键一步:UI 线程 vs 线程池。</summary>
    private static string DescribeThreads(
        Dictionary<int, TimeSpan> before, Dictionary<int, TimeSpan> after, double seconds)
    {
        if (seconds <= 0) return "(窗口过短)";
        var deltas = new List<(int Id, double Percent)>();
        foreach (var (id, end) in after)
        {
            if (!before.TryGetValue(id, out var start)) continue;
            var percent = (end - start).TotalSeconds / seconds * 100;
            if (percent <= 0.02) continue;
            deltas.Add((id, percent));
        }
        deltas.Sort((a, b) => b.Percent.CompareTo(a.Percent));
        if (deltas.Count == 0) return "(无活动线程)";
        return string.Join("  ", deltas.Take(4).Select(entry =>
            $"{(entry.Id == (int)_uiThreadId ? "UI线程" : $"线程{entry.Id}")}={entry.Percent:F2}%"));
    }

    private static void Report(List<Row> rows)
    {
        Log("[pl-cpu] ===== 判定:各场景 CPU(单核百分比) =====");
        foreach (var row in rows)
            Log($"[pl-cpu] {row.Scene,-14} {(row.Minimized ? "最小化" : "可见 "),-4} " +
                $"CPU={row.Cpu,6:F2}%  实化行={row.RealizedRows,4} 节点={row.Nodes,6} 解码图={row.CacheItems,4}");

        Log("[pl-cpu] ===== 判定:最小化下的额外成本(相对空页基线) =====");
        var baseline = rows.Where(row => row.Scene == "空页" && row.Minimized).ToList();
        var baseCpu = baseline.Count > 0 ? baseline[0].Cpu : double.NaN;
        foreach (var scene in new[] { "歌单页·顶部", "歌单页·底部", "歌单页·补页提示", "歌单页·隐藏·补页提示", "歌单页已离屏" })
        {
            var samples = rows.Where(row => row.Scene == scene && row.Minimized).ToList();
            if (samples.Count == 0) continue;
            var cpu = samples.Average(row => row.Cpu);
            Log($"[pl-cpu] {scene,-14} CPU={cpu:F2}% ⇒ 相对空页基线 {(cpu - baseCpu):+0.00;-0.00}%");
        }

        Log("[pl-cpu] ===== 判定:实化行控件是否随列表长度放大 =====");
        foreach (var row in rows.Where(row => row.Scene.StartsWith("歌单页") && !row.Minimized))
            Log($"[pl-cpu] {row.Scene,-14} 实化行={row.RealizedRows} 视觉节点={row.Nodes} " +
                $"⇒ {(row.RealizedRows > 200 ? "疑似未虚拟化(行控件随列表规模放大)" : "行控件受虚拟化约束")}");

        Log("[pl-cpu] ===== 判定:不确定进度条(补页提示)的净代价 =====");
        var idleMin = rows.FirstOrDefault(row => row.Scene == "歌单页·底部" && row.Minimized);
        var busyMin = rows.FirstOrDefault(row => row.Scene == "歌单页·补页提示" && row.Minimized);
        if (idleMin is not null && busyMin is not null)
            Log($"[pl-cpu] 最小化: 无提示条={idleMin.Cpu:F2}% vs 有提示条={busyMin.Cpu:F2}% " +
                $"⇒ 净代价 {busyMin.Cpu - idleMin.Cpu:+0.00;-0.00}%");
        // 同轮对照组:唯一变量是"门控有没有生效"。这条才是本修复成立与否的判据。
        var bypassMin = rows.FirstOrDefault(row => row.Scene == "歌单页·补页提示·绕过门控" && row.Minimized);
        if (bypassMin is not null && busyMin is not null)
        {
            var saved = bypassMin.Cpu - busyMin.Cpu;
            Log($"[pl-cpu] 门控效果(同轮 A/B): 绕过门控={bypassMin.Cpu:F2}% vs 门控生效={busyMin.Cpu:F2}% " +
                $"⇒ 省下 {saved:+0.00;-0.00}% 单核 " +
                $"{(saved > 0.5 ? "(门控生效,这就是最小化残留占用的来源)" : "(差值在噪声内 ⇒ 门控没起作用或动画已不产生成本,须复查)")}");
        }
        var idleVis = rows.FirstOrDefault(row => row.Scene == "歌单页·底部" && !row.Minimized);
        var busyVis = rows.FirstOrDefault(row => row.Scene == "歌单页·补页提示" && !row.Minimized);
        if (idleVis is not null && busyVis is not null)
            Log($"[pl-cpu] 可见:   无提示条={idleVis.Cpu:F2}% vs 有提示条={busyVis.Cpu:F2}% " +
                $"⇒ 净代价 {busyVis.Cpu - idleVis.Cpu:+0.00;-0.00}%");
        // 页面被隐藏(切页)时提示条还转不转。**这条不作为门控的设计依据**:两次运行对同一状态
        // 量到的值差得很远(0.26% vs 14.83%,另一次 4.16%),不足以支撑结论 ⇒ 原样打印,别下判断。
        // 门控只看 TopLevel 的理由见 IndeterminateAnimationGate 的类注释。
        var hiddenVis = rows.FirstOrDefault(row => row.Scene == "歌单页·隐藏·补页提示" && !row.Minimized);
        var busyVis2 = rows.FirstOrDefault(row => row.Scene == "歌单页·补页提示" && !row.Minimized);
        var hiddenMin = rows.FirstOrDefault(row => row.Scene == "歌单页·隐藏·补页提示" && row.Minimized);
        if (hiddenVis is not null && busyVis2 is not null)
            Log($"[pl-cpu] 页面 IsVisible=false(窗口可见): {hiddenVis.Cpu:F2}% vs 同状态不隐藏 {busyVis2.Cpu:F2}% " +
                $"⇒ 该态跨轮不一致,仅作记录(本应用导航是「离开视觉树」,不产生这一态;门控不依赖它)");
        if (hiddenMin is not null && idleMin is not null)
            Log($"[pl-cpu] 页面隐藏×最小化: {hiddenMin.Cpu:F2}% (对照 最小化无提示条={idleMin.Cpu:F2}%)");
    }

    private static List<Song> CreateSongs(int count, IReadOnlyList<string> covers) =>
        Enumerable.Range(1, count).Select(index => new Song
        {
            Id = 800_000 + index,
            Source = MusicSource.NetEase,
            Name = $"歌单页 CPU 歌曲 {index:D4}",
            Artist = "探针歌手",
            Album = "探针专辑",
            DurationMs = 180_000,
            ArtistIds = [9101],
            ArtistNames = ["探针歌手"],
            AlbumId = 9201,
            // 真实封面按轮转分配:歌单真实场景里同一专辑的歌共享封面,40 个不同 URL 足够压出下载/落盘路径。
            CoverUrl = covers.Count > 0 ? covers[index % covers.Count] : "",
        }).ToList();

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) return;
            await Task.Delay(5);
        }
    }

    private static async Task DrainAsync(int milliseconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { }
    }

    private sealed record Row(
        string Scene,
        bool Minimized,
        int Round,
        double Cpu,
        int RealizedRows,
        int TotalRows,
        int Nodes,
        int Images,
        int CacheItems,
        long CacheBytes,
        string State,
        string Threads);
}
