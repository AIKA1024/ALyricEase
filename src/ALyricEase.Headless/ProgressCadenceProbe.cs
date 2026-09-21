using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// 进度条**帧节奏**探针(--progress-cadence-real)。
///
/// 起因是一条用户反馈:"进度条动的卡卡的,但如果鼠标一直移动就会变顺"。
/// 这句话本身就是诊断信息 —— 它指向"送帧被某种和输入相关的门控卡住",而不是"画得不够快"。
///
/// 框架侧读到的链路(Avalonia 12.1.1):
///   `TopLevel.RequestAnimationFrame` → `MediaContext._clock.RequestAnimationFrame` →
///   `ScheduleRender(now: false)`,回调只在渲染帧里由 `_clock.Pulse` 放出来。
///   于是进度条的顺滑度**完全等于"渲染帧来得多规律"**。
///
/// 已用同口径对照排除掉的猜想(都别重复做):
///   - **输入把渲染降级到 Input 优先级**(`MediaContext` 的 `InputStarvationTimeout` 路径):
///     `ALY_DISPATCH_STARVE=3600` 关掉它,静息仍 102 次停顿。
///   - **进程定时器分辨率**:`timeBeginPeriod(1)` 前后 102 vs 101 次停顿,无差别。
///   - **页面重不重**:详情页上灌输入也无效,首页上有效 —— 但那是"灌输入能不能翻盘",
///     不是"静息为什么卡"。
///
/// 这一轮要回答的问题只剩一个:**静息时那 ~74ms 的成簇停顿,发生在渲染时钟那一侧,还是
/// UI 线程分发那一侧?** 三个新仪器分别从三个方向夹它:
///
///   ① **帧时间戳轴 vs 墙钟轴**(`ProgressRenderAnimator.FrameDelivered` 现在传框架给的
///      `TimeSpan.Ticks`)。同一批交付:墙钟说"帧彼此相隔 6ms",帧时钟说"它们代表相隔 74ms 的
///      两刻" ⇒ 帧是排队后补发的(下游);帧时钟自己也跳 74ms ⇒ 上游本来就那样。
///      另看 `帧时钟跨度`:8 秒墙钟里帧时钟只走了 5 秒,就是渲染时钟丢了节拍。
///   ② **裸 RAF 旁证**:同一时钟上再挂一个**什么都不做**、只续订的循环。它和动画器共用送帧,
///      所以"裸循环匀、动画器成簇"⇒ 是动画器自己的写入在拖累;两个都成簇 ⇒ 交付本身的问题。
///   ③ **后台→UI 线程投递延迟**:渲染帧的投递路径**正好就是这个形状**(渲染定时器在后台线程,
///      每个节拍把作业 Post 给 UI 线程)。静息时它 ≈ 74ms ⇒ 卡在分发;它 ≈ 0ms 而帧还是不来
///      ⇒ 卡在上游(后台定时器压根没按 60FPS 投)。
///      ⚠ 这个仪器本身会喂活 UI 线程,所以**只在专门的相位开**,其余相位关掉,否则基线被污染。
///
/// 相位(同一窗口、同一播放状态):
///   1/3/5. 静息(仪器全关,用来取基线并确认可复现)
///   2.     灌 WM_MOUSEMOVE(阳性对照:用户说的"鼠标一动就顺")
///   4.     静息 + 每 16ms 从后台线程投一个空作业(读投递延迟;顺带看"喂活能不能治")
///
/// 判据:**帧间隔的规律性**,不是帧率。一秒钟只走几个像素的细进度条,14fps 和 60fps 的位移差
/// 肉眼分不出来;但**间隔忽长忽短的抖动**会被看成"一顿一顿"。所以报 p50/p90/p99/max
/// 与超阈值停顿计数,不报"平均 fps 是多少"就完事。
/// 另外单看"写入多少帧/秒"是有意义的:动画器的 60FPS 闸门让**成簇的多帧里只有第一帧真的写**,
/// 所以写入次数 ≈ 用户眼里每秒变了几次 —— 静息 10.4/秒 就是"一顿一顿"的直接来源。
///
/// 诊断旋钮:`ALY_BARE_RAF=0` 关掉裸 RAF 旁证;`ALY_INPUT_MODE=mouse|saturate` 改灌输入强度。
/// </summary>
internal static class ProgressCadenceProbe
{
    /// <summary>每个相位的采样时长(毫秒)。</summary>
    private const int PhaseMs = 8000;

    /// <summary>状态切换后的沉降时间。</summary>
    private const int SettleMs = 900;

    /// <summary>曲目时长:180 秒,便于把 ScaleX 推进量直接换算成"是否按实时速率在走"。</summary>
    private const double DurationMs = 180_000;

    /// <summary>真实播放器的进度上报周期(毫秒),与 WindowsMediaPlayer 的轮询同量级。</summary>
    private const int PositionReportMs = 200;

    /// <summary>低优先级超时测量用的节拍间隔。</summary>
    private const int LatencyTickMs = 100;

    /// <summary>后台→UI 线程投递探针的周期:16ms,与 60FPS 的渲染投递同量级。</summary>
    private const int PostProbeMs = 16;

    private static DispatcherTimer? _positionTimer;
    private static long _virtualPositionMs;
    private static HeadlessApp.StubAudioPlayer? _stub;

    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[cadence] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };

        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[cadence] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        ApplyPlatformTweaks();
        var main = ServiceLocator.Get<MainViewModel>();

        // 与 App.axaml.cs 一致的真窗口;ShowActivated=false 免得抢用户焦点。
        // ⚠ Topmost 是必须的:窗口被别的窗口盖住时 DWM 不合成它,送帧节奏会被合成器压掉,
        // 量到的就不是"用户看得见时的节奏"了(--idle-cpu-real 踩过同一个坑)。
        var window = new TestMainWindow { DataContext = main, ShowActivated = false, Topmost = true };
        window.Show();
        await DrainAsync(800);

        var bar = window.GetVisualDescendants().OfType<PlayerProgressBar>().FirstOrDefault();
        var nowPlaying = window.GetVisualDescendants().OfType<NowPlayingView>().FirstOrDefault();
        var barFill = bar?.FindControl<Border>("Fill");
        var nowPlayingFill = nowPlaying?.FindControl<Border>("ProgressFill");
        var barAnimator = FindAnimator(bar);
        var nowPlayingAnimator = FindAnimator(nowPlaying);
        if (bar is null || nowPlaying is null || barFill is null || nowPlayingFill is null)
        {
            Log("[cadence] FAIL: 真实窗口里没装配出两个进度条,探针前提不成立");
            return 1;
        }

        if (main.ShowNowPlaying != true) main.OpenNowPlayingCommand.Execute(null);
        StartPlayback();
        await DrainAsync(SettleMs);

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var starveRaw = Env("ALY_DISPATCH_STARVE");
        Log($"[cadence] 装配: 窗口句柄=0x{handle:X} 底部条动画器={(barAnimator is null ? "无" : "有")} " +
            $"正在播放页动画器={(nowPlayingAnimator is null ? "无" : "有")} " +
            $"输入强度={InputMode()} 裸RAF={(Env("ALY_BARE_RAF") == "0" ? "关" : "开")} " +
            $"降级阈值={(starveRaw.Length > 0 ? starveRaw + "s" : "默认(1s)")}");

        // ---- 钩子:帧交付 / 帧写入 ----
        // 交付记两份时间轴:墙钟(帧**什么时候到**)与框架帧时间戳(帧**代表哪一刻**)。
        // 两条轴的间隔分布一比,就能把"上游渲染时钟本来就不规律"与"帧在 UI 线程排队后补发"分开。
        // 回调都在 UI 线程,列表只在 UI 线程读写。
        var deliveredWall = new Dictionary<ProgressRenderAnimator, List<long>>();
        var deliveredFrame = new Dictionary<ProgressRenderAnimator, List<long>>();
        var rendered = new Dictionary<ProgressRenderAnimator, List<long>>();
        void OnDelivered(ProgressRenderAnimator a, long frameTicks)
        {
            if (!deliveredWall.TryGetValue(a, out var wall)) return;
            wall.Add(Stopwatch.GetTimestamp());
            deliveredFrame[a].Add(frameTicks);
        }
        void OnRendered(ProgressRenderAnimator a, long ts)
        {
            if (rendered.TryGetValue(a, out var list)) list.Add(ts);
        }
        foreach (var animator in new[] { barAnimator, nowPlayingAnimator }.Where(a => a is not null))
        {
            deliveredWall[animator!] = [];
            deliveredFrame[animator!] = [];
            rendered[animator!] = [];
        }
        ProgressRenderAnimator.FrameDelivered += OnDelivered;
        ProgressRenderAnimator.FrameRendered += OnRendered;

        // ---- 旁证:三个优先级的定时器各自"每拍超时多少" ----
        // Send 是最高的那一档:它若也被推迟,说明 UI 线程**在忙/在睡**(而不是被优先级门控);
        // 它若准时而帧来不了,说明卡点在渲染/合成那一侧。
        // ⚠ 实测:静息时 Send/Render/Background **三者同样超时 57ms** ⇒ 优先级仲裁不是变量。
        var sendLatency = new List<double>();
        var renderLatency = new List<double>();
        var backgroundLatency = new List<double>();
        var postLatency = new List<double>();
        using var sendTimer = new LatencyMeter(DispatcherPriority.Send, sendLatency);
        using var renderTimer = new LatencyMeter(DispatcherPriority.Render, renderLatency);
        using var backgroundTimer = new LatencyMeter(DispatcherPriority.Background, backgroundLatency);

        // ---- 裸 RAF 旁证:同一时钟上再挂一个什么都不做的续订循环 ----
        // 全程开着,保证各相位可比(它自己不做视觉写入,不该改变送帧调度;
        // 但它和动画器共用送帧,所以两者节奏一比就能分开"交付问题"与"写入问题")。
        var bare = new BareFrameLoop(window);
        if (Env("ALY_BARE_RAF") != "0") bare.Start();

        // 预热:让页过渡、Skia 首帧、Acrylic 首次合成都走完,否则第一相会串进冷启动尾巴。
        await DrainAsync(1500);
        ClearDelivery(deliveredWall, deliveredFrame, rendered);
        ClearLists(renderLatency, backgroundLatency, sendLatency, postLatency);

        var phases = new PhaseSpec[]
        {
            new("1·首页·静息", FloodKind.None, NowPlaying: false, PostProbe: false),
            new("2·首页·灌鼠标", FloodKind.MouseMove, NowPlaying: false, PostProbe: false),
            new("3·首页·静息·复测", FloodKind.None, NowPlaying: false, PostProbe: false),
            // 关键相位:仪器③只在这一次开 —— 它会喂活 UI 线程,所以绝不能和基线同相测。
            // 读数有两重含义:里面量到的投递延迟 ≈ 渲染帧要等多久;外面量到的帧节奏
            // 顺带回答"给 UI 线程喂活能不能治好卡顿"。
            new($"4·首页·静息·后台每{PostProbeMs}ms投递", FloodKind.None, NowPlaying: false, PostProbe: true),
            new("5·首页·静息·复测2", FloodKind.None, NowPlaying: false, PostProbe: false),
        };

        var results = new List<PhaseResult>();
        foreach (var phase in phases)
        {
            if (main.ShowNowPlaying != phase.NowPlaying)
            {
                if (phase.NowPlaying) main.OpenNowPlayingCommand.Execute(null);
                else main.CloseNowPlayingCommand.Execute(null);
            }
            await DrainAsync(SettleMs);

            ClearDelivery(deliveredWall, deliveredFrame, rendered);
            ClearLists(renderLatency, backgroundLatency, sendLatency, postLatency);
            bare.Reset();
            // ⚠ 必读:别靠"读数没变"去猜帧泵有没有在跑。这里是它自报的内部状态 +
            // 累计投递数 —— 相邻相位两个数的差就是上一相位它真的投了多少次。
            Log($"[cadence]   (相位起点自检) 帧泵: {UiFramePacer.Describe()}");
            var scaleBefore = (FillScale(barFill), FillScale(nowPlayingFill));
            var wall = Stopwatch.StartNew();

            // 仪器③ 与 灌输入 都只在被测相位内存在,相位一结束立刻撤掉。
            using var postMeter = phase.PostProbe ? new PostLatencyMeter(postLatency, PostProbeMs) : null;
            var flooder = phase.Flood == FloodKind.None ? null : InputFlood.Start(handle, phase.Flood, InputMode());
            await Task.Delay(PhaseMs);
            flooder?.Dispose();
            wall.Stop();

            var bareSample = bare.Snapshot();
            var scaleAfter = (FillScale(barFill), FillScale(nowPlayingFill));
            var expected = wall.Elapsed.TotalSeconds * 1000 / DurationMs;

            var result = new PhaseResult(
                phase.Label,
                phase.Flood,
                wall.Elapsed.TotalSeconds,
                Describe(barAnimator, deliveredWall, deliveredFrame, rendered),
                Describe(nowPlayingAnimator, deliveredWall, deliveredFrame, rendered),
                Snapshot(renderLatency),
                Snapshot(backgroundLatency),
                Snapshot(sendLatency),
                Snapshot(postLatency),
                Bare(bareSample),
                scaleAfter.Item1 - scaleBefore.Item1,
                scaleAfter.Item2 - scaleBefore.Item2,
                expected);
            results.Add(result);

            Log($"[cadence] ===== 相位 {phase.Label}(队列: {DescribeFlood(phase.Flood)}, " +
                $"详情页={phase.NowPlaying}, 后台投递探针={(phase.PostProbe ? PostProbeMs + "ms" : "关")})=====");
            ReportAnimator(phase.Label, "底部条", result.Bar);
            ReportAnimator(phase.Label, "正在播放页", result.NowPlaying);
            ReportBare(phase.Label, result.Bare);
            Log($"[cadence]   推进: 底部条={result.BarAdvance:F5} 正在播放页={result.NowPlayingAdvance:F5} " +
                $"(实时速率应为 {result.Expected:F5}) ⇒ {Verdict(result.BarAdvance, result.Expected)}");
            ReportLatency(phase.Label, "Send", result.SendLatency);
            ReportLatency(phase.Label, "Render", result.RenderLatency);
            ReportLatency(phase.Label, "Background", result.BackgroundLatency);
            if (phase.PostProbe) ReportLatency(phase.Label, "后台Post", result.PostLatency);
        }

        ProgressRenderAnimator.FrameDelivered -= OnDelivered;
        ProgressRenderAnimator.FrameRendered -= OnRendered;
        bare.Stop();
        StopPositionReporting();
        window.Close();
        await DrainAsync(400);

        ReportSummary(results);
        return 0;
    }

    /// <summary>把各相并排,给出一句话结论。</summary>
    private static void ReportSummary(List<PhaseResult> results)
    {
        Log("[cadence] ===== 判定:停顿随什么变 =====");
        var pad = new string(' ', 20);
        foreach (var result in results)
        {
            var stats = result.Bar;
            if (stats is null) continue;
            Log($"[cadence]   {result.Label,-20} 交付={stats.DeliveredPerSecond,6:F1}/秒 写入={stats.RenderedPerSecond,6:F1}/秒 " +
                $"p50={Fmt(stats.IntervalP50)} p90={Fmt(stats.IntervalP90)} max={Fmt(stats.IntervalMax)} " +
                $"抖动={Fmt(stats.IntervalStdDev)} 停顿(≥25ms)={stats.Stalls25,3}");
            Log($"[cadence]   {pad} 帧时钟: 跨度={stats.FrameSpanMs,7:F0}ms 间隔 p50={Fmt(stats.FrameIntervalP50)} " +
                $"p90={Fmt(stats.FrameIntervalP90)} max={Fmt(stats.FrameIntervalMax)}");
            if (result.Bare is { } bareStats)
                Log($"[cadence]   {pad} 裸RAF : 交付={bareStats.DeliveredPerSecond,6:F1}/秒 p50={Fmt(bareStats.IntervalP50)} " +
                    $"p90={Fmt(bareStats.IntervalP90)} 停顿={bareStats.Stalls25,3} " +
                    $"帧时钟跨度={bareStats.FrameSpanMs,7:F0}ms");
            Log($"[cadence]   {pad} 等待  : 后台Post延迟p50={result.PostLatency.P50,6:F1}ms " +
                $"Render定时器超时={result.RenderLatency.P50,6:F1}ms Send={result.SendLatency.P50,6:F1}ms");
        }

        Compare(results, "1·静息", "2·灌鼠标", "灌鼠标输入");
        Compare(results, "1·静息", "3·静息·复测", "同一状态复测(应为无差别)");
        Compare(results, "1·静息", $"4·静息·后台每{PostProbeMs}ms投递", "给 UI 线程喂活");
    }

    /// <summary>启动期的平台旋钮(<c>ALY_TIMER_FIX</c>),带**回读校验**。
    ///
    /// 为什么必须校验:早前测过"把进程定时器分辨率抬到 1ms",结论是"无差别"(102 vs 101 次停顿)。
    /// 但那条结论有个漏洞 —— `timeBeginPeriod` 在**被电源节流标记为
    /// `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION` 的进程里会被静默忽略**。
    /// 静默失败伪装成"这个变量无效",会让整条排查走错方向。
    /// 所以这里把返回值、`NtQueryTimerResolution` 的回读都打出来,再顺手清掉那条节流标记。
    ///
    ///   0/空 = 基线;1 = 只抬分辨率;2 = 抬分辨率 + 关电源节流 + 清 IGNORE_TIMER_RESOLUTION。
    /// </summary>
    private static void ApplyPlatformTweaks()
    {
        var mode = Env("ALY_TIMER_FIX");
        Log($"[cadence] 平台旋钮: ALY_TIMER_FIX={mode.Length switch { 0 => "(空)", _ => mode }} " +
            $"(0/空=基线 1=抬分辨率 2=+关电源节流)");
        Log($"[cadence]   改前 {TimerDiagnostics.Query()}");
        if (mode is "" or "0") return;

        var ret = TimerDiagnostics.TimeBeginPeriod(1);
        Log($"[cadence]   timeBeginPeriod(1) → ret={ret}(0=成功) 改后 {TimerDiagnostics.Query()}");

        if (mode != "2") return;
        Log($"[cadence]   关 EXECUTION_SPEED 节流 → {TimerDiagnostics.SetPowerThrottling(0)}");
        Log($"[cadence]   清 IGNORE_TIMER_RESOLUTION → {TimerDiagnostics.SetPowerThrottling(1)}");
        Log($"[cadence]   再抬一次分辨率 → ret={TimerDiagnostics.TimeBeginPeriod(1)} 现在 {TimerDiagnostics.Query()}");
    }

    private static void Compare(List<PhaseResult> results, string aLabel, string bLabel, string what)
    {
        var a = results.FirstOrDefault(r => r.Label == aLabel)?.Bar;
        var b = results.FirstOrDefault(r => r.Label == bLabel)?.Bar;
        if (a is null || b is null) return;
        Log($"[cadence]   {what}: 停顿 {a.Stalls25} vs {b.Stalls25} 次/8秒, " +
            $"p90 {Fmt(a.IntervalP90)} vs {Fmt(b.IntervalP90)}, 写入 {a.RenderedPerSecond:F1} vs {b.RenderedPerSecond:F1}/秒 " +
            $"⇒ {(b.Stalls25 < a.Stalls25 / 2 ? "有效(停顿大幅减少)" : "无显著差别")}");
    }

    private static void ReportAnimator(string phase, string who, AnimatorStats? stats)
    {
        if (stats is null)
        {
            Log($"[cadence]   {who}: 无动画器实例");
            return;
        }
        Log($"[cadence]   {who}: 交付 {stats.Delivered,5} 帧({stats.DeliveredPerSecond,6:F1}/秒) " +
            $"写入 {stats.Rendered,5} 帧({stats.RenderedPerSecond,6:F1}/秒)");
        Log($"[cadence]      交付间隔(墙钟) 均值={Fmt(stats.IntervalMean)} p50={Fmt(stats.IntervalP50)} " +
            $"p90={Fmt(stats.IntervalP90)} p99={Fmt(stats.IntervalP99)} max={Fmt(stats.IntervalMax)} " +
            $"抖动(标准差)={Fmt(stats.IntervalStdDev)}");
        Log($"[cadence]      交付间隔(帧时钟) 跨度={stats.FrameSpanMs,7:F1}ms p50={Fmt(stats.FrameIntervalP50)} " +
            $"p90={Fmt(stats.FrameIntervalP90)} max={Fmt(stats.FrameIntervalMax)} " +
            $"抖动={Fmt(stats.FrameIntervalStdDev)}");
        Log($"[cadence]      停顿: ≥25ms {stats.Stalls25} 次, ≥50ms {stats.Stalls50} 次, " +
            $"≥100ms {stats.Stalls100} 次");
    }

    private static void ReportBare(string phase, AnimatorStats? stats)
    {
        if (stats is null)
        {
            Log("[cadence]   裸RAF旁证: 未启用");
            return;
        }
        Log($"[cadence]   裸RAF旁证: 交付 {stats.Delivered} 帧({stats.DeliveredPerSecond,6:F1}/秒) " +
            $"墙钟间隔 p50={Fmt(stats.IntervalP50)} p90={Fmt(stats.IntervalP90)} 抖动={Fmt(stats.IntervalStdDev)} " +
            $"停顿≥25ms={stats.Stalls25} ‖ 帧时钟跨度={stats.FrameSpanMs,7:F1}ms " +
            $"帧间隔 p50={Fmt(stats.FrameIntervalP50)} max={Fmt(stats.FrameIntervalMax)}");
    }

    private static void ReportLatency(string phase, string who, LatencyStats stats)
    {
        Log($"[cadence]   {who,-10} 每拍超时 均值={stats.Mean:F1}ms p50={stats.P50:F1} " +
            $"p99={stats.P99:F1} max={stats.Max:F1}ms (拍数={stats.Count})" +
            (stats.Count == 0 ? " ⚠ 一拍都没跑到" : ""));
    }

    private static string Verdict(double advance, double expected)
        => expected <= 0 ? "无法判定"
            : advance > expected * 0.5 ? "循环在按实时速率推进" : "⚠ 推进远低于实时速率(循环已被卡住)";

    private static string DescribeFlood(FloodKind flood) => flood switch
    {
        FloodKind.None => "空",
        FloodKind.NullMessage => "有消息但非输入",
        FloodKind.MouseMove => "持续输入",
        _ => "?",
    };

    private static string Fmt(double? value) => value is null or double.NaN ? "  n/a" : $"{value.Value,6:F2}ms";

    private static void ClearDelivery(
        Dictionary<ProgressRenderAnimator, List<long>> deliveredWall,
        Dictionary<ProgressRenderAnimator, List<long>> deliveredFrame,
        Dictionary<ProgressRenderAnimator, List<long>> rendered)
    {
        foreach (var list in deliveredWall.Values) list.Clear();
        foreach (var list in deliveredFrame.Values) list.Clear();
        foreach (var list in rendered.Values) list.Clear();
    }

    private static void ClearLists(params List<double>[] lists)
    {
        foreach (var list in lists) list.Clear();
    }

    private static AnimatorStats? Describe(
        ProgressRenderAnimator? animator,
        Dictionary<ProgressRenderAnimator, List<long>> deliveredWall,
        Dictionary<ProgressRenderAnimator, List<long>> deliveredFrame,
        Dictionary<ProgressRenderAnimator, List<long>> rendered)
    {
        if (animator is null) return null;
        var wall = deliveredWall.TryGetValue(animator, out var w) ? w : [];
        var frame = deliveredFrame.TryGetValue(animator, out var f) ? f : [];
        var r = rendered.TryGetValue(animator, out var rl) ? rl : [];
        return BuildStats(wall, frame, r.Count);
    }

    private static AnimatorStats? Bare((List<long> Wall, List<long> Frame) sample)
        => sample.Wall.Count >= 2 ? BuildStats(sample.Wall, sample.Frame, 0) : null;

    private static AnimatorStats BuildStats(List<long> wall, List<long> frame, int renderedCount)
    {
        var intervals = ToIntervals(wall);
        var frameIntervals = ToIntervals(frame);
        var seconds = intervals.Sum() / 1000.0;
        return new AnimatorStats(
            wall.Count,
            renderedCount,
            seconds > 0 ? wall.Count / seconds : 0,
            seconds > 0 ? renderedCount / seconds : 0,
            Mean(intervals), Percentile(intervals, 0.50), Percentile(intervals, 0.90),
            Percentile(intervals, 0.99), intervals.Count > 0 ? intervals.Max() : 0,
            StdDev(intervals),
            intervals.Count(v => v >= 25), intervals.Count(v => v >= 50), intervals.Count(v => v >= 100),
            FrameSpanMs(frame),
            Percentile(frameIntervals, 0.50), Percentile(frameIntervals, 0.90),
            frameIntervals.Count > 0 ? frameIntervals.Max() : 0, StdDev(frameIntervals));
    }

    /// <summary>框架帧时间戳(<see cref="TimeSpan.Ticks"/>,1 tick = 100ns)首尾跨度 → 毫秒。</summary>
    private static double FrameSpanMs(List<long> frameTicks)
        => frameTicks.Count >= 2 ? (frameTicks[^1] - frameTicks[0]) / 10_000.0 : 0;

    /// <summary>Stopwatch 时间戳序列 → 相邻间隔(毫秒)。丢掉首帧(它是相位起点,没有前一段)。</summary>
    private static List<double> ToIntervals(List<long> timestamps)
    {
        var intervals = new List<double>();
        for (var i = 1; i < timestamps.Count; i++)
            intervals.Add((timestamps[i] - timestamps[i - 1]) * 1000.0 / Stopwatch.Frequency);
        return intervals;
    }

    private static double Mean(List<double> values) => values.Count == 0 ? double.NaN : values.Average();

    private static double StdDev(List<double> values)
    {
        if (values.Count < 2) return double.NaN;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = new List<double>(values);
        sorted.Sort();
        var index = (int)Math.Round(fraction * (sorted.Count - 1));
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static LatencyStats Snapshot(List<double> values)
        => values.Count == 0
            ? new LatencyStats(0, double.NaN, double.NaN, double.NaN, double.NaN)
            : new LatencyStats(values.Count, values.Average(), Percentile(values, 0.5),
                Percentile(values, 0.99), values.Max());

    private static ProgressRenderAnimator? FindAnimator(object? owner)
    {
        if (owner is null) return null;
        var field = owner.GetType().GetField(
            "_progressVisual", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(owner) as ProgressRenderAnimator;
    }

    private static double FillScale(Control? fill)
        => fill?.RenderTransform is ScaleTransform scale ? scale.ScaleX : double.NaN;

    private static string InputMode() => Env("ALY_INPUT_MODE") == "saturate" ? "saturate" : "mouse";

    /// <summary>把播放器推进到"正在播放"状态:走真实 PlayerViewModel 订阅链路,不直接改 VM 的 IsPlaying。</summary>
    private static void StartPlayback()
    {
        var player = ServiceLocator.Get<PlayerViewModel>();
        _stub = ServiceLocator.Get<IAudioPlayer>() as HeadlessApp.StubAudioPlayer;

        player.CurrentSong = new Song
        {
            Id = 860_001,
            Source = (MusicSource)99,
            Name = "进度条帧节奏探针",
            Artist = "探针歌手",
            Album = "探针专辑",
            DurationMs = (int)DurationMs,
        };
        player.DurationMs = (long)DurationMs;
        player.ScrubPositionMs = 0;

        if (_stub is not null) _stub.SetState(PlaybackState.Playing);
        else player.IsPlaying = true;

        _virtualPositionMs = 0;
        _positionTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(PositionReportMs),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _virtualPositionMs += PositionReportMs;
                _stub?.RaisePosition(_virtualPositionMs);
            });
        _positionTimer.Start();
    }

    private static void StopPositionReporting()
    {
        _positionTimer?.Stop();
        _positionTimer = null;
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static async Task DrainAsync(int milliseconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { }
    }

    private enum FloodKind
    {
        None,
        NullMessage,
        MouseMove,
    }

    /// <summary>按固定间隔测一个 <see cref="DispatcherPriority"/> 下定时器的"每拍超时"。
    /// Background(-2) 正好落在 `ExecuteJobsCore` 那条"队列里有输入就推迟"的门控里,
    /// Render(4) 不受它管 —— 两者的差就是那条门控的实测后果(实测:静息时三档同样超时
    /// 57ms,说明门控不是变量,UI 线程整体就没被唤醒)。</summary>
    private sealed class LatencyMeter : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private long _lastTick;

        public LatencyMeter(DispatcherPriority priority, List<double> sink)
        {
            _timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(LatencyTickMs),
                priority,
                (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    if (_lastTick != 0)
                        sink.Add((now - _lastTick) * 1000.0 / Stopwatch.Frequency - LatencyTickMs);
                    _lastTick = now;
                    if (sink.Count > 4000) sink.RemoveRange(0, 2000);
                });
            _timer.Start();
        }

        public void Dispose() => _timer.Stop();
    }

    /// <summary>量"从别的线程往 UI 线程投一个作业,到它被执行"的延迟,周期 <see cref="PostProbeMs"/>。
    ///
    /// 为什么这条最关键:渲染帧的投递路径**正好就是这个形状** —— 渲染定时器在后台线程上,
    /// 每个节拍把渲染作业 Post 给 UI 线程。于是这条延迟 ≈ "渲染帧要等多久"。
    /// 静息时它若 ≈ 帧间隔的 p90,卡点就在 UI 线程分发这一侧(投递本身准时,是执行被推迟);
    /// 它若 ≈ 0 而帧还是不来,卡点就在上游(后台渲染定时器压根没按 60FPS 投)。
    ///
    /// ⚠ 它自己会喂活 UI 线程,所以只在专门相位开,别把它混进基线。</summary>
    private sealed class PostLatencyMeter : IDisposable
    {
        private readonly Thread _thread;
        private readonly List<double> _sink;
        private volatile bool _stop;

        public PostLatencyMeter(List<double> sink, int intervalMs)
        {
            _sink = sink;
            _thread = new Thread(() =>
            {
                while (!_stop)
                {
                    var posted = Stopwatch.GetTimestamp();
                    Dispatcher.UIThread.Post(
                        () =>
                        {
                            if (_sink.Count >= 20_000) return;
                            _sink.Add((Stopwatch.GetTimestamp() - posted) * 1000.0 / Stopwatch.Frequency);
                        },
                        DispatcherPriority.Render);
                    Thread.Sleep(intervalMs);
                }
            })
            { IsBackground = true, Name = "ALyricEase.PostLatency" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(500);
        }
    }

    /// <summary>同一渲染时钟上的"裸"消费者:只记时间、立刻续订下一帧,**不碰视觉树**。
    ///
    /// 用意:进度条动画器每个回调都会改 `ScaleX`/`TranslateTransform`,那会让这一帧有脏区域;
    /// 于是"帧交付成簇"有两种解释 —— ①交付本来就成簇;②交付匀,但动画器自己的写入让每一帧
    /// 变贵到挤在一起。一个零写入的循环和它共用同一条送帧链路,两者节奏一比就分开了。</summary>
    private sealed class BareFrameLoop
    {
        private readonly TopLevel _topLevel;
        private readonly List<long> _wall = [];
        private readonly List<long> _frame = [];
        private int _generation;
        private bool _running;

        public BareFrameLoop(TopLevel topLevel) => _topLevel = topLevel;

        public void Start()
        {
            _running = true;
            Request();
        }

        public void Stop()
        {
            _running = false;
            _generation++;
        }

        public void Reset()
        {
            _wall.Clear();
            _frame.Clear();
        }

        public (List<long> Wall, List<long> Frame) Snapshot() => ([.. _wall], [.. _frame]);

        private void Request()
        {
            var generation = _generation;
            _topLevel.RequestAnimationFrame(now => OnFrame(generation, now));
        }

        private void OnFrame(int generation, TimeSpan now)
        {
            if (generation != _generation || !_running) return;
            _wall.Add(Stopwatch.GetTimestamp());
            _frame.Add(now.Ticks);
            Request();
        }
    }

    /// <summary>往窗口消息队列里灌消息的线程。`WM_MOUSEMOVE` 会被 `QS_INPUT` 计入
    /// (Avalonia 的 `HasPendingInput` 正是查 `QS_INPUT|QS_EVENT|QS_POSTMESSAGE`),
    /// `WM_NULL` 只会被 `QS_POSTMESSAGE` 计入 —— 用它把"队列有活"与"真的是输入"分开。</summary>
    private sealed class InputFlood : IDisposable
    {
        private const uint WM_NULL = 0x0000;
        private const uint WM_MOUSEMOVE = 0x0200;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly Thread _thread;
        private volatile bool _stop;

        private InputFlood(IntPtr hwnd, FloodKind kind, string mode)
        {
            var message = kind == FloodKind.MouseMove ? WM_MOUSEMOVE : WM_NULL;
            var burst = mode == "saturate" ? 64 : 1;
            var sleepMs = mode == "saturate" ? 0 : 8;
            _thread = new Thread(() =>
            {
                var i = 0;
                while (!_stop)
                {
                    for (var n = 0; n < burst; n++)
                    {
                        // 坐标在窗口内缓慢游走,和"手在窗口里动"更接近;WM_NULL 不关心坐标。
                        var x = 200 + i % 400;
                        var y = 300 + i / 400 % 200;
                        PostMessage(hwnd, message, IntPtr.Zero, (IntPtr)((y << 16) | x));
                        i++;
                    }
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                    else Thread.Yield();
                }
            })
            { IsBackground = true, Name = "ALyricEase.InputFlood" };
            _thread.Start();
        }

        public static InputFlood Start(IntPtr hwnd, FloodKind kind, string mode) => new(hwnd, kind, mode);

        public void Dispose()
        {
            _stop = true;
            _thread.Join(500);
        }
    }

    /// <summary>定时器分辨率与电源节流的**可校验**包装。
    /// 只信回读:`timeBeginPeriod` 返回值 + `NtQueryTimerResolution` 的当前值。</summary>
    private static class TimerDiagnostics
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriodNative(uint period);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr process, int infoClass, ref ProcessPowerThrottlingState state, uint size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        /// <summary><c>ProcessPowerThrottling</c> 的 infoClass 值。</summary>
        private const int ProcessPowerThrottlingClass = 4;

        private const uint ThrottlingVersion1 = 1;
        private const uint ThrottlingExecutionSpeed = 0x1;
        private const uint ThrottlingIgnoreTimerResolution = 0x4;

        public static uint TimeBeginPeriod(uint period) => TimeBeginPeriodNative(period);

        /// <summary>读数单位是 100ns,换算成毫秒。</summary>
        public static string Query()
        {
            try
            {
                var status = NtQueryTimerResolution(out var minimum, out var maximum, out var current);
                return $"当前={current / 10_000.0:F3}ms 范围=[{minimum / 10_000.0:F3},{maximum / 10_000.0:F3}]ms " +
                       $"(NTSTATUS=0x{status:X8})";
            }
            catch (Exception ex) { return "查询失败: " + ex.Message; }
        }

        /// <summary>which=0 关 EXECUTION_SPEED(EcoQoS)节流;which=1 清 IGNORE_TIMER_RESOLUTION。
        /// 两者都是 `ControlMask` 置位 + `StateMask` 清零 = 显式关掉该行为。</summary>
        public static string SetPowerThrottling(int which)
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = ThrottlingVersion1,
                ControlMask = which == 0 ? ThrottlingExecutionSpeed : ThrottlingIgnoreTimerResolution,
                StateMask = 0,
            };
            var ok = SetProcessInformation(
                GetCurrentProcess(), ProcessPowerThrottlingClass, ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
            return ok ? "成功" : $"失败 err={Marshal.GetLastWin32Error()}";
        }
    }

    private sealed record PhaseSpec(string Label, FloodKind Flood, bool NowPlaying, bool PostProbe);

    private sealed record AnimatorStats(
        int Delivered,
        int Rendered,
        double DeliveredPerSecond,
        double RenderedPerSecond,
        double IntervalMean,
        double IntervalP50,
        double IntervalP90,
        double IntervalP99,
        double IntervalMax,
        double IntervalStdDev,
        int Stalls25,
        int Stalls50,
        int Stalls100,
        double FrameSpanMs,
        double FrameIntervalP50,
        double FrameIntervalP90,
        double FrameIntervalMax,
        double FrameIntervalStdDev);

    private sealed record LatencyStats(int Count, double Mean, double P50, double P99, double Max);

    private sealed record PhaseResult(
        string Label,
        FloodKind Flood,
        double Seconds,
        AnimatorStats? Bar,
        AnimatorStats? NowPlaying,
        LatencyStats RenderLatency,
        LatencyStats BackgroundLatency,
        LatencyStats SendLatency,
        LatencyStats PostLatency,
        AnimatorStats? Bare,
        double BarAdvance,
        double NowPlayingAdvance,
        double Expected);
}
