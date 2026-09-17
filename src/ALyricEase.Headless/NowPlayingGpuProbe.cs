using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// 播放详情页的 GPU 归因探针(--np-gpu-real)。
///
/// 为什么单开一个探针:现有 CPU 探针(--shell-cpu-real / --pl-cpu-real)量的是
/// "占了多少 CPU 时间",而详情页的代价主要落在**合成与光栅化**上 ——
/// 它可能几乎不吃 CPU(帧全交给合成线程与驱动),CPU 读数挺好看,
/// 显卡却在每帧重新混合整屏。用户看到的"GPU 6%"只能用 GPU 计数器量。
///
/// 夹具与 --shell-cpu-real 同源:真实 TestMainWindow(与 MainWindow.axaml 逐字节一致)
/// + 真实 AppShell + 真实 NowPlayingView,由真实 PlayerViewModel 驱动;
/// 网络不可用,所以封面注入一张合成位图、歌词直接灌进 LyricViewModel。
///
/// 方法一:同轮消融矩阵。每个场景都是一份**完整**状态(不是"在上一个场景上再关一样"),
/// 所以行与行可以任意比较,不受执行顺序影响;同轮而非跨构建,差里只剩被消融的那一项:
///   ① 封面色团背景层(10 个全屏径向渐变椭圆,永远在动)
///   ② 它的**漂移动画**本身(与①分开:一个量"画的东西",一个量"还在动";
///      这一档由用户设置「动态背景」开关走真实链路产生,不由探针强制停)
///   ③ 进度自续订循环(底部条 + 详情页各一个)
///   ④ 歌词逐行的模糊
///   ⑤ 被完全盖住却仍留在视觉树里的底层页面
/// 另插一行"默认(复测)"用于识别热漂移与后台干扰。
///
/// 方法二:抓屏像素验证。消融只能证明"关掉它 GPU 降了",**不能**证明"它画出来了"。
/// 若某个 Effect 根本没渲染,那它就是纯付费零收益,结论完全不同 —— 所以最后
/// 会在全静止状态下抓两帧(噪声底)、关掉模糊再抓一帧、恢复后再抓一帧,用像素说话。
/// 判据:模糊差 ≈ 噪声底 ⇒ 没画出来。
///
/// ⚠ 需要在**可见且没被遮挡**的窗口上跑(GPU 计数只在真的提交到屏幕时才有值),
/// 窗口会置顶约两分钟盖住桌面 —— 量完立刻关。
/// </summary>
internal static class NowPlayingGpuProbe
{
    /// <summary>单个场景的采样窗口(毫秒)。够长以跨过 1 秒级的调度抖动。</summary>
    private const int SampleMs = 5000;

    /// <summary>状态切换后的沉降时间(等 0.45s 的覆盖层滑入动画落地、布局稳定)。</summary>
    private const int SettleMs = 800;

    /// <summary>GPU 计数器轮询间隔。GPU Engine 是速率型计数器,过密只会重复读到同一段增量。</summary>
    private const int GpuIntervalMs = 250;

    private const double DurationMs = 180_000;
    private const int PositionReportMs = 200;

    /// <summary>色团层里 10 个椭圆的元件名(A 层 5 个 + B 层 5 个,CrossFade 用)。</summary>
    private static readonly string[] s_ellipseNames =
        ["A0", "A1", "A2", "A3", "A4", "B0", "B1", "B2", "B3", "B4"];

    private static DispatcherTimer? _positionTimer;
    private static long _virtualPositionMs;
    private static HeadlessApp.StubAudioPlayer? _stub;

    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[np-gpu] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };

        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[np-gpu] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        var process = Process.GetCurrentProcess();
        var main = ServiceLocator.Get<MainViewModel>();

        if (GpuUsageSampler.TryCreate(process.Id, out var diagnosis) is not { } gpu)
        {
            Log($"[np-gpu] FAIL: 量不到 GPU 计数器({diagnosis});本探针的全部结论都依赖它,不做退化");
            return 1;
        }

        using (gpu)
        {
            Log($"[np-gpu] GPU 计数器就绪: pid={process.Id} 实例结构体步长={GpuUsageSampler.ItemStride} 字节(应为 24)");

            // 窗口尺寸:默认 1200x720(与 TestMainWindow 一致);ALY_NP_SIZE=WxH 可对齐
            // 用户的实际窗口 —— 填充率与面积成正比,拿小窗量出的占比去对用户的大窗会低估。
            var (width, height) = ParseSize(Env("ALY_NP_SIZE") ?? "1200x720");
            var window = new TestMainWindow
            {
                DataContext = main,
                ShowActivated = false,
                Width = width,
                Height = height,
                // 必须真的在屏幕最上层 —— 被遮挡时 DWM 不合成,我们的帧没提交,GPU 恒为 0,
                // 那种"数字很漂亮"的错误比崩溃更难查。
                Topmost = true,
            };
            window.Show();
            await DrainAsync(1200);

            LogScreens(window);
            if (int.TryParse(Env("ALY_NP_SCREEN"), out var screenIndex)) PlaceOnScreen(window, screenIndex, width, height);

            Log($"[np-gpu] 窗口: 请求 {width:F0}x{height:F0} 实际 {window.ClientSize.Width:F0}x{window.ClientSize.Height:F0} " +
                $"scaling={window.RenderScaling:F2} 外框={window.FrameSize}");

            var refs = new Refs(window, main);
            if (!refs.IsComplete)
            {
                Log($"[np-gpu] FAIL: 夹具不完整 {refs.Describe()}");
                window.Close();
                return 1;
            }

            SeedCover();
            StartPlayback();
            Log($"装配: 详情页={Describe(refs.NowPlaying)} 封面背景={Describe(refs.Backdrop)} " +
                $"底部动画器={Describe(refs.BarAnimator)} 详情页动画器={Describe(refs.NowPlayingAnimator)}");

            await WarmUpAsync(refs);
            if (refs.BarAnimator is null)
                Log("[np-gpu] ⚠ 底部进度动画器仍未找到 —— 「停进度循环」那一列只关掉了详情页那个,判读时按半个变量算");

            var rows = new List<Row>();
            foreach (var scenario in BuildScenarios())
            {
                var row = await MeasureAsync(refs, process, gpu, scenario);
                rows.Add(row);
                Log($"[np-gpu] {row.Label,-24} GPU={row.GpuMean,6:F2}%(峰 {row.GpuPeak,5:F2}% 单引擎峰 {row.EnginePeak,5:F2}% 引擎 {row.Engines,2}) " +
                    $"CPU={row.Cpu,6:F2}% 歌词行={row.LyricItems,3}(带模糊 {row.BlurredItems,3}) " +
                    $"帧节奏={DescribeCadence(row.FrameIntervalMs)}");


            }

            await BlurPixelTestAsync(refs);

            StopPositionReporting();
            refs.Main.AppState.DynamicBackground = true;
            refs.Main.AppState.NotifyVisualEffectsChanged();
            window.Close();
            await DrainAsync(400);

            Report(rows, refs);
            Log("[np-gpu] PASS: 播放详情页 GPU 归因完成");
            return 0;
        }
    }

    // ────────────────────────────── 场景矩阵 ──────────────────────────────

    /// <summary>歌词行模糊的三种口径。远近歌词在视觉上差得很多，能不能"只模糊近处"要靠量。</summary>
    private enum LyricBlurMode
    {
        /// <summary>与样式一致:除当前句外**所有**实化行都带模糊(远景 5px,近景 1~2.5px)。</summary>
        All,

        /// <summary>只保留当前句上下各两行(样式里的 above/below1/below2),更远的行只靠透明度弱化。</summary>
        Nearest,

        /// <summary>全部关掉(消融用)。</summary>
        None,
    }

    /// <summary>
    /// 色团漂移的两态。
    ///
    /// <para>
    /// 关键在于 <see cref="Default"/>:探针**一行代码都不碰动画**,完全由应用自己的
    /// 绑定门控说了算 —— 于是同一张表里既有"修复前的行为"(<see cref="ForceOn"/> 强制开),
    /// 也有"修复后的行为"(默认态),两者同轮可比。
    /// </para>
    ///
    /// <para>
    /// ⚠ 曾经有个 <c>ForceOff</c>(绕过控件直接 StopAnimation),**已删除**。它带来两个
    /// 互相纠缠的坑,都实测踩过:① 它不走控件自己的 <c>StopMotion</c>,于是
    /// <c>_motionStarted</c> 仍为 true 而动画已停,状态机错位 ⇒ 后面所有 Default 行
    /// 即使属性为真也永远起不来(实测整张表从那一行起全掉到 0.7~1.0%,本该 8.5%,
    /// 而消融行自己看着完全正常)。② 补一个"测完复位"之后又反过来 —— 复位走的是控件
    /// 延后启动路径,与下一行的 StopAnimation 落在同一帧,那一行自己又停不住。
    /// 根因是 <c>StopAnimation</c> 只有在**控件自己的 StopMotion 里**才会 bump
    /// <c>_motionGeneration</c>,而那个代次正是用来作废在途启动的。
    /// 它想回答的"停掉漂移能省多少"现在由「详情页·关动态背景设置」走真实设置链路给出,
    /// 那是更硬的证据(量的就是用户真按开关会发生什么)。
    /// </para>
    /// </summary>
    private enum MotionMode
    {
        /// <summary>
        /// 照应用自己的门控走(验收口径)。**探针一行代码都不碰动画** —— 这是关键:
        /// 若这里顺手把状态"推"成和控件属性一致,就会在同一帧内先 StartAnimation 再被
        /// 应用的 StopAnimation 顶掉,而实测这种时序**停不掉**(读数与应用无关)。
        /// 只有完全袖手旁观,"应用的门控到底管不管用"才会如实反映到 GPU 上。
        /// </summary>
        Default,

        /// <summary>强制开 —— 等价于修复前"永远在漂移"的行为(对照口径)。</summary>
        ForceOn,

    }

    /// <summary>一份完整状态。字段之间没有顺序依赖,行与行可直接相减。</summary>
    private sealed record Scenario(
        string Label,
        bool NowPlaying,
        bool LyricsPanel,
        bool BackdropVisible,
        MotionMode BackdropMotion,
        bool ProgressLoop,
        LyricBlurMode LyricBlur,
        bool DynamicBackground = true);

    private static IEnumerable<Scenario> BuildScenarios()
    {
        // 验收:详情页关闭时,应用自己的门控必须已经把漂移停掉。
        yield return new Scenario("首页·对照", false, false, true, MotionMode.Default, true, LyricBlurMode.All, true);
        // 对照:强行把漂移打开(修复前的行为)。与上一行的差就是这次修复的收益。
        yield return new Scenario("首页·强制开色团动画", false, false, true, MotionMode.ForceOn, true, LyricBlurMode.All, true);

        // 详情页默认态:歌词面板关(用户没点歌词),这是打开详情页最常见的样子。
        yield return new Scenario("详情页·默认", true, false, true, MotionMode.Default, true, LyricBlurMode.All, true);
        // 复测紧贴基线:它的唯一用途是给"相对默认行的差值"定噪声底,离远了就失去意义;
        // 放在整轮最后还会撞上尾部的不稳定(实测末行读到 4.23%,该 8.40%,CPU 也只剩六成)。
        yield return new Scenario("详情页·默认(复测)", true, false, true, MotionMode.Default, true, LyricBlurMode.All, true);

        // 走真实设置链路:把"启用播放详情界面的动态背景效果"关掉(经 AppState 通知 → 主 VM 属性 → 绑定)。
        // 这条验证的是"开关真的接上了",而不是"探针手动停了动画";它同时就是"漂移不在动"
        // 那一档的读数,所以不必再有单独的强制停行。
        yield return new Scenario("详情页·关动态背景设置", true, false, true, MotionMode.Default, true, LyricBlurMode.All, false);

        // 逐项消融:每行只变一个自变量,与"默认"的差就是那一项的净代价。
        yield return new Scenario("详情页·减色团", true, false, false, MotionMode.Default, true, LyricBlurMode.All, true);
        yield return new Scenario("详情页·停进度循环", true, false, true, MotionMode.Default, false, LyricBlurMode.All, true);
        // 全关的下界:两条都是**应用自己的路径**(设置开关 + 循环开关),不碰动画本身。
        yield return new Scenario("详情页·关设置+停循环", true, false, true, MotionMode.Default, false, LyricBlurMode.All, false);

        // 歌词面板:用户点开歌词后的样子(逐行带模糊的那套样式在这里才生效)。
        yield return new Scenario("歌词·默认", true, true, true, MotionMode.Default, true, LyricBlurMode.All, true);
        yield return new Scenario("歌词·只模糊近处", true, true, true, MotionMode.Default, true, LyricBlurMode.Nearest, true);
        yield return new Scenario("歌词·减模糊", true, true, true, MotionMode.Default, true, LyricBlurMode.None, true);
        yield return new Scenario("歌词·减模糊+减色团", true, true, false, MotionMode.Default, true, LyricBlurMode.None, true);
    }

    private static async Task<Row> MeasureAsync(Refs refs, Process process, GpuUsageSampler gpu, Scenario scenario)
    {
        await ApplyAsync(refs, scenario);

        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var gpuSum = 0.0;
        var gpuPeak = 0.0;
        var enginePeak = 0.0;
        var samples = 0;
        var engines = 0;

        while (wall.ElapsedMilliseconds < SampleMs)
        {
            if (gpu.TrySample(out var sample))
            {
                gpuSum += sample.Sum;
                if (sample.Sum > gpuPeak) gpuPeak = sample.Sum;
                // 单引擎峰值:任务管理器"进程 GPU"列更接近"最忙的那个引擎",
                // 而加总口径是"占了多少引擎时间片"。两个都给,免得结论只在一种口径下成立。
                if (sample.Max > enginePeak) enginePeak = sample.Max;
                engines = Math.Max(engines, sample.Engines);
                samples++;
            }
            await Task.Delay(GpuIntervalMs);
        }

        var seconds = wall.Elapsed.TotalSeconds;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds / seconds * 100;

        // 帧节奏放在 GPU 采样**之后**量:请求帧这件事本身会驱动平台出帧,
        // 混进采样窗口就会把"我催出来的帧"算成"应用自己跑的帧"。
        var frameInterval = await MeasureFrameIntervalAsync(refs.Window, 900);

        return new Row(
            scenario.Label,
            samples > 0 ? gpuSum / samples : double.NaN,
            gpuPeak,
            enginePeak,
            engines,
            cpu,
            refs.LyricItemCount,
            refs.BlurredItemCount,
            frameInterval);
    }

    private static async Task ApplyAsync(Refs refs, Scenario scenario)
    {
        var main = refs.Main;

        // 覆盖层要先滑进来/滑出去再谈其他:0.45s 的位移过渡期间渲染的东西完全不同。
        if (main.ShowNowPlaying != scenario.NowPlaying)
        {
            if (scenario.NowPlaying) main.OpenNowPlayingCommand.Execute(null);
            else main.CloseNowPlayingCommand.Execute(null);
            await Task.Delay(700);
        }

        main.NowPlayingPanel = scenario.LyricsPanel ? NowPlayingPanel.Lyrics : NowPlayingPanel.None;
        refs.Shell.IsVisible = true;
        if (refs.Backdrop is not null) refs.Backdrop.IsVisible = scenario.BackdropVisible;
        // MotionMode.Default 是"照应用自己的门控走" —— 探针**完全不动手**。
        // 曾经在这里读回控件的 MotionEnabled 再强推一遍状态,那样会造出
        // "探针 StartAnimation 与应用 StopAnimation 挤在同一帧"的时序,而这种时序
        // 实测停不掉(读数与应用无关)。要验收应用的门控,只能袖手旁观。
        if (scenario.BackdropMotion == MotionMode.ForceOn) refs.SetBackdropMotion(true);

        // 两个进度动画器一起开关:详情页盖住底部条时,底部条那个循环是纯粹的浪费,
        // 只关一个等于没关(它会继续以屏幕刷新率驱动整窗重绘)。
        refs.BarAnimator?.SetActive(scenario.ProgressLoop);
        refs.NowPlayingAnimator?.SetActive(scenario.ProgressLoop);

        // 走 AppState + 通知事件这条真实链路,而**不是**直接改控件属性:
        // 要验的正是"设置改了 → 主 VM 属性变了 → 绑定把 MotionEnabled 更新下去"这一串接对了没有。
        // 故意不经过 SettingsViewModel 的 setter,免得探针把用户的设置文件反复改写。
        refs.Main.AppState.DynamicBackground = scenario.DynamicBackground;
        refs.Main.AppState.NotifyVisualEffectsChanged();

        refs.SetLyricBlur(scenario.LyricBlur);
        await Task.Delay(SettleMs);

        // 门控链路自证:设置值 → 主 VM 计算属性 → 绑定 → 控件属性,中间任何一环断了
        // 都会表现成"设置没用",而只看 GPU 读数分不清是哪一环。
        // 门控链路自证:设置值 → 主 VM 计算属性 → 绑定 → 控件属性,中间任何一环断了
        // 都会表现成"设置没用",而只看 GPU 读数分不清是哪一环。
        // (注:曾在这里挂过"全窗像素抖动"当"还在不在动"的直接证据,实测**不可用** ——
        //  漂移按设计就慢到每帧亚像素,带阈值的像素差恒为 0.00%,连强制开漂移也是 0.00%。
        //  原因见 AlbumCoverBackground 注释:它的代价是"每帧重画整窗",不是"每帧画面都在明显变"。)
        Log($"[np-gpu]   · {scenario.Label}: 设置动态背景={scenario.DynamicBackground} " +
            $"VM属性={refs.Main.NowPlayingMotionEnabled} 控件MotionEnabled={refs.Backdrop?.MotionEnabled} " +
            $"模式={scenario.BackdropMotion}");
    }

    // ────────────────────────────── 像素验证 ──────────────────────────────

    /// <summary>
    /// 用屏幕像素回答"那条歌词模糊到底画出来了没有"。
    ///
    /// 消融只能说明"关掉它省了 X%",说明不了"它有没有生效"。若它根本没渲染,
    /// 那就是纯付费零收益,该直接删;若它渲染了,才谈得上"换个便宜的做法"。
    ///
    /// 设计:全静止(色团不动、循环全停)后连抓四帧 ——
    ///   ① ② 都是"带模糊" ⇒ 两帧之差就是噪声底(抗锯齿、亚像素取整、DWM 抖动)
    ///   ③   "不带模糊"     ⇒ 与①之差若 ≈ 噪声底,说明模糊没画出来
    ///   ④   "恢复模糊"     ⇒ 与①之差同样应为噪声底,否则说明这条消融是不可逆的
    /// </summary>
    private static async Task BlurPixelTestAsync(Refs refs)
    {
        Log("[np-gpu] ===== 抓屏验证:歌词行的 BlurEffect 是否真的渲染 =====");

        refs.Main.NowPlayingPanel = NowPlayingPanel.Lyrics;
        refs.SetBackdropMotion(false);
        refs.BarAnimator?.SetActive(false);
        refs.NowPlayingAnimator?.SetActive(false);
        await Task.Delay(1500);
        // 行引用由 SetLyricBlur 自己按当前视觉树刷新,这里不需要额外重抓
        // (重抓反而会把上一轮被置成 null 的 Effect 当成"原件",更糟)。
        refs.SetLyricBlur(LyricBlurMode.All);
        await Task.Delay(600);

        var region = LyricRegion(refs);
        var blurred1 = ScreenCapture.GrabClient(refs.Window);
        await Task.Delay(350);
        var blurred2 = ScreenCapture.GrabClient(refs.Window);

        refs.SetLyricBlur(LyricBlurMode.None);
        await Task.Delay(600);
        var cleared = ScreenCapture.GrabClient(refs.Window);

        refs.SetLyricBlur(LyricBlurMode.All);
        await Task.Delay(600);
        var restored = ScreenCapture.GrabClient(refs.Window);

        if (blurred1.IsEmpty || region is null)
        {
            Log("[np-gpu] 抓屏失败或定位不到歌词区,跳过像素验证");
            return;
        }

        var noise = ScreenCapture.DiffRatio(blurred1, blurred2, region);
        var effect = ScreenCapture.DiffRatio(blurred1, cleared, region);
        var restore = ScreenCapture.DiffRatio(blurred1, restored, region);
        Log($"[np-gpu] 歌词区 {region.Value.Width}x{region.Value.Height} 设备像素, " +
            $"带模糊行 {refs.BlurredItemCount} 条 / 共 {refs.LyricItemCount} 条");
        Log($"[np-gpu]   噪声底(带模糊 vs 带模糊)     = {Percent(noise)}");
        Log($"[np-gpu]   消融差(带模糊 vs 不带模糊) = {Percent(effect)}");
        Log($"[np-gpu]   可逆性(带模糊 vs 已恢复)   = {Percent(restore)}");

        // 判据:模糊若真的画出来,关掉它至少要让文字边缘成片变化,量级必然远高于
        // 同一状态两帧之间的噪声底。取两者中较大者的两倍作阈值,免得把噪声读成效果。
        var threshold = Math.Max(noise, 0.002) * 2;
        // 恰好 0.00% 单独一档:那更像"消融没作用到画面上"(夹具的行引用失效)而不是
        // "Effect 不起作用"。这两件事的结论天差地别,不能让前者冒充后者的证据。
        var verdict = double.IsNaN(effect)
            ? "无法比较(抓屏失败)"
            : effect == 0
                ? "本轮关掉模糊后画面零变化 —— 先怀疑夹具(歌词行是否已被重建),不要据此断言 Effect 无效"
                : effect < threshold
                    ? "模糊没有渲染 ⇒ 这些 GPU 是纯白付,删掉零视觉损失"
                    : "模糊确实渲染了 ⇒ 该谈的是换个便宜画法,而不是能不能删";
        Log($"[np-gpu] 判定: {verdict}(阈值 {threshold * 100:F2}%)");

        var saved = ScreenCapture.Save(blurred1, "np-gpu-blur-on");
        var savedOff = ScreenCapture.Save(cleared, "np-gpu-blur-off");
        if (saved.Length > 0) Log($"[np-gpu] 两张对照图: {saved} / {savedOff}");
    }

    private static PixelRect? LyricRegion(Refs refs)
    {
        var view = refs.Lyric;
        if (view is null) return null;
        var origin = view.TranslatePoint(new Point(0, 0), refs.Window);
        if (origin is null) return null;
        return ScreenCapture.ToDeviceRect(
            refs.Window, origin.Value.X, origin.Value.Y, view.Bounds.Width, view.Bounds.Height);
    }

    private static string Percent(double ratio) =>
        double.IsNaN(ratio) ? "无法比较" : $"{ratio * 100:F2}% 像素有差异";

    // ────────────────────────────── 夹具 ──────────────────────────────

    /// <summary>探针里所有"抓一次、反复用"的引用。</summary>
    private sealed class Refs
    {
        private readonly Dictionary<ListBoxItem, IEffect?> _lyricEffects = [];

        public Refs(Window window, MainViewModel main)
        {
            Window = window;
            Main = main;
            Player = ServiceLocator.Get<PlayerViewModel>();
            Shell = window.GetVisualDescendants().OfType<AppShell>().FirstOrDefault();
            NowPlaying = window.GetVisualDescendants().OfType<NowPlayingView>().FirstOrDefault();
            Backdrop = window.GetVisualDescendants().OfType<AlbumCoverBackground>().FirstOrDefault();
            Lyric = NowPlaying?.GetVisualDescendants().OfType<LyricView>().FirstOrDefault();
            // 注意:自续订循环挂在 PlayerProgressBar 上,不是 PlayerBarView ——
            // 找错类型会静默拿到 null,于是"关循环"那一列其实没关,整张表都白量。
            BarAnimator = FindAnimator(window.GetVisualDescendants().OfType<PlayerProgressBar>().FirstOrDefault());
            NowPlayingAnimator = FindAnimator(NowPlaying);
        }

        public Window Window { get; }
        public MainViewModel Main { get; }
        public PlayerViewModel Player { get; }
        public AppShell? Shell { get; }
        public NowPlayingView? NowPlaying { get; }
        public AlbumCoverBackground? Backdrop { get; }
        public LyricView? Lyric { get; }
        public ProgressRenderAnimator? BarAnimator { get; }
        public ProgressRenderAnimator? NowPlayingAnimator { get; }

        private ListBoxItem[] _lyricItems = [];
        private int _blurredItems;
        private int _backdropEllipses;

        public bool IsComplete => Shell is not null && NowPlaying is not null && Backdrop is not null;

        public string Describe() =>
            $"shell={(Shell is null ? "缺" : "有")} 详情页={(NowPlaying is null ? "缺" : "有")} " +
            $"封面背景={(Backdrop is null ? "缺" : "有")} 歌词视图={(Lyric is null ? "缺" : "有")}";

        public int LyricItemCount => _lyricItems.Length;

        public int BlurredItemCount => _blurredItems;

        public int BackdropEllipses => _backdropEllipses;

        /// <summary>
        /// 当前**实化出来的**歌词行。每次都重新取,不做快照 ——
        /// 歌词面板收起再展开会重建 ListBoxItem,沿用过一次的数组会指向已脱离视觉树的
        /// 旧实例:Effect 改了、画面纹丝不动(实测踩到过,同一套判据前四轮 4.58%、
        /// 有一轮 0.00%,还据此打印出"模糊没渲染"的错误结论)。
        /// </summary>
        private ListBoxItem[] RealizedLyricItems() => NowPlaying is null
            ? []
            : NowPlaying.GetVisualDescendants().OfType<ListBoxItem>().ToArray();

        /// <summary>
        /// 记录行数、每行"原本的模糊"与色团椭圆数。
        /// 模糊的开关是**双向**的 —— 存下样式给出的那个 Effect 实例,恢复时原样写回;
        /// 只靠 ClearValue 会被 Avalonia 的样式应用时机坑到(实测清掉后不再被样式补回)。
        /// </summary>
        public void CaptureVisualStats()
        {
            _lyricItems = RealizedLyricItems();
            CaptureOriginals(_lyricItems);
            _blurredItems = _lyricItems.Count(item => item.Effect is not null);

            _backdropEllipses = Backdrop is null
                ? 0
                : s_ellipseNames.Count(name => Backdrop.FindControl<Ellipse>(name) is not null);
        }

        /// <summary>
        /// 按"首次见面"补齐原件字典。样式里每条
        /// <c>&lt;Setter Property="Effect"&gt;&lt;BlurEffect Radius="…"/&gt;&lt;/Setter&gt;</c>
        /// 会给**每一行各造一个** BlurEffect 实例(远景 5 / above 1.5 / below1 1 / below2 2.5),
        /// 所以原件必须逐行取、不能共用。新行此刻还带着样式给的 Effect,正好当原件;
        /// 已经存过的行保留旧值 —— 我们把它置过 null,再读就只剩 null 了。
        /// </summary>
        private void CaptureOriginals(ListBoxItem[] items)
        {
            foreach (var item in items)
                if (!_lyricEffects.ContainsKey(item))
                    _lyricEffects[item] = item.Effect;
        }

        /// <summary>开关歌词行的模糊。写本地值是**探针夹具**故意破例的地方:
        /// 要消融的正是样式里那几条 Setter,没有别的口子能把它关掉。</summary>
        public void SetLyricBlur(LyricBlurMode mode)
        {
            _lyricItems = RealizedLyricItems();
            CaptureOriginals(_lyricItems);

            foreach (var item in _lyricItems)
            {
                var keep = mode switch
                {
                    LyricBlurMode.All => true,
                    // 样式里 above/below1/below2 就是"当前句上下各两行";更远的 Default 态
                    // 与它们只差在透明度(0.4 vs 0.6~0.8),模糊本来就是最弱的一档。
                    LyricBlurMode.Nearest => item.Classes.Contains("above")
                                             || item.Classes.Contains("below1")
                                             || item.Classes.Contains("below2"),
                    _ => false,
                };

                if (keep)
                {
                    if (_lyricEffects.TryGetValue(item, out var original)) item.Effect = original;
                }
                else
                {
                    item.Effect = null;
                }
            }

            _blurredItems = _lyricItems.Count(item => item.Effect is not null);
        }

        /// <summary>
        /// 开关色团层的漂移动画。
        /// 停:直接对每个椭圆的合成视觉 StopAnimation(值停在原处,不改位置,免得改变覆盖面积)。
        /// 起:控件自己的启动路径是**一次性**的(私有 _motionStarted),所以要复位那个标记
        ///     再调它自己的 TryStartMotion —— 复刻启动逻辑(缓动/关键帧)是下策,
        ///     那样量的就不是真跑的那套动画了。
        /// </summary>
        public void SetBackdropMotion(bool running)
        {
            if (Backdrop is null) return;

            if (running)
            {
                var type = typeof(AlbumCoverBackground);
                type.GetField("_motionStarted", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(Backdrop, false);
                type.GetMethod("TryStartMotion", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(Backdrop, null);
                return;
            }

            foreach (var name in s_ellipseNames)
            {
                var ellipse = Backdrop.FindControl<Ellipse>(name);
                var visual = ellipse is null ? null : ElementComposition.GetElementVisual(ellipse);
                visual?.StopAnimation("Translation");
                visual?.StopAnimation("Scale");
            }
        }
    }

    private static ProgressRenderAnimator? FindAnimator(object? owner)
    {
        if (owner is null) return null;
        var field = owner.GetType().GetField("_progressVisual", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(owner) as ProgressRenderAnimator;
    }

    private static void SeedCover()
    {
        // 真实网络不可用,用一张合成封面:既让详情页的大图真的画出来,
        // 也让 AlbumCoverBackground 的取色拿到可区分的代表色(纯色会退化成默认 palette)。
        var canvas = new Canvas { Width = 600, Height = 600 };
        canvas.Children.Add(new Rectangle
        {
            Width = 600, Height = 600,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(24, 44, 96), 0),
                    new GradientStop(Color.FromRgb(122, 40, 108), 0.45),
                    new GradientStop(Color.FromRgb(28, 122, 118), 1),
                },
            },
        });
        var gold = new Ellipse { Width = 220, Height = 220, Fill = new SolidColorBrush(Color.FromRgb(214, 168, 62)) };
        Canvas.SetLeft(gold, 60);
        Canvas.SetTop(gold, 340);
        canvas.Children.Add(gold);

        var red = new Ellipse { Width = 160, Height = 160, Fill = new SolidColorBrush(Color.FromRgb(196, 82, 74)) };
        Canvas.SetLeft(red, 380);
        Canvas.SetTop(red, 90);
        canvas.Children.Add(red);

        var bitmap = new RenderTargetBitmap(new PixelSize(600, 600), new Vector(96, 96));
        bitmap.Render(canvas);
        ServiceLocator.Get<PlayerViewModel>().Cover = bitmap;
    }

    private static void StartPlayback()
    {
        var player = ServiceLocator.Get<PlayerViewModel>();
        _stub = ServiceLocator.Get<IAudioPlayer>() as HeadlessApp.StubAudioPlayer;

        player.CurrentSong = new Song
        {
            Id = 860_001,
            Source = (MusicSource)99,
            Name = "播放详情页 GPU 归因探针",
            Artist = "探针歌手",
            Album = "探针专辑",
            DurationMs = (int)DurationMs,
        };
        player.DurationMs = (long)DurationMs;
        player.ScrubPositionMs = 42_000;

        // 歌词直接灌进 VM:真实链路要联网,而这里只需要"有一屏正常长度的歌词"。
        var lines = new List<LyricLine>();
        for (var index = 0; index < 48; index++)
        {
            lines.Add(new LyricLine(TimeSpan.FromSeconds(index * 4), $"第 {index + 1:00} 行歌词文本,用来撑出真实的一屏")
            {
                Translation = index % 3 == 0 ? $"translation line {index + 1:00}" : null,
            });
        }

        var lyric = ServiceLocator.Get<LyricViewModel>();
        lyric.Lines = new ObservableCollection<LyricLine>(lines);
        lyric.HasLyric = true;
        lyric.CurrentIndex = 18;

        if (_stub is not null) _stub.SetState(PlaybackState.Playing);
        else
        {
            Log("[np-gpu] 警告: 播放器桩不可驱动,改为直接置位");
            player.IsPlaying = true;
        }

        _virtualPositionMs = 42_000;
        _positionTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(PositionReportMs),
            DispatcherPriority.Normal,
            (_, _) =>
            {
                _virtualPositionMs += PositionReportMs;
                _stub?.RaisePosition(_virtualPositionMs);
            });
        _positionTimer.Start();

        Log($"[np-gpu] 播放已启动: 曲目 180s, 进度上报 {PositionReportMs}ms/次, " +
            $"歌词 {lines.Count} 行, 桩={(_stub is null ? "不可驱动" : "可驱动")}");
    }

    private static void StopPositionReporting()
    {
        _positionTimer?.Stop();
        _positionTimer = null;
    }

    /// <summary>
    /// 预热:把每个会出现的视觉态先走一遍,否则第一个测量窗口会串进冷启动尾巴 ——
    /// 首次光栅化(字体、大图、径向渐变)与首次合成都不是稳态。
    /// 顺便让歌词列表实化一次,这样 Refs 才能抓到那些 ListBoxItem。
    /// </summary>
    private static async Task WarmUpAsync(Refs refs)
    {
        foreach (var scenario in BuildScenarios())
        {
            await ApplyAsync(refs, scenario);
            await Task.Delay(350);
        }

        refs.CaptureVisualStats();
        Log($"[np-gpu] 预热完成;歌词行实化 {refs.LyricItemCount} 条(带模糊 {refs.BlurredItemCount} 条), " +
            $"色团椭圆找到 {refs.BackdropEllipses}/10 个");
        if (refs.BackdropEllipses < s_ellipseNames.Length)
            Log("[np-gpu] ⚠ 色团椭圆没找全 —— 「停色团动画」那一列只停了一部分,判读要打折");
    }

    // ────────────────────────────── 读数与判定 ──────────────────────────────

    /// <summary>
    /// 帧节奏:连续请求动画帧,取相邻回调时间戳间隔的中位数。
    /// 口径提醒:请求帧这件事**本身**会让平台出帧,所以它量到的是
    /// "平台被要求出帧时的节奏"(≈ 窗口所在显示器的刷新率),不是"应用自主出帧的速率"。
    /// 放在这里是为了解释 GPU 的**量级** —— 同一场景跑在 60Hz 与 144Hz 上,
    /// 每帧代价相同,GPU 占比可以差一倍多。
    /// </summary>
    private static async Task<double> MeasureFrameIntervalAsync(Window window, int milliseconds)
    {
        var intervals = new List<double>();
        var completion = new TaskCompletionSource();
        var start = Stopwatch.GetTimestamp();
        double? previous = null;

        void OnFrame(TimeSpan timestamp)
        {
            var now = timestamp.TotalMilliseconds;
            if (previous is { } last && now > last) intervals.Add(now - last);
            previous = now;
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= milliseconds)
            {
                completion.TrySetResult();
                return;
            }
            window.RequestAnimationFrame(OnFrame);
        }

        window.RequestAnimationFrame(OnFrame);
        if (await Task.WhenAny(completion.Task, Task.Delay(6000)) != completion.Task) return double.NaN;
        if (intervals.Count == 0) return double.NaN;
        intervals.Sort();
        return intervals[intervals.Count / 2];
    }

    private static void LogScreens(Window window)
    {
        var screens = window.Screens.All;
        for (var index = 0; index < screens.Count; index++)
        {
            var screen = screens[index];
            Log($"[np-gpu] 显示器[{index}] {screen.Bounds.Width:F0}x{screen.Bounds.Height:F0} " +
                $"@({screen.Bounds.X:F0},{screen.Bounds.Y:F0}) primary={screen.IsPrimary} scaling={screen.Scaling:F2}");
        }
    }

    /// <summary>把窗口挪到指定显示器:两块的刷新率可能不同(本机 144Hz 虚拟屏 + 60Hz 独显输出),
    /// 同一场景的 GPU 占比会差一倍以上,所以量的时候必须知道在哪块屏上。</summary>
    private static void PlaceOnScreen(Window window, int index, double width, double height)
    {
        var screens = window.Screens.All;
        if (index < 0 || index >= screens.Count) return;
        var target = screens[index];
        window.Position = new PixelPoint(
            target.Bounds.X + (int)Math.Max(0, (target.Bounds.Width - width) / 2),
            target.Bounds.Y + (int)Math.Max(0, (target.Bounds.Height - height) / 2));
        Log($"[np-gpu] 已把窗口移到显示器[{index}]");
    }

    private static void Report(List<Row> rows, Refs refs)
    {
        Log("[np-gpu] ===== 全场景(同轮) =====");
        foreach (var row in rows)
            Log($"[np-gpu]   {row.Label,-24} GPU={row.GpuMean,6:F2}%(单引擎峰 {row.EnginePeak,5:F2}%) " +
                $"CPU={row.Cpu,6:F2}% 帧节奏={DescribeCadence(row.FrameIntervalMs)}");

        var baseline = Find(rows, "详情页·默认");
        var drift = Find(rows, "详情页·默认(复测)");
        var home = Find(rows, "首页·对照");
        var lyricBase = Find(rows, "歌词·默认");

        if (baseline is null)
        {
            Log("[np-gpu] 判定失败: 没有详情页默认行");
            return;
        }

        Log("[np-gpu] ===== 消融:每一项相对「详情页·默认」的净代价 =====");
        Ablate(rows, baseline, "详情页·关动态背景设置", "色团**漂移动画**本身(用户关掉「动态背景」开关,走真实设置链路)");
        Ablate(rows, baseline, "详情页·减色团", "封面色团背景层(10 个全屏径向渐变椭圆)");
        Ablate(rows, baseline, "详情页·停进度循环", "两个进度自续订 RAF 循环");
        Ablate(rows, baseline, "详情页·关设置+停循环", "上面三者全关(下界)");

        if (home is not null)
        {
            var homeForced = Find(rows, "首页·强制开色团动画");
            if (homeForced is not null)
                Log($"[np-gpu] 修复验收: 首页·应用门控 {home.GpuMean:F2}% vs 首页·强制开漂移 {homeForced.GpuMean:F2}% " +
                    $"⇒ 省下 {homeForced.GpuMean - home.GpuMean:+0.00;-0.00}% GPU、CPU {homeForced.Cpu:F2}% → {home.Cpu:F2}%");
            Log($"[np-gpu] 详情页 vs 首页: {baseline.GpuMean:F2}% vs {home.GpuMean:F2}% " +
                $"⇒ {baseline.GpuMean - home.GpuMean:+0.00;-0.00}%(详情页本身的净增)");
        }

        if (lyricBase is not null)
        {
            Ablate(rows, lyricBase, "歌词·减模糊", $"歌词逐行的 BlurEffect({refs.LyricItemCount} 行实化)");
            Ablate(rows, lyricBase, "歌词·只模糊近处", "只留当前句上下两行的模糊(更远的行只靠透明度)");
            Ablate(rows, lyricBase, "歌词·减模糊+减色团", "歌词模糊 + 封面色团层");
            Log($"[np-gpu] 歌词面板本身的代价: 歌词·默认 {lyricBase.GpuMean:F2}% " +
                $"vs 详情页·默认 {baseline.GpuMean:F2}% ⇒ {lyricBase.GpuMean - baseline.GpuMean:+0.00;-0.00}%");
        }

        if (drift is not null)
            Log($"[np-gpu] 漂移核对: 默认 {baseline.GpuMean:F2}% vs 复测 {drift.GpuMean:F2}% " +
                $"⇒ {drift.GpuMean - baseline.GpuMean:+0.00;-0.00}%(小于这个量的差异不算结论)");
    }

    private static void Ablate(List<Row> rows, Row baseline, string label, string what)
    {
        var target = Find(rows, label);
        if (target is null)
        {
            Log($"[np-gpu]   {what}: 缺 {label} 行,跳过");
            return;
        }

        Log($"[np-gpu]   {what}: {baseline.GpuMean - target.GpuMean,6:+0.00;-0.00}% GPU " +
            $"(默认 {baseline.GpuMean:F2}% → {target.GpuMean:F2}%)");
    }

    private static Row? Find(List<Row> rows, string label)
    {
        foreach (var row in rows) if (row.Label == label) return row;
        return null;
    }

    private static string DescribeCadence(double intervalMs) =>
        double.IsNaN(intervalMs) || intervalMs <= 0 ? "未知" : $"{intervalMs:F1}ms({1000 / intervalMs:F0}fps)";

    private static string Describe(object? value) => value is null ? "缺" : "有";

    private static (double Width, double Height) ParseSize(string text)
    {
        var parts = text.Split('x', 'X');
        if (parts.Length == 2
            && double.TryParse(parts[0], out var width) && width > 0
            && double.TryParse(parts[1], out var height) && height > 0)
            return (width, height);
        return (1200, 720);
    }

    private static async Task DrainAsync(int milliseconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

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
        double GpuMean,
        double GpuPeak,
        double EnginePeak,
        int Engines,
        double Cpu,
        int LyricItems,
        int BlurredItems,
        double FrameIntervalMs);
}
