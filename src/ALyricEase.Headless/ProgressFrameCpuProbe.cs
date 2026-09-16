using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>
/// 播放进度动画器的 CPU 代价与"后台是否仍在出帧"验证(--progress-cpu-real,真窗口)。
///
/// 背景:2127c30 把进度条的推进方式从"进度事件驱动(约 5 次/秒改 Width/Margin,顺带触发布局)"
/// 换成 ProgressRenderAnimator —— 它在 RequestAnimationFrame 回调里**自行续订下一帧**,
/// 只要 ShouldAnimate() 成立就按最多 60 FPS 一直跑;而 ShouldAnimate() 只看
/// "已附加 &amp;&amp; 激活 &amp;&amp; 在播放 &amp;&amp; 有进度",**不看窗口是否可见**。
/// 于是"播放期间渲染循环不再空闲" —— 这是"对比早期版本,后台播放 CPU 高了一点"的候选根因。
///
/// 本探针分两段:
///   1) CPU 对照:同一窗口、同一套视觉,每轮先量"循环关"再量"循环开"(配对抵消漂移),
///      只切这一个变量,量进程 TotalProcessorTime 增量 ⇒ 折算"占单核百分之多少"。
///      再单量"窗循环保持开启但窗口最小化/隐藏"的 CPU,判断后台是否真在烧。
///      另打印填充条 ScaleX 的推进量,作为"循环确实在跑"的直接证据 ——
///      只看 CPU 不看推进,循环若早就停了会把"没在跑"误读成"代价很小"。
///   2) 出帧活性:只挂一个自续订的 RequestAnimationFrame 计数器(不启用动画器),
///      分别量窗口可见/最小化/隐藏时平台每秒实际送达多少帧。
///      这段回答的是"窗口不可见时合成器还转不转",与动画器无关。
///
/// 口径提醒:进程级 CPU 含运行时后台线程(GC/线程池)与渲染线程,完整但噪声大。
/// 实测本机静息态的抖动可达 3% 单核量级,与该效应同阶,所以判据看"多轮配对差值的中位数"
/// 而不是单轮值。
/// </summary>
internal static class ProgressFrameCpuProbe
{
    /// <summary>单个测量窗口的时长(毫秒)。噪声与效应同阶,窗口必须够长才压得住偶发尖峰。</summary>
    private const int SampleMs = 10_000;

    /// <summary>开/关配对的轮数:取多轮差值的中位数,抗系统调度与 GC 的偶发尖峰。</summary>
    private const int Rounds = 3;

    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[progress-cpu] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };

        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[progress-cpu] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        var process = Process.GetCurrentProcess();
        Log($"[progress-cpu] 进程 CPU 测量: 单核口径,样本 {SampleMs}ms × {Rounds} 轮配对 " +
            $"逻辑核数={Environment.ProcessorCount} 目标帧率={ProgressRenderAnimator.TargetFramesPerSecond}FPS");

        // 与 PlayerProgressBar 相同结构的最小视觉树:轨道 + 已播放段(拉伸后按 ScaleX) + 球。
        // 只装这三件是为了让"是否出帧"成为唯一变量 —— 带上整页只会把差值淹没在别的工作里。
        var host = new Grid { Width = 1000, Height = 24 };
        var track = new Border
        {
            Height = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45)),
            ClipToBounds = true,
        };
        var fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(0x5E, 0xA9, 0xA2)),
        };
        track.Child = fill;
        var thumb = new Border
        {
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(0x5E, 0xA9, 0xA2)),
        };
        host.Children.Add(track);
        host.Children.Add(thumb);

        var window = new Window
        {
            Width = 1100,
            Height = 300,
            Title = "ALyricEase 进度渲染 CPU 探针",
            ShowActivated = false,
            Content = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "进度动画循环 CPU 对照(探针自动开关,勿关窗口)", Margin = new Thickness(40, 0, 0, 0) },
                    host,
                },
            },
        };
        window.Show();

        var animator = new ProgressRenderAnimator(host, track, fill, thumb);
        animator.Attach();

        // 预热:首帧的样式解析、Skia 首次绘制、着色器与字体载入都不该算进任何一种状态。
        // 第一版只预热 1.5 秒,三轮 ON 的 CPU 是 9.37% → 5.08% → 1.56% 一路下滑 ——
        // 前几轮量到的其实是"首次出帧"的一次性开销,不是稳态代价。
        animator.SetActive(true);
        animator.SetPlaybackState(0, 180_000, isPlaying: true);
        await Task.Delay(5000);
        animator.SetActive(false);
        await Task.Delay(1000);

        var deltas = new List<double>();
        for (var round = 1; round <= Rounds; round++)
        {
            // 配对:紧邻的"关"和"开"共享同一段系统状态,差值抵消慢漂移。
            var idleCpu = await MeasureCpuAsync(process, SampleMs);

            animator.SetActive(true);
            animator.SetPlaybackState(0, 180_000, isPlaying: true);
            var activeCpu = await MeasureCpuAsync(process, SampleMs);
            var delta = activeCpu - idleCpu;
            deltas.Add(delta);

            Log($"[progress-cpu] 第 {round} 轮: 循环关={idleCpu:F2}% 循环开={activeCpu:F2}% 差值={delta:+0.00;-0.00}%");
        }

        var deltaMedian = Median(deltas);
        Log($"[progress-cpu] 配对差值: 各轮=[{string.Join(", ", deltas.ConvertAll(d => d.ToString("F2")))}] " +
            $"中位数={deltaMedian:F2}% 单核 ⇒ 进度动画循环代价");
        Log($"[progress-cpu] ⚠️ 判据说明: 本机静息态抖动可达 3% 单核量级,与该效应同阶;" +
            "若各轮差值正负不一致,应视为不显著,以结构证据(循环是否在推进/是否受可见性约束)为主。");

        // 循环确实在推进的直接证据:填充条的 ScaleX 由动画器每帧改写。
        animator.SetActive(true);
        animator.SetPlaybackState(0, 180_000, isPlaying: true);
        await Task.Delay(200);
        var scaleAt0 = FillScale();
        await Task.Delay(2000);
        var scaleAt2s = FillScale();
        var advance = scaleAt2s - scaleAt0;
        var expected = 2d / 180d;
        Log($"[progress-cpu] 循环推进证据: ScaleX {scaleAt0:F5} → 2秒后 {scaleAt2s:F5} " +
            $"推进={advance:F5} 理论={expected:F5} ⇒ {(advance > expected * 0.5 ? "循环在按实时速率推进" : "循环未推进(已停)")}");

        // 窗口不可见时的代价,循环保持开启。最小化的窗口仍在视觉树上,只是不再合成;
        // 若这里落回"循环关"的水平,说明不可见确实让渲染停摆。
        animator.SetActive(true);
        animator.SetPlaybackState(0, 180_000, isPlaying: true);
        window.WindowState = WindowState.Minimized;
        await Task.Delay(800);
        var minimizedCpu = await MeasureCpuAsync(process, SampleMs);
        window.WindowState = WindowState.Normal;
        await Task.Delay(800);

        window.Hide();
        await Task.Delay(800);
        var hiddenCpu = await MeasureCpuAsync(process, SampleMs);
        window.Show();
        await Task.Delay(800);

        Log($"[progress-cpu] 循环保持开启: 最小化={minimizedCpu:F2}% 隐藏={hiddenCpu:F2}% " +
            $"(对照 可见关闭的静息水平见上表)");

        // ---- 第二段:出帧活性(不启用动画器,只留一个自续订的计数器)----
        animator.SetActive(false);
        await Task.Delay(400);
        var normalFrames = await CountFramesAsync(window, 2000);
        window.WindowState = WindowState.Minimized;
        await Task.Delay(400);
        var minimizedFrames = await CountFramesAsync(window, 2000);
        window.WindowState = WindowState.Normal;
        await Task.Delay(400);
        var normalAgainFrames = await CountFramesAsync(window, 2000);
        window.Hide();
        await Task.Delay(400);
        var hiddenFrames = await CountFramesAsync(window, 2000);
        window.Show();

        Log($"[progress-cpu] 出帧活性(仅计数器驱动, 2 秒): 可见={normalFrames}帧 " +
            $"最小化={minimizedFrames}帧 恢复可见={normalAgainFrames}帧 隐藏={hiddenFrames}帧");
        // 以"可见时的帧数"作基准比较,而不是假设备显示器是某个刷新率:探针要判的是"是否停帧"。
        var reference = Math.Max(1, normalAgainFrames > 0 ? normalAgainFrames : normalFrames);
        var minimizedRatio = minimizedFrames / (double)reference;
        var hiddenRatio = hiddenFrames / (double)reference;
        Log($"[progress-cpu] 判定: 最小化={minimizedFrames}帧(可见时{reference}帧的 {minimizedRatio:P0}) " +
            $"⇒ {(minimizedRatio > 0.5 ? "仍在出帧:后台播放会持续走动" : "已停帧:后台不走")}; " +
            $"隐藏={hiddenFrames}帧({hiddenRatio:P0}) ⇒ {(hiddenRatio > 0.5 ? "仍在出帧" : "已停帧")}");

        await Task.Delay(300);
        window.Close();
        await Task.Delay(200);
        Log($"[progress-cpu] PASS: 循环代价(配对差值中位数)={deltaMedian:F2}% 单核," +
            $"目标 {ProgressRenderAnimator.TargetFramesPerSecond}FPS");
        return 0;

        // 填充条的当前 ScaleX:动画器每帧写入,是"循环是否还在推进"的直接读数。
        double FillScale() => fill.RenderTransform is ScaleTransform scale ? scale.ScaleX : double.NaN;
    }

    /// <summary>在窗口内挂一个自续订的 RequestAnimationFrame 计数器,返回采样窗口内的帧数。
    /// 这段刻意**不启用**进度动画器:要测的是平台在窗口不可见时还送不送帧,与动画器无关。
    ///
    /// ⚠️ 重挂下一帧必须**在回调里直接调用** RequestAnimationFrame(与 ProgressRenderAnimator 同款)。
    /// 第一版写成 `Dispatcher.UIThread.Post(() => RequestAnimationFrame(...))`,多出来的那趟
    /// dispatcher 往返破坏了帧合并,计数器退化成自旋 —— 实测 2 秒"3 727 916 帧",完全失真。</summary>
    private static async Task<int> CountFramesAsync(Window window, int milliseconds)
    {
        var frames = 0;
        var done = new TaskCompletionSource();
        var deadline = Environment.TickCount64 + milliseconds;

        void Tick(TimeSpan _)
        {
            frames++;
            if (Environment.TickCount64 >= deadline || TopLevel.GetTopLevel(window) is not { } topLevel)
            {
                done.TrySetResult();
                return;
            }

            topLevel.RequestAnimationFrame(Tick);
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(window) is { } topLevel)
                topLevel.RequestAnimationFrame(Tick);
            else
                done.TrySetResult();
        });

        await done.Task;
        return frames;
    }

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

    /// <summary>中位数(偶数个取中间两者平均):比平均值抗单轮抖动。</summary>
    private static double Median(List<double> samples)
    {
        if (samples.Count == 0) return 0;
        var sorted = new List<double>(samples);
        sorted.Sort();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { }
    }
}
