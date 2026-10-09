using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// 个人主页滚动的 GPU 归因探针(--home-scroll-gpu)。
///
/// 为什么单开:用户报"个人主页滚动时 GPU 能到 20%",而同一个外壳在静止态几乎不吃显卡。
/// 20% 有两个互斥的解释,必须分开量:
///   ① **滚动本身**:内容整体位移 ⇒ 每一帧整个视口都是脏区(与静止时"只脏一个小区域"
///      完全不同),再叠上透明窗 + 亚克力的每帧重合成 —— 代价 ≈ 出帧次数 × 每帧固定开销;
///   ② **这页的内容**:非虚拟化 UniformGrid 里几十张卡片、每张一个 360px 解码封面 +
///      圆角裁剪 —— 每帧要采样/缩放这么多位图。
/// 所以夹具是一个**真窗口 + 真 AppShell + 真个人主页**,同轮跑五档单变量消融:
///
///   主页·静止              —— 基线:窗口可见、页面在顶、什么都不动
///   主页·滚动·计时器        —— 每 16ms 改一次 ScrollViewer.Offset(实测 ~58fps,受 Windows 定时器粒度限制)
///   主页·滚动·满帧          —— 自续订 RequestAnimationFrame 改 Offset,节奏 = 合成器出帧节奏(真手滚的节奏)
///   主页·滚动·无封面        —— 同一页面、同一布局,只把所有 360px 封面 Image 的 Source 置空
///   主页·静止·无封面        —— 拆出"静态下封面在不在"这一格(通常≈0,用作噪声底)
///
/// 判据:
///   滚动−静止              = "让帧流起来"值多少
///   计时器档 vs 满帧档      = 只差"每秒改几次 Offset" ⇒ 验证「代价 ≈ 出帧次数 × 每帧固定开销」
///   滚动−滚动·无封面        = 封面位图/裁剪值多少(必须配合"有源封面张数"指纹,否则消融可能没生效)
///
/// ⚠ 20% 是"60fps 下的 20%"还是"165Hz 满帧下的 20%"结论完全不同 ⇒ 帧率必须是显式的一档。
/// ⚠ 帧率那一格刻意放在所有 GPU 采样**之后**:自续订 RAF 会主动把渲染管线叫醒,
///   放在前面会把"静止"档污染成"其实在出帧"。同理静止档启动前会先把 Offset 归零。
/// ⚠ 需要窗口**可见且未被遮挡**(GPU 计数只在真的提交到屏幕才有值),窗口置顶约一分钟。
/// ⚠ 封面走真实管线(真 URL、真 360px 解码),但写盘与缓存都重定向到临时目录,
///   不碰用户的封面缓存;先跑一轮预热把下载/首解码落地,测量轮里只剩"绘制"。
///
/// <para>
/// **页面轴**(<c>ALY_HOME_PAGE=home|playlist</c>,2026-10-08 加):用户实测"滚动歌单页只有
/// 5~6%,而主页能到 20%"。两页在同一窗口、同一驱动下量,**只能比 ⑤ 的每帧开销**(已除掉帧数):
/// 每帧开销一样 ⇒ 差值来自帧数(滚动节奏/掉帧),不是那页画得贵;歌单页明显更低 ⇒
/// 视口内确实有东西按内容计费,回去查两页视口不共有的构造。标签由 <see cref="BuildScenarios"/>
/// 按前缀生成 —— 硬写"主页·xxx"会让歌单页那轮全部读成 n/a。
/// </para>
/// </summary>
internal static class HomeScrollGpuProbe
{
    /// <summary>卡片封面的解码尺寸(与 PersonalHomeView 里 playlist-art 的 DecodeSize 一致)。</summary>
    private const int CardDecodeSize = 360;

    /// <summary>切态后等布局/封面落地再开始采样。</summary>
    private const int SettleMs = 1200;

    /// <summary>滚动的驱动方式。
    ///
    /// <para>
    /// Timer 档 ≈ 每 16ms 改一次 Offset;但 Windows 定时器粒度约 15.6ms,实测只到 ~58fps。
    /// Raf 档用自续订 RequestAnimationFrame 改 Offset,把出帧节奏顶到合成器允许的上限
    /// —— 用户在 165Hz 屏上真手滚时就是这个节奏。两档只差"每秒改几次 Offset",
    /// 正好用来验证 <c>代价 ≈ 出帧次数 × 每帧固定开销</c> 这条定律(而不是"滚了多少像素")。
    /// </para>
    /// </summary>
    private enum ScrollMode { None, Timer, Raf }

    /// <summary>收集一卡<b>完整状态</b>的档位:每档都自带封面开关与滚动方式,行与行可任意比较。
    ///
    /// <para>
    /// ⚠ 标签必须由本方法用**页面前缀**生成,不能硬写"主页·xxx":报告里所有取值都按标签匹配,
    /// 硬写的标签会让歌单页那轮全部取不到中位数(整段读成 n/a),而这正是加这条轴的目的。
    /// </para>
    /// <para>
    /// 歌单详情页不给封面档:那页没有 360px 卡片图(只有 DecodeSize=400 的 hero 与 50px 行缩略图),
    /// 拿卡片图的消融去测它只会白等两秒轮询,读到的指纹恒 0/0。
    /// </para>
    /// </summary>
    private static (string Label, bool Covers, ScrollMode Mode, int TickMs)[] BuildScenarios(
        string prefix, bool withCoverAblation)
    {
        (string Label, bool Covers, ScrollMode Mode, int TickMs)[] scenarios = withCoverAblation
            ?
            [
                ($"{prefix}·静止", true, ScrollMode.None, 0),
                ($"{prefix}·滚动·计时器", true, ScrollMode.Timer, 16),
                ($"{prefix}·滚动·满帧", true, ScrollMode.Raf, 0),
                ($"{prefix}·滚动·无封面", false, ScrollMode.Timer, 16),
                ($"{prefix}·静止·无封面", false, ScrollMode.None, 0),
            ]
            :
            [
                ($"{prefix}·静止", false, ScrollMode.None, 0),
                ($"{prefix}·滚动·计时器", false, ScrollMode.Timer, 16),
                ($"{prefix}·滚动·满帧", false, ScrollMode.Raf, 0),
            ];
        return scenarios;
    }

    private static uint _uiThreadId;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static Process? _dwm;

    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[home-scroll] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };

        _uiThreadId = GetCurrentThreadId();
        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[home-scroll] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        var process = Process.GetCurrentProcess();
        var sampleMs = EnvInt("ALY_HOME_SAMPLE_MS", 6000);
        var rounds = EnvInt("ALY_HOME_ROUNDS", 2);
        // ⚠ 这里允许 0:0 = **不注入**,直接用真实登录账号的资料库(卡片数、封面、分区形态都是真的)。
        var cards = EnvIntAllowingZero("ALY_HOME_CARDS", 64);
        // 页面轴:主页(非虚拟化卡片网格 + 大封面) vs 歌单详情页(虚拟化曲目行 + 小缩略图)。
        // 用户实测后者只有前者一半左右 ⇒ 这条轴把"两页在同一窗口、同一驱动下的每帧开销"摆到一起比,
        // 用来判定"代价只跟出帧次数走"这条定律到底成不成立(不成立就说明视口内有东西在按内容计费)。
        var page = Env("ALY_HOME_PAGE") == "playlist" ? "playlist" : "home";
        var scenarios = BuildScenarios(page == "playlist" ? "歌单" : "主页", page == "home");
        var step = EnvInt("ALY_HOME_SCROLL_PX", 24);
        var fpsMs = EnvInt("ALY_HOME_FPS_MS", 2500);
        // ⚠ 关键消融:主页顶部那条不确定进度条由 IsRefreshing 控制。
        // IndeterminateAnimationGate 自己的注释记着"可见时更贵:强制整个窗口每帧重合成(实测 5.7%~17.7%)"
        // ⇒ 若用户滚动时它正好在转,读数就是"两条出帧源叠在一起",而不是"滚动本身贵"。
        // 置 1 强制它保持可见(离线夹具里 EnsureLoadedAsync 早就跑完、本来会是 false)。
        var forceRefreshing = Env("ALY_HOME_REFRESHING") == "1";
        var (winWidth, winHeight) = ParseSize(Env("ALY_HOME_SIZE"), 1200, 720);

        // 封面写盘重定向:探针不该往用户的封面缓存里塞文件(见 --idle-cpu-real 的同款处理)。
        var tempRoot = Path.Combine(Path.GetTempPath(), "aly-home-scroll-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        CoverImagePipeline.Configure(new MusicCacheService(512, tempRoot, new HttpClient()));
        Log($"[home-scroll] 封面写盘重定向: {tempRoot}");

        _dwm = Process.GetProcessesByName("dwm").FirstOrDefault();

        var main = ServiceLocator.Get<MainViewModel>();
        var library = ServiceLocator.Get<PlaylistViewModel>();
        var home = main.PersonalHome;

        // 进主页会自动拉资料库(EnsureLoadedAsync 只跑一次)。先把它等掉,否则它的
        // RebuildPlaylists 会把我注入的卡片整片换掉(与 --idle-cpu-real 踩过的是同一件事)。
        await home.EnsureLoadedAsync();
        await DrainAsync(800);

        if (cards > 0)
        {
            var covers = await FetchRealCoverUrlsAsync(24);
            InjectCards(library, cards, covers);
            await DrainAsync(600);
            Log($"[home-scroll] 注入卡片={home.TastePlaylists.Count + home.CreatedPlaylists.Count + home.CollectedPlaylists.Count} " +
                $"(喜欢 {home.TastePlaylists.Count} / 创建 {home.CreatedPlaylists.Count} / 收藏 {home.CollectedPlaylists.Count}) " +
                $"真实封面 URL={covers.Count} 个" +
                (covers.Count == 0 ? " ⚠ 拿不到真实封面,封面消融两行不可信" : ""));
        }
        else
        {
            Log($"[home-scroll] 用真实资料库(不注入): 喜欢 {home.TastePlaylists.Count} / " +
                $"创建 {home.CreatedPlaylists.Count} / 收藏 {home.CollectedPlaylists.Count} ⇒ " +
                $"合计 {home.TastePlaylists.Count + home.CreatedPlaylists.Count + home.CollectedPlaylists.Count} 张卡片");
        }

        var window = new TestMainWindow
        {
            DataContext = main,
            ShowActivated = false,
            Width = winWidth,
            Height = winHeight,
        };
        window.Show();
        window.Topmost = true;   // 保证不被遮挡:被遮挡时合成器不出帧,读数必然归零(假"很省")
        window.Activate();
        await DrainAsync(900);

        if (page == "playlist") await OpenPlaylistPageAsync(main, library);
        else main.ActivePage = "PersonalHome";
        await DrainAsync(SettleMs);
        if (forceRefreshing)
        {
            home.IsRefreshing = true;
            await DrainAsync(600);
        }

        // 封面消融只对主页有意义(歌单页没有 360px 卡片图);抽成局部函数,免得三处各写一次判断。
        async Task<(int Total, int WithSource)> ApplyCoversHereAsync(bool enabled)
            => page == "home" ? await ApplyCoversAsync(window, enabled) : (0, 0);

        Log($"[home-scroll] 装配: 页面={page} 夹具=TestMainWindow(真实 MainWindow 结构) " +
            $"透明级别=实际{window.ActualTransparencyLevel} " +
            $"尺寸={window.ClientSize.Width:F0}x{window.ClientSize.Height:F0} 逻辑核={Environment.ProcessorCount} " +
            $"采样={sampleMs}ms×{rounds} 轮 滚动步长={step}px/帧 卡片目标={cards} " +
            $"刷新条={(forceRefreshing ? "强制可见(不确定动画在转)" : "关(IsRefreshing=false)")} " +
            $"dwm={(_dwm is null ? "未找到" : "可读")}");

        GpuUsageSampler? gpu = null;
        try
        {
            gpu = GpuUsageSampler.TryCreate(process.Id, out var diagnosis);
            if (gpu is not null && gpu.TrySample(out _)) { /* prime:速率型计数器首采恒 0,丢掉 */ }
            if (gpu is null) Log($"[home-scroll] ⚠ 量不到 GPU 计数器({diagnosis});CPU/线程/dwm 三路仍有效");
            else Log($"[home-scroll] GPU 计数器就绪: pid={process.Id}");
        }
        catch (Exception ex)
        {
            Log($"[home-scroll] ⚠ GPU 计数器异常: {ex.Message}");
            gpu = null;
        }

        // 预热:把四档都走一遍(含首解码、首次绘制、滚动时的首次光栅化),
        // 不然第一个测量窗口会把冷启动尾巴算进去。
        Log("[home-scroll] 预热(不计入读数)…");
        foreach (var scenario in scenarios)
        {
            var scroller = FindPageScroller(window);
            await ApplyCoversHereAsync(scenario.Covers);
            var driver = CreateDriver(window, scroller, scenario, step, 0);
            await DrainAsync(driver is null ? 1200 : 1500);
            driver?.Dispose();
        }
        if (FindPageScroller(window) is { } top) top.Offset = new Vector(top.Offset.X, 0);
        await DrainAsync(600);

        var rows = new List<Row>();
        var seq = 0;
        for (var round = 1; round <= rounds; round++)
        {
            // 奇数轮正序、偶数轮倒序:否则"第一个量到的档"总背着残留尾巴,会系统性高估它。
            var order = round % 2 == 1 ? scenarios : scenarios.Reverse().ToArray();
            foreach (var scenario in order)
            {
                seq++;
                rows.Add(await MeasureAsync(process, gpu, window, scenario, sampleMs, step, seq, page));
            }
        }

        // 帧率收尾量(放最后:自续订 RAF 会主动叫醒渲染管线,不许污染上面的 GPU 采样)。
        var fps = new List<(string Label, double Fps, int Ticks)>();
        foreach (var mode in new[] { ScrollMode.None, ScrollMode.Timer, ScrollMode.Raf })
        {
            var scroller = FindPageScroller(window);
            await ApplyCoversHereAsync(true);
            if (scroller is not null) scroller.Offset = new Vector(scroller.Offset.X, 0);
            await DrainAsync(400);
            var frames = await MeasureFpsAsync(window, scroller, mode, 16, step, fpsMs);
            fps.Add((mode switch
            {
                ScrollMode.None => "静止",
                ScrollMode.Timer => "滚动·计时器",
                _ => "滚动·满帧",
            }, frames.Fps, frames.Ticks));
        }

        window.WindowState = WindowState.Normal;
        window.Topmost = false;
        window.Close();
        await DrainAsync(400);
        gpu?.Dispose();

        Report(rows, fps, gpu is not null, step, sampleMs, page == "playlist" ? "歌单" : "主页");
        Log($"[home-scroll] 预览/临时目录: {tempRoot}");
        return 0;
    }

    /// <summary>一个测量窗口 = (切到目标状态 → 沉降 → 采样)。</summary>
    private static async Task<Row> MeasureAsync(
        Process process,
        GpuUsageSampler? gpu,
        Window window,
        (string Label, bool Covers, ScrollMode Mode, int TickMs) scenario,
        int sampleMs,
        double step,
        int seq,
        string page)
    {
        var scroller = FindPageScroller(window);
        if (scroller is not null) scroller.Offset = new Vector(scroller.Offset.X, 0);
        // 封面消融只对主页成立:歌单页没有 360px 卡片图(见 ApplyCoversAsync)。
        (int Total, int WithSource) covers = page == "home"
            ? await ApplyCoversAsync(window, scenario.Covers)
            : (0, 0);
        await DrainAsync(SettleMs);

        var driver = CreateDriver(window, scroller, scenario, step, 0);

        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var threadsStart = SnapshotThreads(process);
        var allocatedStart = GC.GetTotalAllocatedBytes(false);
        var gc0 = GC.CollectionCount(0);
        var dwmStart = DwmCpuSeconds();

        var wall = Stopwatch.StartNew();
        var gpuSum = 0.0;
        var gpuPeak = 0.0;
        var gpuSamples = 0;
        while (wall.ElapsedMilliseconds < sampleMs)
        {
            await DrainAsync(400);
            if (gpu is not null && gpu.TrySample(out var sample))
            {
                gpuSum += sample.Sum;
                gpuPeak = Math.Max(gpuPeak, sample.Max);
                gpuSamples++;
            }
        }

        var wallSeconds = wall.Elapsed.TotalSeconds;
        var ticks = driver?.Ticks ?? 0;
        driver?.Dispose();

        process.Refresh();
        var cpuPercent = (process.TotalProcessorTime - cpuStart).TotalSeconds / wallSeconds * 100;
        var gpuAverage = gpuSamples > 0 ? gpuSum / gpuSamples : double.NaN;
        var cacheItems = CoverImagePipeline.MemoryCacheStats.Items;
        // 本档实际出帧节奏,以及"每帧值多少 GPU 毫秒" —— 跨档比这个才不受帧数干扰。
        var fps = wallSeconds > 0 ? ticks / wallSeconds : 0;
        var gpuMsPerFrame = fps > 0.5 && !double.IsNaN(gpuAverage)
            ? gpuAverage / 100.0 / fps * 1000
            : double.NaN;

        var row = new Row(
            seq,
            scenario.Label,
            scenario.Mode != ScrollMode.None,
            scenario.Covers,
            cpuPercent,
            gpuAverage,
            gpuPeak,
            (GC.GetTotalAllocatedBytes(false) - allocatedStart) / 1024.0 / 1024.0,
            GC.CollectionCount(0) - gc0,
            DwmCpuSeconds() - dwmStart,
            window.GetVisualDescendants().Count(),
            page == "home"
                ? window.GetVisualDescendants().OfType<Border>()
                    .Count(b => b.Classes.Contains("playlist-card") && b.IsEffectivelyVisible)
                : window.GetVisualDescendants().Count(v => v.GetType().Name == "TrackRow"),
            covers.Total,
            covers.WithSource,
            scroller is null ? 0 : Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height),
            ticks,
            fps,
            gpuMsPerFrame,
            cacheItems,
            DescribeThreads(threadsStart, SnapshotThreads(process), wallSeconds));

        Log($"[home-scroll] R{seq} {row.Label,-18} CPU={row.Cpu,6:F2}%单核(任务管理器≈{row.Cpu / Environment.ProcessorCount:F2}%) " +
            $"GPU={row.Gpu,5:F2}%(峰{row.GpuPeak:F2}) 帧率={row.Fps,5:F1}fps 每帧GPU={FormatMs(row.GpuMsPerFrame / 1000)} " +
            $"dwmΔ={FormatMs(row.DwmSeconds)} 分配={row.AllocatedMb,5:F1}MB GC0={row.Gc0} 节点={row.Nodes,5} " +
            $"{(page == "home" ? "卡片" : "曲目行")}={row.Cards,3} 封面图={row.CoverImages,3}(有源{row.CoversWithSource,3}) 可滚={row.ScrollRange,6:F0}px " +
            $"滚帧={row.Ticks,4} 解码图={row.CacheItems,3}项 | {row.Threads}");
        return row;
    }

    /// <summary>按档位建滚动驱动;静止档返回 null。只有 Raf 档需要 TopLevel(拿帧回调)。</summary>
    private static ScrollDriver? CreateDriver(
        Window window,
        ScrollViewer? scroller,
        (string Label, bool Covers, ScrollMode Mode, int TickMs) scenario,
        double step,
        double startY)
    {
        if (scroller is null || scenario.Mode == ScrollMode.None) return null;
        var topLevel = scenario.Mode == ScrollMode.Raf ? TopLevel.GetTopLevel(window) : null;
        var driver = new ScrollDriver(scroller, step, startY, scenario.Mode, scenario.TickMs, topLevel);
        driver.Start();
        return driver;
    }

    /// <summary>自续订 RAF 计数器数帧。⚠ 只用于"帧率"这一格,不能与 GPU 采样同段(见类型注释)。</summary>
    private static async Task<(double Fps, int Ticks)> MeasureFpsAsync(
        Window window, ScrollViewer? scroller, ScrollMode mode, int tickMs, double step, int ms)
    {
        var frames = 0;
        var completion = new TaskCompletionSource();
        var driver = CreateDriver(window, scroller, ("fps", true, mode, tickMs), step, 0);

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ms);
        void Tick(TimeSpan _)
        {
            frames++;
            if (DateTime.UtcNow >= deadline) { completion.TrySetResult(); return; }
            // ⚠ 下一帧必须在回调里**直接**续订(与 ProgressRenderAnimator 同款);
            // 多绕一趟 Dispatcher.Post 会明显掉帧并把这个读数做坏。
            window.RequestAnimationFrame(Tick);
        }

        window.RequestAnimationFrame(Tick);
        await completion.Task;
        var ticks = driver?.Ticks ?? 0;
        driver?.Dispose();
        return (frames / (ms / 1000.0), ticks);
    }

    /// <summary>把内容区切到<b>歌单详情页</b>(用户报"只有 5~6%"的那页:虚拟化曲目行 + 小缩略图)。
    ///
    /// <para>
    /// 必须走生产入口 <see cref="MainViewModel.OpenShellPlaylistAuto"/>:跨音源打开写死网易云命令
    /// 会把 QQ tid 丢给网易云接口(2026-10-03 用户页点"听歌吧"空白实锤)。
    /// 找<b>曲目最多</b>的那个歌单:曲目太少时一屏就滚不动,量不到滚动档。
    /// </para>
    /// </summary>
    private static async Task OpenPlaylistPageAsync(MainViewModel main, PlaylistViewModel library)
    {
        var target = library.Playlists.Concat(library.NetEaseCollectedPlaylists)
            .Where(item => item.Playlist.TrackCount > 40)
            .OrderByDescending(item => item.Playlist.TrackCount)
            .FirstOrDefault();

        if (target is null)
        {
            Log("[home-scroll] ⚠ 资料库里没有 >40 首的歌单,量不了歌单页;本次退回主页(登录态丢了?)");
            main.ActivePage = "PersonalHome";
            return;
        }

        Log($"[home-scroll] 打开歌单页: {target.Playlist.Name} 标称 {target.Playlist.TrackCount} 首 " +
            $"(源={target.Playlist.Source})");
        main.OpenShellPlaylistAuto(target);

        for (var i = 0; i < 160 && library.Tracks.Count < 20; i++) await DrainAsync(250);
        Log($"[home-scroll] 歌单页落地: 实到曲目 {library.Tracks.Count} 首 " +
            $"SelectedPlaylist={library.SelectedPlaylist?.Name ?? "null"} " +
            $"页内还在加载更多={library.IsLoadingMore} " +
            "(⚠ 若为 True,滚动时那条不确定进度条也在出帧,读数会掺进 §六 那笔账)");
    }

    /// <summary>把所有卡片封面的 Image 置成"有 URL"或"清空"。每档都重新应用,使状态自足。
    /// 返回 (卡片图总数, <b>真的有 Source 的</b> 张数) —— 后者才是消融是否真的成立的指纹:
    /// 只数"DecodeSize==360 的 Image 有几个"在清空后数量不变,会把没生效的消融当成有效读数。</summary>
    private static async Task<(int Total, int WithSource)> ApplyCoversAsync(Window window, bool enabled)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var images = window.GetVisualDescendants().OfType<Image>()
                .Where(image => ManagedCoverImage.GetDecodeSize(image) == CardDecodeSize)
                .ToArray();
            if (images.Length > 0)
            {
                foreach (var image in images)
                {
                    var url = (image.DataContext as PlaylistItemViewModel)?.CoverUrl;
                    image.SetValue(ManagedCoverImage.SourceProperty, enabled ? url : null);
                }
                await DrainAsync(150);
                // 清空是同步生效的;置 URL 要等加载器回填,轮询几轮到"有源"稳定下来为止。
                var withSource = 0;
                for (var poll = 0; poll < (enabled ? 20 : 1); poll++)
                {
                    withSource = images.Count(image => image.Source is not null);
                    if (!enabled || withSource == images.Length) break;
                    await DrainAsync(100);
                }
                return (images.Length, withSource);
            }
            await DrainAsync(50);
        }
        return (0, 0);
    }

    /// <summary>找出当前页那个"能滚的"ScrollViewer(侧栏等小容器排不上号)。</summary>
    private static ScrollViewer? FindPageScroller(Window window)
        => window.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(scroller => scroller.IsEffectivelyVisible)
            .OrderByDescending(scroller => scroller.Extent.Height - scroller.Viewport.Height)
            .FirstOrDefault(scroller => scroller.Extent.Height - scroller.Viewport.Height > 60);

    /// <summary>按帧改 Offset 的程序化滚动。Timer 档走 DispatcherTimer(受 Windows ~15.6ms 粒度限制);
    /// Raf 档自续订 RequestAnimationFrame,节奏 = 合成器出帧节奏(真手滚时的节奏)。</summary>
    private sealed class ScrollDriver : IDisposable
    {
        private readonly ScrollViewer _scroller;
        private readonly DispatcherTimer? _timer;
        private readonly TopLevel? _topLevel;
        private readonly ScrollMode _mode;
        private readonly double _step;
        private double _y;
        private bool _disposed;

        public ScrollDriver(ScrollViewer scroller, double step, double startY, ScrollMode mode, int tickMs, TopLevel? topLevel)
        {
            _scroller = scroller;
            _step = step;
            _y = startY;
            _mode = mode;
            _topLevel = topLevel;
            if (mode == ScrollMode.Timer)
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(tickMs), DispatcherPriority.Render, (_, _) => Advance());
        }

        public int Ticks { get; private set; }

        public void Start()
        {
            if (_mode == ScrollMode.Raf) _topLevel?.RequestAnimationFrame(OnFrame);
            else _timer?.Start();
        }

        public void Stop() { _disposed = true; _timer?.Stop(); }

        public void Dispose() => Stop();

        private void OnFrame(TimeSpan _)
        {
            if (_disposed) return;
            Advance();
            // ⚠ 自续订必须在回调里直接续:绕一趟 Dispatcher.Post 会把出帧节奏压到 ~1/2。
            // 每档结束 Dispose 驱动即停(不再续订),所以不会污染后续档位的采样。
            _topLevel?.RequestAnimationFrame(OnFrame);
        }

        private void Advance()
        {
            var maximum = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
            if (maximum <= 1) return;
            _y += _step;
            if (_y > maximum) _y = 0;   // 看到底回头:保证整段采样期间一直在滚
            _scroller.Offset = new Vector(_scroller.Offset.X, _y);
            Ticks++;
        }
    }

    /// <summary>注入可控数量的歌单卡片(封面用真实 URL),让页面高度与内容量与真实账号同量级。</summary>
    private static void InjectCards(PlaylistViewModel library, int cards, IReadOnlyList<string> covers)
    {
        // "我喜欢的音乐"按 api 定位到的喜欢集合 id 识别,不设这个 id 就全落进"创建的歌单"。
        var likedId = 990_001L;
        try
        {
            var api = ServiceLocator.Get<NetEaseApiClient>();
            var field = typeof(NetEaseApiClient).GetField("_likedPlaylistId", BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(api, likedId);
        }
        catch (Exception ex)
        {
            Log($"[home-scroll] 设置喜欢集合 id 失败(不影响其余档): {ex.Message}");
        }

        library.Playlists.Clear();
        library.NetEaseCollectedPlaylists.Clear();
        library.QqPlaylists.Clear();

        for (var index = 0; index < cards; index++)
        {
            var cover = covers.Count > 0 ? covers[index % covers.Count] : "";
            // 每 16 首一个"喜欢"(进 72px 小卡区),其余为创建/收藏 —— 与实际主页的分区形态一致。
            var isLiked = index % 16 == 0;
            var isCollected = !isLiked && index % 3 == 0;
            var playlist = new Playlist
            {
                Id = isLiked ? likedId : 900_000 + index,
                Source = MusicSource.NetEase,
                Name = $"探针歌单 {index + 1:D3}",
                Description = "",
                TrackCount = 12 + index % 90,
                CoverUrl = cover,
                CanAddTracks = !isCollected,
            };
            var item = new PlaylistItemViewModel(playlist);
            if (isCollected) library.NetEaseCollectedPlaylists.Add(item);
            else library.Playlists.Add(item);
        }
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
            Log($"[home-scroll] 真实封面 URL 获取失败(网络?): {ex.Message}");
        }
        return result.Take(wanted).ToList();
    }

    private static void Report(
        List<Row> rows,
        List<(string Label, double Fps, int Ticks)> fps,
        bool gpuAvailable,
        double step,
        int sampleMs,
        string prefix)
    {
        // ⚠ 所有取值都按"页面前缀"拼标签:硬写"主页·xxx"会让歌单页那轮全部读成 n/a。
        var staticLabel = prefix + "·静止";
        var timerLabel = prefix + "·滚动·计时器";
        var rafLabel = prefix + "·滚动·满帧";
        var offLabel = prefix + "·滚动·无封面";
        var staticOffLabel = prefix + "·静止·无封面";
        var hasCoverAblation = rows.Any(row => row.Label == offLabel);

        Log("[home-scroll] ===== 读数 =====");
        Log("[home-scroll] 档位                 轮  CPU%单核  任务管理器   GPU%   峰GPU   帧率   每帧GPU   项数  封面图(有源)");
        foreach (var row in rows)
        {
            Log($"[home-scroll] {row.Label,-18} {row.Seq,2}  {row.Cpu,7:F2}  {row.Cpu / Environment.ProcessorCount,9:F2}  " +
                $"{row.Gpu,6:F2}  {row.GpuPeak,6:F2}  {row.Fps,6:F1}  {FormatMs(row.GpuMsPerFrame / 1000)}  " +
                $"{row.Cards,4}  {row.CoverImages,4}({row.CoversWithSource,2})");
        }

        var staticOn = Median(rows, staticLabel);
        var scrollTimer = Median(rows, timerLabel);
        var scrollRaf = Median(rows, rafLabel);
        var scrollOff = Median(rows, offLabel);
        var staticOff = Median(rows, staticOffLabel);

        Log("[home-scroll] ===== 结论 =====");
        if (!gpuAvailable)
        {
            Log("[home-scroll] ⚠ GPU 读数不可用,以下按 CPU 口径给;GPU 归因缺失");
        }
        Log($"[home-scroll] ① 让帧流起来(滚动−静止,同为计时器档): {scrollTimer:F2}% − {staticOn:F2}% " +
            $"= {scrollTimer - staticOn:+0.00;-0.00;0.00}% GPU");
        if (hasCoverAblation)
        {
            Log($"[home-scroll] ② 封面位图/裁剪(滚动·计时器 vs 滚动·无封面): 无封面档 {scrollOff:F2}% " +
                $"⇒ 封面贡献 {scrollTimer - scrollOff:+0.00;-0.00;0.00}% GPU " +
                $"(指纹:有源封面 {Fingerprint(rows, timerLabel)} vs {Fingerprint(rows, offLabel)})");
            Log($"[home-scroll] ③ 静止档噪声底: 有封面 {staticOn:F2}% / 无封面 {staticOff:F2}% " +
                $"(两者都应≈0;若不是,说明静止态本身还有别的东西在出帧)");
        }
        else
        {
            Log($"[home-scroll] ③ 静止档噪声底: {staticOn:F2}%(该页无卡片封面消融档,②跳过)");
        }
        Log($"[home-scroll] ④ 帧率消融(同一页、同一滚动步长,只改每秒改几次 Offset): " +
            $"计时器档 {scrollTimer:F2}% / {MedianFps(rows, timerLabel):F1}fps " +
            $"⇒ 满帧档 {scrollRaf:F2}% / {MedianFps(rows, rafLabel):F1}fps ;" +
            $"帧数比 {FpsRatio(rows, timerLabel, rafLabel):F2}× → GPU 比 {GpuRatio(scrollTimer, scrollRaf):F2}×");
        Log($"[home-scroll] ⑤ 每帧固定开销(把 GPU 折算到单帧): 计时器档 {FormatMs(MedianPerFrame(rows, timerLabel) / 1000)} " +
            $"vs 满帧档 {FormatMs(MedianPerFrame(rows, rafLabel) / 1000)}" +
            (hasCoverAblation ? $" vs 滚动·无封面 {FormatMs(MedianPerFrame(rows, offLabel) / 1000)}" : "") +
            " —— 跨页比这一格才公平(它已经除掉了帧数)");
        foreach (var entry in fps)
        {
            var perFrame = entry.Fps > 0.5 ? 1000.0 / entry.Fps : double.NaN;
            Log($"[home-scroll] ⑥ 帧率[{entry.Label}]: {entry.Fps:F1} fps(每帧 {DescribeDouble(perFrame):F1}ms,滚动次数 {entry.Ticks})");
        }
        Log($"[home-scroll] 口径提示: 采样 {sampleMs}ms/档,滚动步长 {step}px/帧;" +
            "静止档若 GPU>0 说明窗口仍在自己出帧,不是“只有滚动才贵”");
        Log("[home-scroll] 判定: 若 ④ 的 GPU 比 ≈ 帧数比、⑤ 两个档的每帧开销接近 ⇒ 代价就是“出帧次数 × 每帧固定开销”," +
            "用户看到的 20% = 显示屏刷新率(165Hz)下的每帧开销;要压只能少出帧/少脏区,换画法没用。" +
            "若 ② 的封面贡献很大(且指纹确实从 42 掉到 0),才轮到“封面位图”这条线。");
        Log("[home-scroll] 跨页判定(主页 vs 歌单页,同窗口同驱动): 比 ⑤ 的每帧开销。" +
            "若歌单页 ≈ 主页 ⇒ “每帧开销与内容无关”成立,用户看到的差值来自帧数(滚动节奏/掉帧),不是这页画得贵;" +
            "若歌单页明显更低 ⇒ 视口内确实有东西在按内容计费,回去查两页视口里不共有的构造。");
    }

    /// <summary>该档"真的有 Source"的封面张数(消融是否生效的指纹)。</summary>
    private static string Fingerprint(List<Row> rows, string label)
    {
        var row = rows.FirstOrDefault(r => r.Label == label);
        return row.Label is null ? "n/a" : $"{row.CoversWithSource}/{row.CoverImages}";
    }

    private static double MedianFps(List<Row> rows, string label) => MedianOf(
        rows.Where(row => row.Label == label).Select(row => row.Fps));

    private static double MedianPerFrame(List<Row> rows, string label) => MedianOf(
        rows.Where(row => row.Label == label).Select(row => row.GpuMsPerFrame));

    private static double FpsRatio(List<Row> rows, string timerLabel, string rafLabel)
    {
        var timer = MedianFps(rows, timerLabel);
        var raf = MedianFps(rows, rafLabel);
        return timer > 0.5 ? raf / timer : double.NaN;
    }

    private static double GpuRatio(double timer, double raf)
        => timer > 0.01 ? raf / timer : double.NaN;

    private static double Median(List<Row> rows, string label) => MedianOf(
        rows.Where(row => row.Label == label).Select(row => row.Gpu));

    private static double MedianOf(IEnumerable<double> values)
    {
        var list = values.Where(value => !double.IsNaN(value)).OrderBy(value => value).ToList();
        return list.Count == 0 ? double.NaN : list[list.Count / 2];
    }

    private static string DescribeDouble(double value) => double.IsNaN(value) ? "n/a" : $"{value:F1}";

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

    /// <summary>按线程拆 CPU —— 归因"卡在 UI 线程(光栅)还是渲染/合成线程"。</summary>
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
        return string.Join(" ", deltas.Take(3).Select(entry =>
            $"{(entry.Id == (int)_uiThreadId ? "UI线程" : $"线程{entry.Id}")}={entry.Percent:F1}%"));
    }

    private static string FormatMs(double seconds) =>
        double.IsNaN(seconds) ? " 不可读" : $"{seconds * 1000,5:F1}ms";

    /// <summary>读 dwm.exe 累计 CPU。读不到返回 NaN(跑在 DWM-1 会话时会被拒),不要返回 0。</summary>
    private static double DwmCpuSeconds()
    {
        if (_dwm is null) return double.NaN;
        try
        {
            _dwm.Refresh();
            return _dwm.TotalProcessorTime.TotalSeconds;
        }
        catch { return double.NaN; }
    }

    private static async Task DrainAsync(int milliseconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    /// <summary>允许 0 的读数(0 有语义:见 ALY_HOME_CARDS)。</summary>
    private static int EnvIntAllowingZero(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    /// <summary>解析 <c>WxH</c> 尺寸;缺失或写坏就退回默认值(不要拿 0 去建窗口)。</summary>
    private static (int Width, int Height) ParseSize(string text, int fallbackWidth, int fallbackHeight)
    {
        var parts = text.Split('x', 'X');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var width) && width > 200
            && int.TryParse(parts[1], out var height) && height > 200)
            return (width, height);
        return (fallbackWidth, fallbackHeight);
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { }
    }

    private readonly record struct Row(
        int Seq,
        string Label,
        bool Scroll,
        bool Covers,
        double Cpu,
        double Gpu,
        double GpuPeak,
        double AllocatedMb,
        int Gc0,
        double DwmSeconds,
        int Nodes,
        int Cards,
        int CoverImages,
        int CoversWithSource,
        double ScrollRange,
        int Ticks,
        double Fps,
        double GpuMsPerFrame,
        int CacheItems,
        string Threads);
}
