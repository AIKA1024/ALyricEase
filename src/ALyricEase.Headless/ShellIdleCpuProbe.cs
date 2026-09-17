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
/// "静止底座"探针(--idle-cpu-real):用户在任务管理器里看到"歌单页什么都不做也有 CPU 占用",
/// 而 --pl-cpu-real 在同一页面、同一静止态量到 0.00%(像素零差、零分配、零 GC)。
///
/// 两者最大的结构差异只有一个:**夹具**。
/// --pl-cpu-real 用裸 <c>Window</c> + 单独挂载的 PlaylistView;
/// 本探针用与真实 MainWindow.axaml 逐字节一致的 TestMainWindow
/// (透明窗 + TransparencyLevelHint=AcrylicBlur + ExperimentalAcrylicBorder
/// + 真 AppShell(导航/播放条) + 常驻 NowPlayingView 覆盖层),
/// 并且通过**真 MainViewModel 切页**,走的是真实的 TransitioningContentControl 模板解析路径。
/// 于是"外壳(透明窗/亚克力/常驻覆盖层)本身有没有常驻成本"这个自变量第一次被量到。
///
/// 三个自变量,每个只切一个维度:
///   ① 页面:首页(Recommend) vs 歌单页(Favorites);
///   ② 窗口:可见 vs 最小化(负对照,应≈0);
///   ③ 窗口透明:AcrylicBlur(默认) vs 不透明(ALY_IDLE_NO_ACRYLIC=1,另跑一次对照)。
///
/// 观测量刻意全部选**不扰动**窗口的那些:进程 CPU / 按线程拆的 CPU / 进程 GPU 引擎占比 /
/// dwm.exe 的 CPU 增量 / 托管分配量与 GC 次数 / 视觉节点与实化行数。
/// 刻意**不用**"自续订 RequestAnimationFrame 计数器"数帧:请求帧本身就会把渲染管线叫醒,
/// 数出来的"在出帧"是探针自己造成的(那个技巧只适合"不可见时平台还送不送帧"这类对照)。
///
/// GPU 读数在这里兼职"出帧活性"判定:窗口若仍在连续呈现,DWM 每一帧都得重做亚克力模糊,
/// 于是**进程 GPU 引擎占比与 dwm.exe 的 CPU 会同时抬起来** —— 这正好能把
/// "真的在空转重绘"与"只是 GC/线程池尾巴"分开。
///
/// 口径:CPU 一律**单核百分比**(不除核数)。任务管理器"进程"页显示的是**全机**百分比,
/// 等于本探针读数再除以逻辑核数:报告里两列都打,避免拿两种口径互相"验证"出一个假结论。
/// </summary>
internal static class ShellIdleCpuProbe
{
    /// <summary>切态后等待布局/过渡动画/封面请求落地,再开始采样。</summary>
    private const int SettleMs = 1500;

    private static uint _uiThreadId;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>dwm.exe 句柄(桌面窗口管理器)。它是"有没有人还在出帧"的旁证:
    /// 应用侧出帧 ⇒ DWM 每帧重新合成/重算模糊 ⇒ 它的 CPU 跟着抬。
    /// 注意口径:它服务整个桌面,别的窗口动一下也会抬,所以只看**同一轮内的相对变化**。</summary>
    private static Process? _dwm;

    /// <summary>补页提示条(不确定进度条)实例。第一次显示时才被实化,拿到就一直复用:
    /// 三态配对要靠它直接翻 <c>IsIndeterminate</c> 来区分"动画在转"与"只是控件还在树上"。</summary>
    private static ProgressBar? _loadingBar;

    /// <summary>提示条的当前状态串:有效可见性 + 动画开关 + 视觉树位置。
    /// ⚠ 必须看 <c>IsEffectivelyVisible</c> —— 最小化时 <c>Window.IsVisible</c> 仍是 true,
    /// 只看它必然把"其实没显示"读成"显示了"。</summary>
    private static string DescribeLoadingBar()
    {
        if (_loadingBar is null) return "提示条=尚未实化";
        return $"提示条=可见性[{(_loadingBar.IsEffectivelyVisible ? "有效可见" : "被隐藏")}] " +
               $"自身IsVisible={_loadingBar.IsVisible} 动画={(_loadingBar.IsIndeterminate ? "开" : "关")} " +
               $"高={_loadingBar.Bounds.Height:F0}px 在树={_loadingBar.IsAttachedToVisualTree()}";
    }

    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[idle-cpu] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };

        _uiThreadId = GetCurrentThreadId();
        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[idle-cpu] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        var process = Process.GetCurrentProcess();
        var sampleMs = EnvInt("ALY_IDLE_SAMPLE_MS", 6000);
        var rounds = EnvInt("ALY_IDLE_ROUNDS", 3);
        var rowCount = EnvInt("ALY_IDLE_ROWS", 300);
        var noAcrylic = Env("ALY_IDLE_NO_ACRYLIC") == "1";
        var realCovers = Env("ALY_IDLE_REAL_COVERS") == "1";
        var tempCoverCache = Env("ALY_IDLE_TEMP_COVER_CACHE") == "1";

        // ALY_IDLE_LOADING_BAR=1:强制把"正在加载更多"提示条摆出来(等价于补页被限速卡在重试)。
        // 这是歌单页**唯一**可持续的空转机制 —— 框架的不确定进度动画只要控件可见就一直转。
        // 单变量:同一个 VM 状态,分别停在歌单页(条可见)与首页(歌单页已离树)量。
        var loadingBar = Env("ALY_IDLE_LOADING_BAR") == "1";

        // 带真实封面跑时,把封面写盘重定向到临时目录:探针不该往用户的封面缓存里塞文件,
        // 更不该触发那边目录的 LRU 淘汰(哪怕淘汰逻辑已经增量化,也不去碰用户数据)。
        if (tempCoverCache)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "aly-idle-covers-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempRoot);
            CoverImagePipeline.Configure(new MusicCacheService(512, tempRoot, new HttpClient()));
            Log($"[idle-cpu] 封面写盘已重定向到临时目录: {tempRoot}");
        }

        _dwm = Process.GetProcessesByName("dwm").FirstOrDefault();

        var main = ServiceLocator.Get<MainViewModel>();
        var playlist = ServiceLocator.Get<PlaylistViewModel>();
        var cache = ServiceLocator.Get<MusicCacheService>();

        // 歌单页数据:与 --pl-cpu-real 走同一条"快照恢复"路径(不触网),口径可比。
        // 封面默认留空 —— 本探针问的是"静止时外壳/页面有没有常驻成本",先不带下载与解码这个自变量。
        var covers = realCovers ? await FetchRealCoverUrlsAsync(40) : [];
        var songs = CreateSongs(rowCount, covers);

        async Task InjectPlaylistAsync(string tag, int barMode)
        {
            var cacheKey = Guid.NewGuid().ToString("N");
            var payload = new PlaylistPageCacheData(
                songs.Select(song => new NavigationPageCacheTrack(song, true, true)).ToList(),
                songs.Select(song => song.Id).ToArray(),
                songs.Count,
                null);
            _ = cache.CachePlaylistPageSnapshotAsync(cacheKey, payload);
            var snapshot = new PlaylistNavigationSnapshot(
                cacheKey,
                PlaylistPageKind.NetEase,
                new Playlist
                {
                    Id = 78,
                    Source = MusicSource.NetEase,
                    Name = "静止底座探针",
                    TrackCount = songs.Count,
                },
                null,
                "静止底座探针",
                "测试用户",
                0,
                "",
                false,
                0);
            await playlist.RestoreNavigationSnapshotAsync(snapshot);
            // LoadMoreAsync 的 finally 会把 IsLoadingMore 置回 false,但这里没有真正的加载在跑,
            // 所以强制值能一直存活到采样结束(与 --pl-cpu-real"绕过门控"那条同样的做法)。
            // 提示条的三态由 ApplyBarModeAsync 统一负责,这里只负责把数据摆好。
            Log($"[idle-cpu] 注入歌单页数据[{tag}]: 行={playlist.RetainedTrackRowCount} " +
                $"队列={playlist.RetainedQueueSongCount} 真实封面={covers.Count} 个" +
                (barMode == 0 ? "" : $" [提示条档={barMode}]"));
        }

        await InjectPlaylistAsync("初始", loadingBar ? 1 : 0);
        Log($"[idle-cpu] 登录态(网易云)={playlist.IsLoggedIn}");

        var window = new TestMainWindow { DataContext = main, ShowActivated = false };
        if (noAcrylic)
        {
            // 唯一自变量:窗口不再要求亚克力/透明。窗口级透明是"每个 present 都要 DWM 重做背景"
            // 的前提,所以这一版必须单独跑一次对照,不能与默认版混在一轮里比。
            window.TransparencyLevelHint = [WindowTransparencyLevel.None];
        }

        window.Show();
        window.Topmost = true;   // 保证不被遮挡:被遮挡时合成器不出帧,读数必然归零(那是假的"很省")
        window.Activate();
        await DrainAsync(900);

        Log($"[idle-cpu] 装配: 夹具=TestMainWindow(真实 MainWindow 结构) " +
            $"透明级别=请求[{string.Join(",", window.TransparencyLevelHint.Select(level => level.ToString()))}] " +
            $"实际={window.ActualTransparencyLevel} " +
            $"尺寸={window.ClientSize.Width:F0}x{window.ClientSize.Height:F0} " +
            $"逻辑核={Environment.ProcessorCount} 采样={sampleMs}ms×{rounds} 轮 " +
            $"dwm={( _dwm is null ? "未找到" : double.IsNaN(DwmCpuSeconds()) ? "在但读不到(跨会话权限)" : "可读")}");
        Log($"[idle-cpu] 播放态: IsPlaying={main.Player.IsPlaying}(本探针全程不启动播放)");

        // 预热:两个页面各走一遍,让样式解析、字体/封面首次光栅化、Acrylic 首次合成、
        // 以及切页时的 TransitioningContentControl 首次模板解析全部落地。
        // 不预热的话第一个测量窗口会把冷启动尾巴算进去(参考 --shell-cpu-real 的同类结论)。
        foreach (var page in new[] { "Recommend", "Favorites", "Recommend" })
        {
            main.ActivePage = page;
            await DrainAsync(1200);
        }

        // ⚠ 预热里那次切到歌单页会触发 MainViewModel.OnActivePageChanged 的
        // `if (value == "Favorites" && !_restoringPlaylistNavigation) _ = _playlist.EnsureLoadedAsync();`
        // —— 已登录时它会去拉"我喜欢的音乐"并把注入的数据**整片换掉**(实测预热后 行=0)。
        // 那条加载只在首次导航时真的干活,所以预热之后再注入一次,测量期间的数据才可信;
        // 每个歌单页场景前也都再注入一次,免得单例 VM 的状态被前一场带走。
        await InjectPlaylistAsync("预热后", loadingBar ? 1 : 0);
        main.ActivePage = "Favorites";
        await DrainAsync(1500);
        Log($"[idle-cpu] 预热后: 歌单页行={playlist.RetainedTrackRowCount} 首页区块已加载={main.Recommend.Sections.Count}" +
            (playlist.RetainedTrackRowCount == 0 ? " ⚠ 歌单页仍为空,本轮的歌单页读数不可信" : ""));

        GpuUsageSampler? gpu = null;
        var gpuDiagnosis = "";
        try
        {
            gpu = GpuUsageSampler.TryCreate(process.Id, out gpuDiagnosis);
            if (gpu is not null && gpu.TrySample(out _)) { /* prime:速率型计数器首次采样恒为 0,先丢掉 */ }
        }
        catch (Exception ex)
        {
            gpuDiagnosis = ex.Message;
            gpu = null;
        }
        Log(gpu is null
            ? $"[idle-cpu] ⚠ 量不到 GPU 计数器({gpuDiagnosis}) —— 出帧活性这一路结论缺失,CPU/线程/DWM 三路仍然有效"
            : $"[idle-cpu] GPU 计数器就绪: pid={process.Id}");

        var scenarios = new (string Label, string Page, bool Minimized, bool Inject, int BarMode)[]
        {
            ("首页·可见", "Recommend", false, false, 0),
            ("歌单页·可见", "Favorites", false, true, loadingBar ? 1 : 0),
            ("歌单页·最小化", "Favorites", true, true, loadingBar ? 1 : 0),
            ("首页·最小化", "Recommend", true, false, 0),
        };

        // ALY_IDLE_DWELL=playlist|recommend|bar-ab:长驻留模式。
        // 四个场景轮换时,每次切页都会带一条"过渡/布局/首帧"的尾巴,而这个尾巴的衰减时间与采样
        // 窗口同量级 ⇒ 轮换读数永远在"尾巴 + 底座"的混合区间里跳(实测同一场景同一构建
        // 0.26% ↔ 6.70%)。长驻留模式把页切一次就不再动,连续采 N 个窗口,直接看
        // **同一个静止态**的 CPU 是收敛到 0(尾巴)还是稳定在一个非零值(真常驻成本)。
        //
        // bar-ab 是三态配对(同一进程、同一份数据、同一次驻留里循环):
        //   ① 隐藏提示条(动画开着)——"用户什么都没做"的常态;
        //   ② 可见 + 动画开 —— 提示条真在转;
        //   ③ 可见 + 动画关 —— 同一个控件、同一个位置,只把动画停掉(拆出"动画"与"控件/布局"两件事)。
        // 跨进程/跨时段对比会被"这台机器本底抖动 ±1% 单核"淹没,配对循环才能把差值提出来;
        // ③ 是关键对照:它把"动画本身"从"多一个控件在树上"里分离出来。
        var dwell = Env("ALY_IDLE_DWELL");
        var outerRounds = rounds;
        if (dwell.Length > 0)
        {
            if (dwell == "bar-ab")
            {
                var cycle = new (int Mode, string Label)[]
                {
                    (4, "歌单页·可见(隐藏+强开动画=旧行为)"),
                    (1, "歌单页·可见(可见+动画)"),
                    (2, "歌单页·可见(可见+无动画)"),
                    (0, "歌单页·可见(隐藏=真实应用做法)"),
                };
                scenarios = Enumerable.Range(0, Math.Max(3, rounds))
                    .Select(index => cycle[index % cycle.Length])
                    .Select(entry => (entry.Label, "Favorites", false, true, entry.Mode))
                    .ToArray();
                Log($"[idle-cpu] 三态配对模式: 歌单页·可见 × {scenarios.Length} 个窗口,循环[隐藏 / 可见+动画 / 可见+无动画]");
            }
            else if (dwell == "bar-onoff")
            {
                // 最硬的一对:可见+动画 → 隐藏(**只翻业务标志,不碰动画**)。
                // 只有这一对能证明"门控自己把动画杀掉了" —— 若前一档是"可见+动画关",
                // 隐藏档读到的"动画=关"可能只是上一档留下来的,不能当证据。
                var cycle = new (int Mode, string Label)[]
                {
                    (1, "歌单页·可见(可见+动画)"),
                    (0, "歌单页·可见(隐藏=只翻业务标志)"),
                };
                scenarios = Enumerable.Range(0, Math.Max(2, rounds))
                    .Select(index => cycle[index % cycle.Length])
                    .Select(entry => (entry.Label, "Favorites", false, true, entry.Mode))
                    .ToArray();
                Log($"[idle-cpu] 单变量对拍模式: 歌单页·可见 × {scenarios.Length} 个窗口,循环[可见+动画 → 只翻业务标志隐藏]");
            }
            else
            {
                var dwellScenario = dwell == "recommend"
                    ? ("首页·可见", "Recommend", false, false, 0)
                    : ("歌单页·可见", "Favorites", false, true, loadingBar ? 1 : 0);
                scenarios = Enumerable.Repeat(dwellScenario, Math.Max(2, rounds)).ToArray();
                Log($"[idle-cpu] 长驻留模式: 固定[{dwellScenario.Item1}] × {scenarios.Length} 个连续采样窗口");
            }
            outerRounds = 1;   // 每个场景就是"一个连续窗口",不再按轮重复
        }

        /// <summary>三态切换:0=隐藏(不碰动画,与真实应用一致) / 1=可见+动画 / 2=可见+动画关 /
        /// 4=隐藏+把动画强开(复现 2026-09-17 之前的行为:探针直接写 IsIndeterminate,门控管不着)。
        /// 提示条只在"可见"时才会实化,所以第一次要先把它摆出来、等它上树,才拿得到实例。</summary>
        async Task ApplyBarModeAsync(int mode)
        {
            if (mode is 0 or 4)
            {
                playlist.IsLoadingMore = false;
                // ⚠ 0 档刻意**不写** IsIndeterminate:真实应用只翻 IsLoadingMore,
                // 动画该不该停由门控负责 —— 这一档量的就是门控有没有把"隐藏态空转"压下去。
                if (mode == 4 && _loadingBar is not null) _loadingBar.IsIndeterminate = true;
                return;
            }

            playlist.IsLoadingMore = true;
            for (var attempt = 0; attempt < 60 && _loadingBar is null; attempt++)
            {
                // 只认"不确定进度条"(歌单页补页提示条是本探针唯一的进度条来源),
                // 拿到实例后就不再靠 IsIndeterminate 认它了 —— 第③态会把它置 false。
                _loadingBar = window.GetVisualDescendants().OfType<ProgressBar>()
                    .FirstOrDefault(bar => bar.IsIndeterminate);
                if (_loadingBar is null) await DrainAsync(50);
            }
            if (_loadingBar is not null) _loadingBar.IsIndeterminate = mode == 1;
        }

        var rows = new List<Row>();
        var seq = 0;   // 全局窗口序号:长驻留模式下"第 1 个窗口 vs 第 6 个窗口"的衰减曲线靠它读
        var injected = false;
        for (var round = 1; round <= outerRounds; round++)
        {
            // 顺序轮换:奇数轮正序、偶数轮倒序。否则"第一个量到的场景"总背着预热/加载的尾巴,
            // 会系统性高估它(冒烟那轮首页量到 8.70% 而之后各态都 <2%,就吃过这个亏)。
            var order = round % 2 == 1 ? scenarios : scenarios.Reverse().ToArray();
            foreach (var scenario in order)
            {
                seq++;
                // 长驻留模式只在第一个窗口前注入一次:要量的是"同一份数据静止下来之后"的曲线,
                // 每个窗口都重注入等于每窗口都换一次数据集,曲线就不干净了。
                if (scenario.Inject && (dwell.Length == 0 || !injected))
                {
                    await InjectPlaylistAsync($"R{round}", scenario.BarMode);
                    injected = true;
                }
                if (scenario.Inject) await ApplyBarModeAsync(scenario.BarMode);
                rows.Add(await MeasureAsync(process, gpu, window, main, playlist,
                    scenario.Label, scenario.Page, scenario.Minimized, sampleMs, seq));
            }
        }

        window.WindowState = WindowState.Normal;
        window.Close();
        await DrainAsync(400);
        gpu?.Dispose();

        Report(rows, noAcrylic, gpu is not null);
        return 0;
    }

    /// <summary>一个测量窗口 = 切到目标页面/窗口态 → 沉降 → 采样。</summary>
    private static async Task<Row> MeasureAsync(
        Process process,
        GpuUsageSampler? gpu,
        Window window,
        MainViewModel main,
        PlaylistViewModel playlist,
        string label,
        string page,
        bool minimized,
        int sampleMs,
        int seq)
    {
        main.ActivePage = page;
        window.WindowState = minimized ? WindowState.Minimized : WindowState.Normal;
        await DrainAsync(SettleMs);

        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var threadsStart = SnapshotThreads(process);
        var allocatedStart = GC.GetTotalAllocatedBytes(false);
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
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
        process.Refresh();
        var cpuPercent = (process.TotalProcessorTime - cpuStart).TotalSeconds / wallSeconds * 100;
        var dwmDelta = DwmCpuSeconds() - dwmStart;

        var row = new Row(
            seq,
            label,
            cpuPercent,
            gpuSamples > 0 ? gpuSum / gpuSamples : double.NaN,
            gpuPeak,
            (GC.GetTotalAllocatedBytes(false) - allocatedStart) / 1024.0 / 1024.0,
            GC.CollectionCount(0) - gc0,
            GC.CollectionCount(1) - gc1,
            GC.CollectionCount(2) - gc2,
            dwmDelta,
            window.GetVisualDescendants().Count(),
            window.GetVisualDescendants().OfType<TrackRow>().Count(),
            playlist.RetainedTrackRowCount,
            DescribeThreads(threadsStart, SnapshotThreads(process), wallSeconds));

        // 解码图缓存:封面这条路的直接指纹(条目/字节在涨 ⇒ 封面管线还在干活)。
        var coverStats = CoverImagePipeline.MemoryCacheStats;
        Log($"[idle-cpu] R{seq} {label,-12} CPU={row.Cpu,6:F2}%单核(任务管理器口径≈{row.Cpu / Environment.ProcessorCount:F2}%) " +
            $"GPU={Describe(row.Gpu):F2}%(峰{row.GpuPeak:F2}) dwmΔ={FormatMs(row.DwmSeconds)} " +
            $"分配={row.AllocatedMb:F1}MB GC={row.Gc0}/{row.Gc1}/{row.Gc2} " +
            $"节点={row.Nodes,5} 行控件={row.TrackRows,3}(VM 保留={row.RetainedRows,3}) " +
            $"解码图={coverStats.Items,3}项/{coverStats.Bytes / 1024.0 / 1024.0:F1}MB | {row.Threads} | {DescribeLoadingBar()}" +
            (row.Label.StartsWith("歌单页") && row.RetainedRows == 0 ? "  ⚠ 歌单页为空,此行不可信" : ""));
        return row;
    }

    /// <summary>判定表。核心是"同一台机器、同一轮内"的比较:
    /// 页面之间比(首页 vs 歌单页)回答"是不是歌单页特有",窗口态之间比(可见 vs 最小化)
    /// 回答"是不是可见窗自身的常驻成本",GPU 是否为零回答"到底还在不在出帧"。</summary>
    private static void Report(List<Row> rows, bool noAcrylic, bool gpuAvailable)
    {
        Log("[idle-cpu] ===== 判定:各场景静止底座 =====");
        Log("[idle-cpu] 场景          轮  CPU%单核  任务管理器口径   GPU%   dwmΔ/6s   分配MB  GC0/1/2   节点/行控件");

        foreach (var row in rows)
        {
            Log($"[idle-cpu] {row.Label,-12} {row.Round,2}  {row.Cpu,8:F2}  {row.Cpu / Environment.ProcessorCount,13:F2}  " +
                $"{Describe(row.Gpu),6:F2}  {FormatMs(row.DwmSeconds),10}  {row.AllocatedMb,7:F1}  " +
                $"{row.Gc0}/{row.Gc1}/{row.Gc2}  {row.Nodes}/{row.TrackRows}");
        }

        var recommendVisible = Median(rows, "首页·可见");
        var playlistVisible = Median(rows, "歌单页·可见");
        var playlistMinimized = Median(rows, "歌单页·最小化");
        var recommendMinimized = Median(rows, "首页·最小化");
        var visibleGpu = MedianOf(rows.Where(row => !row.Label.Contains("最小化")).Select(row => row.Gpu));
        var minimizedGpu = MedianOf(rows.Where(row => row.Label.Contains("最小化")).Select(row => row.Gpu));
        var cores = Environment.ProcessorCount;

        Log("[idle-cpu] ===== 结论 =====");
        Log($"[idle-cpu] ① 页面之差(可见窗口下): 歌单页 {playlistVisible:F2}% - 首页 {recommendVisible:F2}% " +
            $"= {playlistVisible - recommendVisible:+0.00;-0.00;0.00}% 单核");
        Log($"[idle-cpu] ② 可见 vs 最小化(同页): 歌单页 {playlistVisible:F2}% - {playlistMinimized:F2}% " +
            $"= {playlistVisible - playlistMinimized:+0.00;-0.00;0.00}% 单核; " +
            $"首页 {recommendVisible:F2}% - {recommendMinimized:F2}% = {recommendVisible - recommendMinimized:+0.00;-0.00;0.00}% 单核");
        Log($"[idle-cpu] ③ 可见态绝对值: 首页 {recommendVisible:F2}% 歌单页 {playlistVisible:F2}% 单核 " +
            $"⇒ 任务管理器(全机口径)约 {recommendVisible / cores:F2}% / {playlistVisible / cores:F2}%");
        Log(gpuAvailable
            ? $"[idle-cpu] ④ 出帧活性: 可见态 GPU 中位 {visibleGpu:F2}%(峰 {MedianOf(rows.Where(row => !row.Label.Contains("最小化")).Select(row => row.GpuPeak)):F2}%) " +
              $"vs 最小化 {minimizedGpu:F2}% —— 可见态若明显 >0 说明窗口在持续呈现(不是空转 GC 尾巴);" +
              $"若两者都≈0 说明真的没有帧在流动"
            : "[idle-cpu] ④ 出帧活性: GPU 计数器不可用,本路结论缺失");
        Log($"[idle-cpu] ⑤ 窗口透明: {(noAcrylic ? "本轮=不透明(对照)" : "本轮=AcrylicBlur(与真实应用一致)")};" +
            $"判断透明层本身值多少,要用同一台机器另跑一次 ALY_IDLE_NO_ACRYLIC=1 对比");
        Log($"[idle-cpu] PASS: 静止底座已量清(页面 × 窗口态 × 出帧活性 × 线程归因)");
    }

    /// <summary>某个场景跨轮的 CPU 中位数(抗偶发尖峰)。</summary>
    private static double Median(List<Row> rows, string label)
        => MedianOf(rows.Where(row => row.Label == label).Select(row => row.Cpu));

    private static double MedianOf(IEnumerable<double> values)
    {
        var list = values.Where(value => !double.IsNaN(value)).OrderBy(value => value).ToList();
        if (list.Count == 0) return double.NaN;
        return list[list.Count / 2];
    }

    private static double Describe(double value) => double.IsNaN(value) ? double.NaN : value;

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

    /// <summary>按线程拆 CPU —— 归因的关键一步:UI 线程 vs 渲染/合成线程 vs 线程池。</summary>
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

    /// <summary>读 dwm.exe 的累计 CPU 秒数。⚠ 读不到时返回 NaN,不要返回 0 ——
    /// dwm 跑在 DWM-1 会话,跨会话读 TotalProcessorTime 会被拒;返回 0 会让"读失败"
    /// 伪装成"DWM 完全没干活",而后者恰好是我们要证明的结论,这种假阳性最危险。</summary>
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

    /// <summary>毫秒列:读不到就明确写"不可读",别留一个看起来像 0 的数。</summary>
    private static string FormatMs(double seconds) =>
        double.IsNaN(seconds) ? " 不可读" : $"{seconds * 1000,6:F1}ms";

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
            Log($"[idle-cpu] 真实封面 URL 获取失败(网络?): {ex.Message}");
        }
        return result.Take(wanted).ToList();
    }

    private static List<Song> CreateSongs(int count, IReadOnlyList<string> covers) =>
        Enumerable.Range(1, count).Select(index => new Song
        {
            Id = 850_000 + index,
            Source = MusicSource.NetEase,
            Name = $"静止底座歌曲 {index:D4}",
            Artist = "探针歌手",
            Album = "探针专辑",
            DurationMs = 180_000,
            ArtistIds = [9101],
            ArtistNames = ["探针歌手"],
            AlbumId = 9201,
            CoverUrl = covers.Count > 0 ? covers[index % covers.Count] : "",
        }).ToList();

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

    private readonly record struct Row(
        int Round,
        string Label,
        double Cpu,
        double Gpu,
        double GpuPeak,
        double AllocatedMb,
        int Gc0,
        int Gc1,
        int Gc2,
        double DwmSeconds,
        int Nodes,
        int TrackRows,
        int RetainedRows,
        string Threads);
}
