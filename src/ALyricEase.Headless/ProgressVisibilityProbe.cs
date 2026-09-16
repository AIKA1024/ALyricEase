using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>
/// 进度动画循环在窗口不可见时的行为与代价(--progress-visibility,真窗口)。
///
/// 要回答的两个问题:
///   1) 窗口最小化/隐藏时,ProgressRenderAnimator 那台"自行续订 RequestAnimationFrame"的循环
///      还在不在跑?(ShouldAnimate() 只看 已附加&amp;&amp;激活&amp;&amp;在播放&amp;&amp;有进度,**不看窗口是否可见**)
///   2) 如果在跑,代价是多少 —— 并且它是否随场景规模放大?
///      这决定"要不要给它补一个可见性判断"。所以场景做了轻/重两档对照:
///      轻 = 三控件(轨道/填充/小球),重 = 同样三个控件 + 800 个矩形 + 40 个文本块。
///
/// ⚠️ 与上一版(--progress-cpu-real)的关键差别:那版数帧时**关掉了动画器**,
/// 窗口内容完全静态、合成器没有任何失效可做,数到的 27 帧/2 秒在四种状态下完全相同 ——
/// 那个数字量的是"空转的平台",不能用来判定循环是否在跑。本版全程保持动画器开启,
/// 并用两条互相独立的证据判定活性:
///   1) 平台实际送达的帧数(自续订 RAF 计数器,与动画器同款写法);
///   2) 填充条 ScaleX 的推进量(动画器每帧自己写入;理论值 = 经过秒数 / 曲目时长)。
/// 只有两条都落回 0 才能说"后台不在跑"。
///
/// 口径提醒:本机静息 CPU 抖动可达 3% 单核量级,单轮 CPU 数字只作参考,
/// 判定以同一场景内"循环开/关的配对差值"为主 —— 场景不同不能直接比绝对值。
/// </summary>
internal static class ProgressVisibilityProbe
{
    /// <summary>单个测量窗口的时长(毫秒)。够长以压住偶发尖峰,又不至于让整轮跑太久。</summary>
    private const int SampleMs = 6000;

    /// <summary>曲目时长:180 秒,便于把 ScaleX 推进量直接换算成"是否按实时速率在走"。</summary>
    private const double DurationMs = 180_000;

    /// <summary>状态切换后的沉降时间:等窗口管理器的状态真正落地,再开始计时。</summary>
    private const int SettleMs = 900;

    /// <summary>重场景的规模:接近"一个真实页面"的视觉元素数量级。</summary>
    private const int HeavyRectangles = 800;
    private const int HeavyTexts = 40;

    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[progress-visibility] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };

        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[progress-visibility] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        var process = Process.GetCurrentProcess();
        Log($"[progress-visibility] 单变量=窗口可见性 + 场景规模;循环由动画器自行续订。样本 {SampleMs}ms/状态 " +
            $"目标帧率={ProgressRenderAnimator.TargetFramesPerSecond}FPS 逻辑核数={Environment.ProcessorCount}");

        var rows = new List<Row>();

        // ---- 第 1 段:轻场景(三个控件)----
        await RunSceneAsync("轻", heavy: false, rows, process);

        // ---- 第 2 段:重场景(三控件 + 800 矩形 + 40 文本)----
        await RunSceneAsync("重", heavy: true, rows, process);

        Log("[progress-visibility] ===== 结果 =====");
        foreach (var row in rows)
            Log($"[progress-visibility] {row.Label,-24} 实际={row.StateDescription,-18} " +
                $"帧数={row.Frames,4} ({row.Fps,6:F1}/s) ScaleX推进={row.Advance,8:F5} " +
                $"CPU={row.Cpu,6:F2}% ⇒ {(row.Animating ? "循环在跑" : "循环已停")}");

        var lightOn = Find(rows, "轻·可见+循环开");
        var lightOff = Find(rows, "轻·可见+循环关");
        var heavyOn = Find(rows, "重·可见+循环开");
        var heavyOff = Find(rows, "重·可见+循环关");
        var heavyMinOn = Find(rows, "重·最小化+循环开");
        var heavyMinOff = Find(rows, "重·最小化+循环关");
        var heavyHiddenOn = Find(rows, "重·隐藏+循环开");
        var heavyRestored = Find(rows, "重·恢复可见+循环开");

        if (lightOn is null || lightOff is null || heavyOn is null || heavyOff is null
            || heavyMinOn is null || heavyMinOff is null || heavyHiddenOn is null || heavyRestored is null)
        {
            Log("[progress-visibility] FAIL: 采样不完整");
            return 1;
        }

        var lightCost = lightOn.Cpu - lightOff.Cpu;
        var heavyCost = heavyOn.Cpu - heavyOff.Cpu;
        var heavyMinCost = heavyMinOn.Cpu - heavyMinOff.Cpu;

        Log($"[progress-visibility] 循环代价(同场景开/关配对差): 轻场景={lightCost:+0.00;-0.00}% " +
            $"重场景={heavyCost:+0.00;-0.00}% ⇒ 规模放大 {HeavyRectangles + HeavyTexts} 件视觉元素后 " +
            $"代价变化={(lightCost > 0.05 ? $"{heavyCost / lightCost:F1}×" : "无法比(轻场景基线过低)")}");
        Log($"[progress-visibility] 重场景最小化时: 循环开={heavyMinOn.Cpu:F2}% 循环关={heavyMinOff.Cpu:F2}% " +
            $"⇒ 最小化时循环代价={heavyMinCost:+0.00;-0.00}% (可见时={heavyCost:+0.00;-0.00}%)");
        Log($"[progress-visibility] 重场景隐藏时循环开={heavyHiddenOn.Cpu:F2}%, 可见时循环关={heavyOff.Cpu:F2}%");

        var visibleFps = Math.Max(0.1, heavyOn.Fps);
        var backgroundStops = heavyMinOn.Fps < visibleFps * 0.2 && heavyHiddenOn.Fps < visibleFps * 0.2;
        var restoredResumes = heavyRestored.Fps > visibleFps * 0.5 && heavyRestored.Animating;
        Log($"[progress-visibility] 判定: 窗口不可见时={(backgroundStops ? "平台停帧 ⇒ 循环停摆" : "平台仍在送帧 ⇒ 循环在跑,后台仍有每帧开销")}; " +
            $"恢复可见后={(restoredResumes ? "循环正常恢复" : "循环未能恢复(需查 _frameRequested 是否卡住)")}");

        if (!restoredResumes)
            return 1;
        Log("[progress-visibility] PASS: 可见性与规模两个自变量都已量清");
        return 0;
    }

    /// <summary>跑一个场景的完整相位序列:同一场景内先量"关"再量"开",差值即循环在该场景下的代价。</summary>
    private static async Task RunSceneAsync(string sceneName, bool heavy, List<Row> rows, Process process)
    {
        var (window, host, track, fill, thumb) = BuildWindow(heavy);
        window.Show();

        var animator = new ProgressRenderAnimator(host, track, fill, thumb);
        animator.Attach();
        var counter = new FrameCounter(window);

        // 预热:首次出帧的样式解析、Skia 管线、字体载入与场景首次光栅化都不该算进任何一种状态。
        animator.SetActive(true);
        animator.SetPlaybackState(0, DurationMs, isPlaying: true);
        await Task.Delay(4000);
        animator.SetActive(false);
        await Task.Delay(500);

        if (!heavy)
        {
            // 轻场景只取"可见"这一对,作为规模放大的基准。
            await MeasureAsync(rows, $"{sceneName}·可见+循环关", () => SetState(window, WindowState.Normal, true),
                loopOn: false, animator, counter, process, fill, window);
            await MeasureAsync(rows, $"{sceneName}·可见+循环开", () => SetState(window, WindowState.Normal, true),
                loopOn: true, animator, counter, process, fill, window);
        }
        else
        {
            await MeasureAsync(rows, $"{sceneName}·可见+循环开", () => SetState(window, WindowState.Normal, true),
                loopOn: true, animator, counter, process, fill, window);
            await MeasureAsync(rows, $"{sceneName}·可见+循环关", () => SetState(window, WindowState.Normal, true),
                loopOn: false, animator, counter, process, fill, window);
            await MeasureAsync(rows, $"{sceneName}·最小化+循环开", () => SetState(window, WindowState.Minimized, true),
                loopOn: true, animator, counter, process, fill, window);
            await MeasureAsync(rows, $"{sceneName}·最小化+循环关", () => SetState(window, WindowState.Minimized, true),
                loopOn: false, animator, counter, process, fill, window);
            await MeasureAsync(rows, $"{sceneName}·隐藏+循环开", () => SetState(window, WindowState.Normal, false),
                loopOn: true, animator, counter, process, fill, window);
            await MeasureAsync(rows, $"{sceneName}·恢复可见+循环开", () => SetState(window, WindowState.Normal, true),
                loopOn: true, animator, counter, process, fill, window);
        }

        window.Close();
        await Task.Delay(500);
    }

    private static async Task MeasureAsync(
        List<Row> rows,
        string label,
        Action applyState,
        bool loopOn,
        ProgressRenderAnimator animator,
        FrameCounter counter,
        Process process,
        Control fill,
        Window window)
    {
        applyState();
        await Task.Delay(SettleMs);

        // 状态必须在测量前就位,否则量到的还是上一个状态的尾巴。
        // 读回平台侧真实状态:要排除"我们以为切过去了、其实没切"这种假前提。
        var stateDescription = Describe(window);
        animator.SetActive(loopOn);
        animator.SetPlaybackState(0, DurationMs, isPlaying: true);
        await Task.Delay(300);

        var scaleStart = FillScale(fill);
        var wall = Stopwatch.StartNew();
        counter.Start();
        var cpu = await MeasureCpuAsync(process, SampleMs);
        wall.Stop();
        var frames = counter.Stop();
        var scaleEnd = FillScale(fill);

        var seconds = wall.Elapsed.TotalSeconds;
        var expected = seconds * 1000 / DurationMs;
        var advance = scaleEnd - scaleStart;

        rows.Add(new Row(
            label,
            stateDescription,
            cpu,
            frames,
            seconds > 0 ? frames / seconds : 0,
            advance,
            // 判定活性看推进量而不是帧数:帧数可能被别的失效顶起来(例如状态切换本身),
            // 而 ScaleX 只有动画器自己在写,它动了就是循环真的在跑。
            advance > expected * 0.5));
    }

    /// <summary>把窗口切到目标状态。隐藏用 Hide(),最小化用 WindowState,两者在平台层是不同路径。</summary>
    private static void SetState(Window window, WindowState state, bool visible)
    {
        if (!visible)
        {
            window.WindowState = WindowState.Normal;
            window.Hide();
            return;
        }

        if (!window.IsVisible)
            window.Show();
        window.WindowState = state;
    }

    /// <summary>读窗口平台侧的真实状态 —— 用来确认"最小化/隐藏"这类切换真的落地了,
    /// 而不是我们以为落地了(那会把"根本没切过去"误读成"切过去了却不费电")。</summary>
    private static string Describe(Window window)
        => $"state={window.WindowState},visible={window.IsVisible}";

    private static Row? Find(List<Row> rows, string label)
    {
        foreach (var row in rows)
            if (row.Label == label)
                return row;
        return null;
    }

    private static (Window Window, Grid Host, Border Track, Border Fill, Border Thumb) BuildWindow(bool heavy)
    {
        // 与 PlayerProgressBar 同结构:轨道 + 已播放段(ScaleX 拉伸) + 球。
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

        var root = new Grid();
        if (heavy)
        {
            // 用绝对定位铺满窗口:数量接近一个真实页面(列表行 + 文案)的视觉元素规模,
            // 逐帧同步一次整场景。
            root.Children.Add(BuildHeavyLayer());
            host.VerticalAlignment = VerticalAlignment.Bottom;
            host.Margin = new Thickness(100, 0, 100, 60);
        }
        else
        {
            host.VerticalAlignment = VerticalAlignment.Center;
        }

        root.Children.Add(host);

        var window = new Window
        {
            Width = 1200,
            Height = 800,
            Title = $"ALyricEase 进度循环可见性探针({(heavy ? "重" : "轻")}场景)",
            ShowActivated = false,
            Content = root,
        };
        return (window, host, track, fill, thumb);
    }

    private static Control BuildHeavyLayer()
    {
        var layer = new Grid();
        var palette = new IBrush[]
        {
            new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x33)),
            new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x3F)),
            new SolidColorBrush(Color.FromRgb(0x5E, 0xA9, 0xA2)),
            new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xE0)),
        };

        for (var index = 0; index < HeavyRectangles; index++)
        {
            layer.Children.Add(new Rectangle
            {
                Width = 90 + index % 7 * 22,
                Height = 18,
                Fill = palette[index % palette.Length],
                Opacity = 0.25 + index % 5 * 0.15,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(index % 9 * 128, index / 9 * 22, 0, 0),
            });
        }

        for (var index = 0; index < HeavyTexts; index++)
        {
            layer.Children.Add(new TextBlock
            {
                Text = $"可见性探针文本行 {index:D2}",
                FontSize = 13,
                Foreground = palette[3],
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(900, index * 19, 0, 0),
            });
        }

        return layer;
    }

    private static double FillScale(Control fill)
        => fill.RenderTransform is ScaleTransform scale ? scale.ScaleX : double.NaN;

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
        string StateDescription,
        double Cpu,
        int Frames,
        double Fps,
        double Advance,
        bool Animating);

    /// <summary>自续订的 RequestAnimationFrame 计数器:量平台在一段时间内实际送达多少帧。
    ///
    /// ⚠️ 下一帧必须**在回调里直接请求**(与 ProgressRenderAnimator 同款写法)。
    /// 上一版绕了一趟 Dispatcher.UIThread.Post,多出来的 dispatcher 往返破坏了帧合并,
    /// 计数器退化成自旋 —— 实测 2 秒"3 727 916 帧",完全失真。</summary>
    private sealed class FrameCounter
    {
        private readonly Window _window;
        private int _generation;
        private int _frames;
        private bool _running;

        public FrameCounter(Window window) => _window = window;

        public void Start()
        {
            _generation++;
            _frames = 0;
            _running = true;
            Request(_generation);
        }

        /// <summary>停止计数并返回本次窗口内送达的帧数。停止后遗留的回调不再续订,也不计数。</summary>
        public int Stop()
        {
            _running = false;
            _generation++;
            return _frames;
        }

        private void Request(int generation)
        {
            if (!_running || generation != _generation) return;
            if (TopLevel.GetTopLevel(_window) is not { } topLevel) return;
            topLevel.RequestAnimationFrame(_ => Tick(generation, topLevel));
        }

        private void Tick(int generation, TopLevel topLevel)
        {
            if (!_running || generation != _generation) return;
            _frames++;
            topLevel.RequestAnimationFrame(_ => Tick(generation, topLevel));
        }
    }
}
