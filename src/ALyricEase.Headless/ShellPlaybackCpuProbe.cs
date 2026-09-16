using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
/// 真实 AppShell + 真实播放链路下的"后台播放 CPU"对照探针(--shell-cpu-real)。
///
/// 为什么还需要这个探针:上一轮的 --progress-visibility 用的是**自建的三矩形窗口**,
/// 只证明了"循环的活性受不受窗口可见性约束",量级说明不了真实应用
/// (请求一帧的代价与场景复杂度成正比)。本探针改用与 MainWindow.axaml 逐字节一致的
/// TestMainWindow(真 AppShell + 真 PlayerBarView 进度条 + 真 NowPlayingView + 动态背景),
/// 并由真实 PlayerViewModel 驱动,量的才是用户能在任务管理器里看到的那个数。
///
/// 单变量:
///   1) 窗口状态:可见 / 最小化
///   2) 当前页面:首页 / 正在播放页(--showNowPlaying,决定第二个动画器实例是否运行)
///   3) 进度上报:开(真实播放口径,5Hz) / 关(活性判定口径)
/// 每一对"循环关→循环开"紧邻配对,差值即该场景下进度动画循环的净代价。
///
/// 活性判据说明:本轮**不再用帧计数**。请求帧的计数器自己就是 RAF 注册者,
/// 会让平台为它出帧 —— 上一轮实测"循环关"也有 65 帧/6s,那个数字量的是
/// "计数器自激的速率",不是平台自主送帧的速率。唯一干净的证据是
/// ProgressFill 的 ScaleX:只有 ProgressRenderAnimator 会写它
/// (或进度样本 SetPlaybackState 一次性改写),所以第一遍刻意关掉进度上报,
/// 此时"6 秒推进 ≈ 6/180"就等价于"循环真的在跑"。
///
/// 口径提醒:本机静息 CPU 抖动可达 3% 单核量级,判定以同一场景内的配对差为主,
/// 跨场景的绝对值不可直接比较(页面不同、元素数不同)。
/// </summary>
internal static class ShellPlaybackCpuProbe
{
    /// <summary>单个测量窗口(毫秒)。够长以压住偶发尖峰。</summary>
    private const int SampleMs = 6000;

    /// <summary>状态切换后的沉降时间:等页面过渡动画与窗口状态真正落地。</summary>
    private const int SettleMs = 900;

    /// <summary>曲目时长:180 秒,便于把 ScaleX 推进量直接换算成"是否按实时速率在走"。</summary>
    private const double DurationMs = 180_000;

    /// <summary>真实播放器的进度上报周期(毫秒),与 WindowsMediaPlayer 的轮询同量级。</summary>
    private const int PositionReportMs = 200;

    private static DispatcherTimer? _positionTimer;
    private static long _virtualPositionMs;
    private static HeadlessApp.StubAudioPlayer? _stub;

    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[shell-cpu] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };

        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[shell-cpu] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        var process = Process.GetCurrentProcess();
        var main = ServiceLocator.Get<MainViewModel>();

        // 与 App.axaml.cs 完全一致:new MainWindow { DataContext = vm }
        // ShowActivated=false:探针不该抢用户的焦点(ShouldAnimate 目前也不看激活状态)。
        var window = new TestMainWindow { DataContext = main, ShowActivated = false };
        window.Show();
        await DrainAsync(800);

        var bar = window.GetVisualDescendants().OfType<PlayerProgressBar>().FirstOrDefault();
        var nowPlaying = window.GetVisualDescendants().OfType<NowPlayingView>().FirstOrDefault();
        var barFill = bar?.FindControl<Border>("Fill");
        var nowPlayingFill = nowPlaying?.FindControl<Border>("ProgressFill");
        var barAnimator = FindAnimator(bar);
        var nowPlayingAnimator = FindAnimator(nowPlaying);

        Log($"[shell-cpu] 装配: 底部进度条={(bar is null ? "未找到" : "已挂")} " +
            $"正在播放页={(nowPlaying is null ? "未找到" : "已挂")} " +
            $"动画器实例 底部={Describe(barAnimator)} 正在播放页={Describe(nowPlayingAnimator)}");
        if (bar is null || nowPlaying is null || barFill is null || nowPlayingFill is null)
        {
            Log("[shell-cpu] FAIL: 真实窗口里没装配出两个进度条,探针前提不成立");
            return 1;
        }

        StartPlayback();

        // 预热:把每个场景态都先走一遍。首帧的样式解析、Skia 管线、字体/封面首次光栅化
        // 与 Acrylic 层首次合成都不是任何一种稳态,不预热会让第一个窗口串进冷启动尾巴
        // (上一版实测第一个"可见·循环关"量到 34.37%,而稳态同场景只有 ~20%)。
        await WarmUpAsync(window, main, barAnimator, nowPlayingAnimator, barFill, nowPlayingFill, process);

        var rows = new List<Row>();

        // ---- 第一遍:进度上报关闭 —— 活性判定 + 纯循环代价 ----
        Log("[shell-cpu] ===== 第一遍: 关闭进度上报(唯一干净口径:ScaleX 只由动画器写) =====");
        await RunScenesAsync(window, main, barAnimator, nowPlayingAnimator, barFill, nowPlayingFill,
            process, rows, reportPosition: false);

        // ---- 第二遍:进度上报开启(真实播放口径) ----
        Log("[shell-cpu] ===== 第二遍: 开启 5Hz 进度上报(真实播放口径的 CPU) =====");
        await RunScenesAsync(window, main, barAnimator, nowPlayingAnimator, barFill, nowPlayingFill,
            process, rows, reportPosition: true);

        StopPositionReporting();
        window.Close();
        await DrainAsync(400);

        Report(rows);
        Log("[shell-cpu] PASS: 真实 shell 下可见性/页面/上报三个自变量均已量清");
        return 0;
    }

    /// <summary>把四个场景态各走一遍(每态停留片刻),让后续每个测量窗口都落在稳态上。</summary>
    private static async Task WarmUpAsync(
        Window window,
        MainViewModel main,
        ProgressRenderAnimator? barAnimator,
        ProgressRenderAnimator? nowPlayingAnimator,
        Control barFill,
        Control nowPlayingFill,
        Process process)
    {
        foreach (var nowPlaying in new[] { false, true })
        {
            if (main.ShowNowPlaying != nowPlaying)
            {
                if (nowPlaying) main.OpenNowPlayingCommand.Execute(null);
                else main.CloseNowPlayingCommand.Execute(null);
            }

            foreach (var minimized in new[] { false, true })
            {
                if (!window.IsVisible) window.Show();
                window.WindowState = minimized ? WindowState.Minimized : WindowState.Normal;
                await Task.Delay(1200);
            }
        }

        window.WindowState = WindowState.Normal;
        if (main.ShowNowPlaying) main.CloseNowPlayingCommand.Execute(null);
        await Task.Delay(600);
        Log("[shell-cpu] 预热完成(四个场景态已各走一遍)");
    }


    /// <summary>一个场景 = 页面 × 窗口状态;每对内部先量"循环关"再量"循环开"(紧邻配对)。
    /// ALY_SHELL_ONLY=minimized 只跑最小化场景;ALY_SHELL_ROUNDS=n 重复 n 轮(取配对差值中位数)。</summary>
    private static async Task RunScenesAsync(
        Window window,
        MainViewModel main,
        ProgressRenderAnimator? barAnimator,
        ProgressRenderAnimator? nowPlayingAnimator,
        Control barFill,
        Control nowPlayingFill,
        Process process,
        List<Row> rows,
        bool reportPosition)
    {
        var phase = reportPosition ? "上报开" : "上报关";
        SetPositionReporting(reportPosition);

        var all = new (string Page, bool NowPlaying, bool Minimized)[]
        {
            ("首页", false, false),
            ("首页", false, true),
            ("正在播放页", true, false),
            ("正在播放页", true, true),
        };
        var scenes = Env("ALY_SHELL_ONLY") == "minimized"
            ? all.Where(scene => scene.Minimized).ToArray()
            : all;
        var rounds = EnvInt("ALY_SHELL_ROUNDS", 1);

        for (var round = 1; round <= rounds; round++)
        {
            foreach (var scene in scenes)
            {
                foreach (var loopOn in new[] { false, true })
                {
                    var label = $"{scene.Page}·{(scene.Minimized ? "最小化" : "可见")}·循环{(loopOn ? "开" : "关")}";
                    var row = await MeasureAsync(
                        window, main, barAnimator, nowPlayingAnimator, barFill, nowPlayingFill,
                        process, label, phase, round, scene.NowPlaying, scene.Minimized, loopOn);
                    rows.Add(row);
                    Log($"[shell-cpu] R{round} {label,-22} 实际={row.StateDescription,-24} " +
                        $"底部条推进={row.BarAdvance,8:F5} 正在播放页推进={row.NowPlayingAdvance,8:F5} " +
                        $"(期望 {row.Expected,7:F5}) CPU={row.Cpu,6:F2}% 循环标记={row.LoopFlag}");
                }
            }
        }
    }

    private static async Task<Row> MeasureAsync(
        Window window,
        MainViewModel main,
        ProgressRenderAnimator? barAnimator,
        ProgressRenderAnimator? nowPlayingAnimator,
        Control barFill,
        Control nowPlayingFill,
        Process process,
        string label,
        string phase,
        int round,
        bool nowPlaying,
        bool minimized,
        bool loopOn)
    {
        // ---- 页面与窗口状态先就位(这两者自身会触发 SetActive,所以必须早于循环开关) ----
        if (main.ShowNowPlaying != nowPlaying)
        {
            if (nowPlaying) main.OpenNowPlayingCommand.Execute(null);
            else main.CloseNowPlayingCommand.Execute(null);
        }

        if (!window.IsVisible) window.Show();
        window.WindowState = minimized ? WindowState.Minimized : WindowState.Normal;
        await Task.Delay(SettleMs);

        // ---- 循环开关:唯一在配对之间变化的变量 ----
        barAnimator?.SetActive(loopOn);
        nowPlayingAnimator?.SetActive(loopOn);
        await Task.Delay(300);

        // 状态读回:排除"我们以为切过去了、其实没切"这种假前提。
        var stateDescription = $"state={window.WindowState},visible={window.IsVisible}";

        var barStart = FillScale(barFill);
        var nowPlayingStart = FillScale(nowPlayingFill);
        var wall = Stopwatch.StartNew();
        var cpu = await MeasureCpuAsync(process, SampleMs);
        wall.Stop();
        var barEnd = FillScale(barFill);
        var nowPlayingEnd = FillScale(nowPlayingFill);

        var seconds = wall.Elapsed.TotalSeconds;
        var expected = seconds * 1000 / DurationMs;

        return new Row(
            label,
            phase,
            round,
            stateDescription,
            cpu,
            barEnd - barStart,
            nowPlayingEnd - nowPlayingStart,
            expected,
            $"{DescribeFlag(barAnimator)}/{DescribeFlag(nowPlayingAnimator)}");
    }

    /// <summary>判定表:把同一场景的"关/开"配对起来。多轮时取配对差值的中位数(抗系统调度与 GC 尖峰)。</summary>
    private static void Report(List<Row> rows)
    {
        Log("[shell-cpu] ===== 判定:最小化时循环是否仍在跑(用上报关那遍的 ScaleX 推进) =====");
        foreach (var scene in new[] { "首页", "正在播放页" })
        {
            var visible = Find(rows, $"{scene}·可见·循环开", "上报关");
            var minimized = Find(rows, $"{scene}·最小化·循环开", "上报关");
            if (visible is null || minimized is null) continue;

            Log($"[shell-cpu] {scene}: 可见时推进={visible.BarAdvance:F5}/{visible.NowPlayingAdvance:F5}(底部/正在播放页) " +
                $"最小化时推进={minimized.BarAdvance:F5}/{minimized.NowPlayingAdvance:F5}");
        }

        Log("[shell-cpu] ===== 判定:进度动画循环的净代价(同场景、同上报口径下的关/开配对差) =====");
        foreach (var phase in new[] { "上报关", "上报开" })
        {
            foreach (var scene in new[] { "首页·可见", "首页·最小化", "正在播放页·可见", "正在播放页·最小化" })
            {
                var deltas = new List<double>();
                var advances = new List<double>();
                var expecteds = new List<double>();
                foreach (var off in RowsOf(rows, $"{scene}·循环关", phase))
                {
                    if (Find(rows, $"{scene}·循环开", phase, off.Round) is not { } on) continue;
                    deltas.Add(on.Cpu - off.Cpu);
                    advances.Add(on.BarAdvance);
                    expecteds.Add(on.Expected);
                }

                if (deltas.Count == 0) continue;

                // 活性判定只在"上报关"那遍做:上报开时 SetPlaybackState 每 200ms 也会写一次
                // ScaleX(位置样本一次性改写),推进量与"循环在跑"无关,拿它判定会得出
                // "循环在跑"的假结论 —— 与同一份报告里"已停摆"的判定自相矛盾。
                var running = phase == "上报关"
                    ? advances.Count > 0 && Median(advances) > Median(expecteds) * 0.5
                    : (bool?)null;
                Log($"[shell-cpu] [{phase}] {scene,-16} 配对 {deltas.Count} 组 " +
                    $"⇒ 循环代价中位数={Median(deltas),6:+0.00;-0.00}% " +
                    (running is null ? "活性见上报关那遍" : $"循环{(running.Value ? "在跑" : "未跑")}") +
                    $" ({string.Join("/", deltas.Select(value => value.ToString("+0.00;-0.00")))})");
            }
        }

        // 期望值对照:改动前最小化时这里应仍等于"实时速率",改动后应回落到 0。
        Log("[shell-cpu] ===== 判定:最小化时是否停摆(推进量 / 期望推进量) =====");
        foreach (var phase in new[] { "上报关" })
        {
            foreach (var scene in new[] { "首页·最小化", "正在播放页·最小化" })
            {
                var on = RowsOf(rows, $"{scene}·循环开", phase);
                if (on.Count == 0) continue;
                var ratio = on[0].Expected > 0 ? on[0].BarAdvance / on[0].Expected : 0;
                Log($"[shell-cpu] [{phase}] {scene,-16} 底部条推进/期望={ratio:F3} " +
                    $"⇒ {(ratio > 0.5 ? "循环仍在跑(后台每帧开销照旧)" : "循环已停摆")}");
            }
        }
    }

    private static List<Row> RowsOf(List<Row> rows, string label, string phase, int? round = null)
    {
        var result = new List<Row>();
        foreach (var row in rows)
            if (row.Label == label && row.Phase == phase && (round is null || row.Round == round))
                result.Add(row);
        return result;
    }

    private static double Median(List<double> samples)
    {
        if (samples.Count == 0) return 0;
        var sorted = new List<double>(samples);
        sorted.Sort();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>把播放器推进到"正在播放"状态:走真实 PlayerViewModel 订阅链路,不直接改 VM 的 IsPlaying。</summary>
    private static void StartPlayback()
    {
        var player = ServiceLocator.Get<PlayerViewModel>();
        _stub = ServiceLocator.Get<IAudioPlayer>() as HeadlessApp.StubAudioPlayer;

        player.CurrentSong = new Song
        {
            Id = 850_001,
            Source = (MusicSource)99,
            Name = "后台播放 CPU 探针",
            Artist = "探针歌手",
            Album = "探针专辑",
            DurationMs = (int)DurationMs,
        };
        player.DurationMs = (long)DurationMs;
        player.ScrubPositionMs = 0;

        if (_stub is not null)
        {
            _stub.SetState(PlaybackState.Playing);
        }
        else
        {
            // 桩不可驱动时退化为直接置位:循环照样会跑,只是没有真实的状态回调链路。
            Log("[shell-cpu] 警告: 播放器桩不可驱动,改为直接设置 IsPlaying");
            player.IsPlaying = true;
        }

        _virtualPositionMs = 0;
        _positionTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(PositionReportMs),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _virtualPositionMs += PositionReportMs;
                _stub?.RaisePosition(_virtualPositionMs);
            });

        Log($"[shell-cpu] 播放已启动: 曲目 180s, 进度上报 {PositionReportMs}ms/次 " +
            $"(真实引擎同量级), 桩={(_stub is null ? "不可驱动" : "可驱动")}");
    }

    private static void SetPositionReporting(bool enabled)
    {
        if (enabled) _positionTimer?.Start();
        else _positionTimer?.Stop();
    }

    private static void StopPositionReporting()
    {
        _positionTimer?.Stop();
        _positionTimer = null;
    }

    private static ProgressRenderAnimator? FindAnimator(object? owner)
    {
        if (owner is null) return null;
        var field = owner.GetType().GetField(
            "_progressVisual", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(owner) as ProgressRenderAnimator;
    }

    /// <summary>读动画器"是否已挂着一帧"的私有标记 —— 仅作结构佐证,不作判定依据。</summary>
    private static string DescribeFlag(ProgressRenderAnimator? animator)
    {
        if (animator is null) return "无";
        var field = typeof(ProgressRenderAnimator).GetField(
            "_frameRequested", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(animator) is true ? "挂帧" : "空闲";
    }

    private static string Describe(ProgressRenderAnimator? animator) => animator is null ? "无" : "有";

    private static double FillScale(Control? fill)
        => fill?.RenderTransform is ScaleTransform scale ? scale.ScaleX : double.NaN;

    private static Row? Find(List<Row> rows, string label, string phase, int? round = null)
    {
        foreach (var row in rows)
            if (row.Label == label && row.Phase == phase && (round is null || row.Round == round))
                return row;
        return null;
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    /// <summary>量一段时间内进程的 CPU 时间增量,折算成"占单核百分之多少"。单核口径 = 不除以核数。</summary>
    private static async Task<double> MeasureCpuAsync(Process process, int milliseconds)
    {
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var wallStart = Stopwatch.GetTimestamp();

        await Task.Delay(milliseconds);

        var wall = Stopwatch.GetElapsedTime(wallStart).TotalSeconds;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        return wall > 0 ? cpu / wall * 100 : 0;
    }

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

    private sealed record Row(
        string Label,
        string Phase,
        int Round,
        string StateDescription,
        double Cpu,
        double BarAdvance,
        double NowPlayingAdvance,
        double Expected,
        string LoopFlag);
}
