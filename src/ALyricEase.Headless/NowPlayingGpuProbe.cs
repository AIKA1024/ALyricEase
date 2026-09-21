using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
// 用别名而不是 using System.Numerics:后者会把 System.Numerics.Vector 一起带进来,
// 与本文件里那些裸写的 Avalonia.Vector(如 new Vector(96, 96))撞成 CS0104。
using Vector3 = System.Numerics.Vector3;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
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
/// 聚焦模式(`ALY_NP_MODE=backdrop-ab`):只跑色团那几个变体,按**拉丁方**排顺序
/// (N 变体 × N 位置,每变体在每个位置上各坐一次)⇒ 均值里的位置偏差严格抵消;
/// 谁进这张表、为什么,见 `s_abVariants` 与 `BuildScenarios`。
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
    /// <summary>单个场景的采样窗口(毫秒)。够长以跨过 1 秒级的调度抖动。
    /// 交错 A/B 模式可以用 <c>ALY_NP_SAMPLE</c> 拉长 —— 窗口越长,配对差的方差越小。</summary>
    private static int s_sampleMs = 5000;

    /// <summary>状态切换后的沉降时间(等 0.45s 的覆盖层滑入动画落地、布局稳定)。</summary>
    private const int SettleMs = 800;

    /// <summary>GPU 计数器轮询间隔。GPU Engine 是速率型计数器,过密只会重复读到同一段增量。</summary>
    private const int GpuIntervalMs = 250;

    private const double DurationMs = 180_000;
    private const int PositionReportMs = 200;

    /// <summary>色团层里 10 个椭圆的元件名(A 层 5 个 + B 层 5 个,CrossFade 用)。</summary>
    private static readonly string[] s_ellipseNames =
        ["A0", "A1", "A2", "A3", "A4", "B0", "B1", "B2", "B3", "B4"];

    /// <summary>CrossFade 的两层(不显示的那层常驻,Opacity=0)。</summary>
    private const string s_layerAName = "PaletteLayerA";
    private const string s_layerBName = "PaletteLayerB";

    /// <summary>
    /// 探针自己驱动漂移时的间隔(毫秒)。<b>33 = 生产当前用的值</b> —— 自证、替身那几档
    /// 一律用它,确保"探针量到的"就是"生产跑的"。
    /// </summary>
    private const int ProbeDriftMs = 33;

    /// <summary>"漂移 60fps"那一档用的间隔 —— 频率曲线上的中间点。</summary>
    private const int ProbeDrift60Ms = 16;

    /// <summary>
    /// 探针替身定时器的 <see cref="DispatcherPriority"/>,生产用的就是 <c>Render</c>。
    /// 这是个**诊断旋钮**:实测生产的 33ms 只跑到 ~13 次/秒(名义 30),而把间隔改成 16ms
    /// 读数一模一样 —— 说明卡住它的不是间隔,那就只剩"谁在泵这个优先级的队列"。
    /// <c>ALY_NP_DRIFT_PRIORITY=normal|input|background|render</c> 换一个优先级量同一件事。
    /// </summary>
    private static DispatcherPriority s_probePriority = DispatcherPriority.Render;

    // ── 下面这一组是**应用动画的画像**(AlbumCoverBackground.TryStartMotion)的副本 ──
    //
    // 为什么副本不可避免:探针要试的两件事("换成单视觉纯平移""换成 30fps 定时器")改的正是
    // "谁在推、推几条",所以轨迹本身必须还是应用那一条 —— 否则差里会混进"另一套动画"。
    // 这也是唯一允许复刻的地方:其它档一律走应用的启动路径(见 RestoreMechanics 的注释)。
    // ⚠ 应用改了轨迹,这里就得跟着改;数值对不上时量出的差是假的。
    private static readonly TimeSpan s_driftCycle = TimeSpan.FromSeconds(26);
    private static readonly SplineEasing s_slowEasing = new(0.62, 0.03, 0.38, 0.97);
    private static readonly SplineEasing s_burstEasing = new(0.42, 0, 0.58, 1);

    private static readonly (Vector3 Hold, Vector3 Burst, Vector3 Drift)[] s_driftPaths =
    [
        (new Vector3(12, 5, 0), new Vector3(148, 58, 0), new Vector3(64, 142, 0)),
        (new Vector3(-7, 13, 0), new Vector3(-86, 146, 0), new Vector3(-154, 32, 0)),
        (new Vector3(-14, -6, 0), new Vector3(-152, -70, 0), new Vector3(22, -154, 0)),
        (new Vector3(9, -12, 0), new Vector3(108, -128, 0), new Vector3(158, 28, 0)),
        (new Vector3(-10, 8, 0), new Vector3(-104, 92, 0), new Vector3(82, 132, 0)),
    ];

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
            if (int.TryParse(Env("ALY_NP_SAMPLE"), out var sampleMs) && sampleMs >= 1000) s_sampleMs = sampleMs;
            if (ParsePriority(Env("ALY_NP_DRIFT_PRIORITY")) is { } priority)
            {
                s_probePriority = priority;
                Log($"[np-gpu] 探针替身定时器优先级改为 {priority}(默认 Render 与生产一致)");
            }
            Log($"[np-gpu] 采样窗口 {s_sampleMs}ms/场景,模式={Env("ALY_NP_MODE") ?? "全量"}");
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
                    $"帧节奏={DescribeCadence(row.FrameIntervalMs)} 漂移={row.DriftRate,4:F1}次/秒");


            }

            await BlurPixelTestAsync(refs);
            await BackdropPixelTestAsync(refs);

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

        /// <summary>
        /// 只去掉**当前句**那一行的 Effect。样式给 selected 行的是
        /// <c>&lt;BlurEffect Radius="0"/&gt;</c> —— 视觉上等于没有模糊,却仍让该行
        /// 走离屏 + 模糊这条管线。若它真的贵,那是纯付费零收益的东西。
        /// </summary>
        ExceptSelected,

        /// <summary>全部关掉(消融用)。</summary>
        None,

        /// <summary>
        /// 对照口径:其余行照原样,但**额外**给当前句写一个 <c>BlurEffect Radius=0</c>
        /// —— 复刻修复前样式里那条 <c>&lt;Setter Property="Effect"&gt;&lt;BlurEffect Radius="0"/&gt;</c>。
        /// 修好之后,「歌词·默认」就是修后的样子,这一格是修前的样子,两者同轮可比。
        /// </summary>
        AllWithZeroOnSelected,

        /// <summary>
        /// 对照口径:给当前句写一个**半径正常的**模糊(1.5,与 above 行一致)。
        /// 与 <see cref="AllWithZeroOnSelected"/> 配对,用来分清"贵是因为半径 0"
        /// 还是"贵是因为当前句这一行"—— 两者修法完全不同。
        /// </summary>
        AllWithRealOnSelected,
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

    /// <summary>
    /// 色团层的几种"更便宜的画法"。
    ///
    /// 每一项都必须**可逆**:<see cref="Refs.SetBackdropAblation"/> 每次都先把 10 个椭圆
    /// 还原成应用自己的样子,再套上本档的改动 —— 场景之间不残留,行与行才能直接相减。
    /// 已知口径:径向渐变改位图这条路,先前用椭圆级 <c>BitmapCache</c> 量过,只值 +0.15%
    /// (见 perf-notes),所以这里的重点是把"到底哪一段在花钱"拆开:
    /// 是渐变的 fragment 计算、是 5 层半透明混合、还是那个 Opacity=0 却还在渲染树里的层。
    /// </summary>
    private enum BackdropAblation
    {
        /// <summary>照应用自己的样子(不做任何改动)。</summary>
        Default,

        /// <summary>10 个椭圆各挂 <c>BitmapCache</c>:渐变栅格化一次,漂移只移动位图。</summary>
        EllipseCache,

        /// <summary>整层(Canvas)挂 <c>BitmapCache</c>:5 次半透明混合有机会合成 1 次。</summary>
        LayerCache,

        /// <summary>
        /// 径向渐变换成**预渲染位图**(<c>ImageBrush</c>)—— 用户问的那条路。
        ///
        /// 结论(2026-09-21,位置平衡 A/B 6 vs 6 行):**无效**。位图 12.03%(极差 0.33)
        /// vs 渐变 12.26%(极差 0.49),差 0.23% —— 比组内极差还小。别再做这条路:
        /// 同一轮里"整个色团层不画"的上界也只有 0.85%,换填充方式最多只能吃到这个数。
        /// (那个夹具后来被拉丁方取代,这一档没在拉丁方里重跑 —— 结论方向不必变:
        /// 它连"层值 0.85%"这个上界都够不着。)
        /// </summary>
        BitmapBlobs,

        /// <summary>
        /// 强制用径向渐变画。生产代码本来就是渐变 ⇒ 这一档如今是**空操作**,
        /// 留着是为了"若有人再试位图填充,能拿它做同轮对照"。
        /// </summary>
        GradientBlobs,

        /// <summary>渐变换成同 alpha 的纯色。归因用:渐变 shader 本身值多少(纯色是它的下界)。</summary>
        SolidBlobs,

        /// <summary>把当前不显示的那一层(<c>Opacity=0</c>)也摘出渲染树。归因用。</summary>
        HideIdleLayer,

        /// <summary>每层只留 3 个色团(藏掉 2 个)。归因用:每个色团的全屏混合值多少。</summary>
        ThreeBlobs,

        /// <summary>
        /// 把当前**不显示**的那一层(<c>Opacity=0</c>)的漂移动画停掉 —— 10 个椭圆里有一半
        /// 永远在替一个看不见的层做无用功。与「藏掉闲置层」是两回事:<c>IsVisible=false</c>
        /// **停不了已经跑起来的组合动画**(项目里踩过这个坑),两档必须分开量。
        /// </summary>
        StopIdleMotion,

        /// <summary>
        /// 用户提过的那一版:**每层栅格化成一张 1000×1000 纹理**,用 <c>Image</c> 顶替 5 个椭圆,
        /// 漂移改成**单视觉、纯平移**。等于把原版在 DWM 里的模型(固定画布 + 合成器缩放)
        /// 在 Avalonia 里尽力凑一遍。
        ///
        /// 结论(2026-09-21,拉丁方):**判负,别再做**。最干净的口径是"同一个驱动器、只换画的东西":
        /// 探针定时器推 10 个椭圆渐变的 1.64% vs 推 2 张 1000² 纹理的 11.35% —— **差 7 倍**。
        /// (相对那轮默认 7.38% 则是"更贵 3.97%"、CPU 多 8 个点。)
        /// 旁证:同类"单个色团渐变→256² ImageBrush"的单项只值 0.23% ⇒ 贵的不是"纹理采样"这一件事,
        /// 差在"每帧搬一整块会被放大 1.92× 的大纹理"这个组合上,**具体机制未定论**。
        ///
        /// ⚠ 这条档的抓屏自证(`LayerTextureSelfCheckAsync`)**不稳定**:首轮 0.57 级(同一张画),
        /// 复跑 4.39 级 / 严口径 64.48%(相位没对齐或替身没烘对,工具分不出来)。
        /// **所以判负只按 GPU 读数看**,别引那条自证。
        /// 留着是为了"以后再有人提这条路,能直接重跑,不用重新发明夹具"。
        /// </summary>
        LayerTexture,

        /// <summary>
        /// 33ms <c>DispatcherTimer</c> 直接写 <c>visual.Translation/Scale</c>。
        ///
        /// **这一档是被否决的**:生产 2026-09-21 上过一版、又回退了,因为**画面肉眼可见地卡**。
        /// 原因不是审美,是这条驱动的物理量:实测只跑到 <b>12.5~14 次/秒</b>
        /// (名义 33ms 不是实际值 —— 间隔改 16ms 读数一样、优先级 Render↔Normal 也一样),
        /// 而且位置取自 15.6ms 分辨率的 <c>Environment.TickCount64</c>,被量化成阶梯。
        /// 它的读数确实漂亮(1.9~2.3% GPU vs 组合动画 12%),但那不是"省出来"的,是**少画了帧** ——
        /// 换算到每帧两者几乎持平(0.13% vs 0.16% GPU/帧)。
        /// 留着当**反面回归对**:谁再想"降频省 GPU",先看这一档的实测频率和它在真机上的观感。
        /// </summary>
        TimedDrift33,

        /// <summary>
        /// 定时器驱动但换成 16ms。这一档只为回答"名义间隔到底是不是杠杆":实测与
        /// <see cref="TimedDrift33"/> **无差**(2.02 vs 2.12),所以不进拉丁方。
        /// </summary>
        TimedDrift60,

        /// <summary>
        /// 20 条组合动画(10 个椭圆 × Translation+Scale),由合成器按显示刷新率推帧。
        /// **这就是生产现状**(2026-09-21 回退之后) ⇒ 它与 <see cref="Default"/> 等价,
        /// 留着的用途是"生产若再换驱动,能一眼对照"以及核对"应用自己的机制"与"探针复刻"是否同价。
        /// 它的关键性质:与"UI 线程能不能按时回调"**无关** —— 帧节奏读数塌到 13fps 时 GPU 仍稳在 12%,
        /// 所以别拿帧节奏那一栏去否定它(那一栏只对"UI 线程推帧"的档有效)。
        /// </summary>
        CompositorDrift,
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
        bool DynamicBackground = true,
        /// <summary>色团层这一档的改动(<c>Default</c> = 照应用自己的样子)。</summary>
        BackdropAblation Backdrop = BackdropAblation.Default,
        /// <summary>
        /// 歌词行的位图缓存三态:<c>null</c> = 照样式走(样式里已经给每行挂了一个);
        /// <c>true</c> = 强制挂;<c>false</c> = 强制摘掉(复刻加缓存之前的样子)。
        /// </summary>
        bool? LyricCache = null,
        /// <summary>
        /// 详情页主画布(<c>DesktopCanvas</c>:封面、标题、按钮、面板)画不画。
        /// 背景层不归它管 ⇒ 可以做到"色团照漂移、页面不画",用来回答
        /// "那 10% 到底花在色团上还是花在**被反复重画的页面**上"。
        /// </summary>
        bool PageContent = true);

    /// <summary>
    /// 聚焦模式(拉丁方)里的一个变体 —— 一份**完整状态**,不是一个"开关"。
    /// 每一项都要能独立说清"它和默认差在哪",否则差值的归因就成了一锅粥。
    /// </summary>
    private sealed record AbVariant(
        string Label,
        BackdropAblation Mode = BackdropAblation.Default,
        bool PageContent = true,
        bool DynamicBackground = true,
        bool BackdropVisible = true);

    /// <summary>
    /// 当前跑的是不是 A/B 夹具（拉丁方）。**只有它**允许探针去"落实并校验"驱动状态 ——
    /// 全量验收档必须袖手旁观（门控链路自证才有意义），否则量到的是探针而不是应用。
    /// </summary>
    private static readonly bool s_abMode =
        string.Equals(Env("ALY_NP_MODE"), "backdrop-ab", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 拉丁方里各变体的**基准序**（第 r 轮把整条序左移 r 位）。
    ///
    /// 顺序就是 `AB·默认` 打头,只有一个原因:报告里的差值全以它为基准,
    /// 而"默认"读数的分母位置偏差已经被拉丁方抵消掉了,谁打头都不影响,但读起来方便。
    /// </summary>
    private static readonly AbVariant[] s_abVariants =
    [
        // 生产现状:20 条组合动画,由合成器按显示刷新率推帧(2026-09-21 从定时器**回退**到这里)。
        new("AB·默认"),

        // 被否决的那一档:33ms 定时器驱动(探针复刻)。它省下的是**帧数**不是每帧成本 ——
        // 读数很漂亮,可代价是画面卡(实测只有 12.5~14 次/秒,且位置被量化)。反面回归对就在这一格。
        new("AB·定时器33ms", BackdropAblation.TimedDrift33),

        // 诊断锚:漂移照跑,但详情页主画布(封面/标题/按钮/面板)不画。
        // 用来切开"色团的钱"和"页面的钱" —— 实测两者都只是零头,钱全在"多久重绘一次"上。
        new("AB·藏页面内容", PageContent: false),

        // 标尺锚:干脆不动。任何单项收益都该 ≤(默认 − 它),超了就是夹具在骗人。
        new("AB·关动态背景", DynamicBackground: false),

        // ⚠ 「漂移 60fps」那一档(TimedDrift60)**故意不进这张表**:实测它和 TimedDrift33 读数无差
        // (2.02 vs 2.12),而"间隔改成 16ms 却什么都没变"只能说明 DispatcherTimer 在那个间隔上
        // 根本没按 60Hz 跑 —— 留在表里就是一个说不清的数。要用它先看探针报的**实测频率**。
    ];

    private static IEnumerable<Scenario> BuildScenarios()
    {
        // 聚焦模式(拉丁方):同一批变体在同一轮里把每个位置都坐一遍,只看**均值差**。
        //
        // 为什么非要同轮:GPU 计数器量的是"本进程占引擎活跃时间的比例",别的程序一占 GPU,
        // 整张表都被整体抬高 —— 实测同一构建跨轮,「首页·对照」读到过 0.69/0.85/1.81%,
        // 「详情页·默认」7.79 → 10.24%,而我们要判的收益只有 0.5~2.5%。跨轮比绝对值必然失真。
        if (s_abMode)
        {
            // **拉丁方**:N 个变体 × N 个位置,每个变体在每个位置上各坐一次
            // (顺序 orders[round][slot] = (slot + round) % N)。
            //
            // 为什么"交错重复"和"轮内奇偶反序"都不够:读数带**轮内位置偏差**(第 1 行稳定比第 2 行
            // 高 1.4%),只要变体与位置相关,差里就混着位置差 —— 同一处改动在两个方向各量到
            // -2.81% 和 +1.39%,而两次的轮内值都稳到 ±0.1%("稳的假象比噪声危险")。
            // 奇偶反序只让每个变体坐 2 个位置,各变体的均值落在**不同的位置对**上,残差不会自己抵消。
            // 拉丁方让均值里的位置项**严格抵消** —— 这是这套夹具能拿到的最干净的口径。
            // 四个变体的分工见 s_abVariants:1 个回归对(旧驱动)、2 个锚(页面不画 / 干脆不动)。
            var orders = Enumerable.Range(0, s_abVariants.Length)
                .Select(round => Enumerable.Range(0, s_abVariants.Length)
                    .Select(slot => (slot + round) % s_abVariants.Length).ToArray())
                .ToArray();
            // ALY_NP_ROUNDS=n 只跑前 n 轮 —— 改夹具时用它把"验证机制"的往返从 3 分钟压到几十秒。
            // 正式跑数**必须**跑满:轮数不满,拉丁方的位置抵消就不成立。
            if (int.TryParse(Env("ALY_NP_ROUNDS"), out var rounds) && rounds > 0 && rounds < orders.Length)
                orders = orders.Take(rounds).ToArray();
            for (var round = 0; round < orders.Length; round++)
            for (var slot = 0; slot < orders[round].Length; slot++)
            {
                var variant = s_abVariants[orders[round][slot]];
                yield return new Scenario(
                    $"{variant.Label}#r{round + 1}p{slot + 1}",
                    NowPlaying: true,
                    LyricsPanel: false,
                    BackdropVisible: variant.BackdropVisible,
                    BackdropMotion: MotionMode.Default,
                    ProgressLoop: true,
                    LyricBlur: LyricBlurMode.All,
                    DynamicBackground: variant.DynamicBackground,
                    Backdrop: variant.Mode,
                    PageContent: variant.PageContent);
            }
            yield break;
        }

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
        // 色团这一层到底哪一段在花钱 —— 五档都是从「详情页·默认」只改一个自变量:
        //   ① 渐变 fragment 本身   ② 5 层全屏半透明混合   ③ 那个看不见却还在渲染树里的层
        // 方案 A(旧):椭圆级栅格化。先前量过只值 +0.15%,留在这里当"渐变算法不贵"的对照。
        yield return new Scenario("详情页·色团椭圆缓存", true, false, true, MotionMode.Default, true, LyricBlurMode.All, true, BackdropAblation.EllipseCache);
        yield return new Scenario("详情页·色团整层缓存", true, false, true, MotionMode.Default, true, LyricBlurMode.All, true, BackdropAblation.LayerCache);
        // 方案 B:径向渐变 → 预渲染位图,内容一模一样,只把"算渐变"换成"采样纹理"。
        yield return new Scenario("详情页·色团渐变换位图", true, false, true, MotionMode.Default, true, LyricBlurMode.All, true, BackdropAblation.BitmapBlobs);
        // 归因下界:连渐变都不算,直接纯色。它若和默认差得不多,说明渐变 shader 根本不是成本。
        yield return new Scenario("详情页·色团渐变改纯色", true, false, true, MotionMode.Default, true, LyricBlurMode.All, true, BackdropAblation.SolidBlobs);
        // 归因:CrossFade 的 B 层常驻(Opacity=0),5 个椭圆的漂移**从来没停过**。
        yield return new Scenario("详情页·藏掉闲置色团层", true, false, true, MotionMode.Default, true, LyricBlurMode.All, true, BackdropAblation.HideIdleLayer);
        yield return new Scenario("详情页·色团减到3个", true, false, true, MotionMode.Default, true, LyricBlurMode.All, true, BackdropAblation.ThreeBlobs);
        yield return new Scenario("详情页·停进度循环", true, false, true, MotionMode.Default, false, LyricBlurMode.All, true);
        // 全关的下界:两条都是**应用自己的路径**(设置开关 + 循环开关),不碰动画本身。
        yield return new Scenario("详情页·关设置+停循环", true, false, true, MotionMode.Default, false, LyricBlurMode.All, false);

        // 歌词面板:用户点开歌词后的样子(逐行带模糊的那套样式在这里才生效)。
        yield return new Scenario("歌词·默认", true, true, true, MotionMode.Default, true, LyricBlurMode.All, true);
        // 修复前的样子:其余行不变,只把当前句那条半径 0 的模糊补回去。
        yield return new Scenario("歌词·修复前(当前句Radius0)", true, true, true, MotionMode.Default, true, LyricBlurMode.AllWithZeroOnSelected, true);
        // 配对:当前句换成半径 1.5 的**真**模糊。与上一格的差回答"是半径的错还是这一行的错"。
        yield return new Scenario("歌词·当前句真模糊(1.5)", true, true, true, MotionMode.Default, true, LyricBlurMode.AllWithRealOnSelected, true);
        yield return new Scenario("歌词·只模糊近处", true, true, true, MotionMode.Default, true, LyricBlurMode.Nearest, true);
        // 与「歌词·默认」只差一项:当前句那行的 BlurEffect(Radius=0) 被摘掉。
        // 它若不贵,两行应当贴着;若差出一大截,说明"半径 0"照样在付离屏的钱。
        yield return new Scenario("歌词·去当前句模糊", true, true, true, MotionMode.Default, true, LyricBlurMode.ExceptSelected, true);
        yield return new Scenario("歌词·减模糊", true, true, true, MotionMode.Default, true, LyricBlurMode.None, true);
        // 对照口径:把样式里那个 BitmapCache 摘掉(复刻加缓存之前)。与「歌词·默认」
        // 只差这一项 —— 两行之差就是"把每行缓存在位图里"省下的钱。
        yield return new Scenario("歌词·无行缓存", true, true, true, MotionMode.Default, true, LyricBlurMode.All, true, LyricCache: false);
        yield return new Scenario("歌词·减模糊+减色团", true, true, false, MotionMode.Default, true, LyricBlurMode.None, true);
        // 模糊全保留、只把色团漂移按真实设置链路停掉 ⇒ 回答"模糊的代价是不是
        // 靠'整窗每帧重画'才被放大"。这一格决定修法方向:是改模糊画法,还是改重画频率。
        yield return new Scenario("歌词·关动态背景", true, true, true, MotionMode.Default, true, LyricBlurMode.All, false);
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
        // 漂移驱动的**实测节拍**,和 GPU 放在同一个窗口里量:"整窗重绘多少次"是这一层的全部杠杆,
        // 而它随窗口状态/负载变,跨窗口借读数会把"这一格跑得更快"错记成"这一格更省"。
        // ⚠ 生产已是组合动画(合成器推帧),没有 tick 可数 —— 这种行这一栏是 NaN(打"—"),
        // 它的等价量是"帧节奏"那一栏。只有探针复刻的定时器档才有得数。
        var ticksBefore = refs.ProbeTicks;
        var tickDriven = refs.ProbeDriving;

        while (wall.ElapsedMilliseconds < s_sampleMs)
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
        var driftRate = tickDriven
            ? (refs.ProbeTicks - ticksBefore) / Math.Max(0.5, seconds)
            : double.NaN;
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
            frameInterval,
            driftRate,
            refs.LastStateOk);
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
        // 页面内容与背景层是**兄弟**:可以只关前者,做出"色团照漂移、页面不画"的诊断态。
        refs.SetPageContent(scenario.PageContent);
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
        refs.SetBackdropAblation(scenario.Backdrop);
        // 放在 SetLyricBlur 之后:行引用在那里面刚刷新过,缓存要挂到当前实化的实例上。
        refs.SetLyricCache(scenario.LyricCache);

        // A/B 夹具:标签要求的**驱动状态**必须落实到位并读回校验。"照应用自己的样子"这一档
        // 尤其危险 —— 它要求"应用自己在动"，而上一档的收尾动作可能把动画掐掉却没让应用知道，
        // 于是同一档在同一轮里读出过 0.58% 与 8.51% 两种值(差 15 倍)。那不是噪声，是夹具故障。
        // ⚠ 只在 A/B 模式里做:全量验收档要**袖手旁观**(探针一碰门控链条，量的就是探针了)。
        var wantProductionDrift = scenario.DynamicBackground && scenario.BackdropVisible
                                  && scenario.Backdrop == BackdropAblation.Default;
        if (s_abMode) refs.EnsureProductionDrift(wantProductionDrift);

        await Task.Delay(SettleMs);

        if (s_abMode)
        {
            var actualDrift = refs.ProductionDriftRunning;
            refs.LastStateOk = actualDrift == wantProductionDrift;
            Log($"[np-gpu]   · {scenario.Label} 状态校验: 期望漂移={(wantProductionDrift ? "动" : "停")} " +
                $"实际={(actualDrift ? "动" : "停")} ⇒ {(refs.LastStateOk ? "一致" : "⚠ 不一致,这一行读数不可信")}");
        }

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
        // 顺序要紧:先让色团层回到应用自己的样子(上一行若是「单纹理」/「限频」,替身还在树里),
        // **再**停漂移 —— 反过来会把还原时那一次"重建动画"留在测量态里,整个像素验证的背景
        // 就在动,所有差值都变成噪声。页面内容同理:它可能还停在「藏页面内容」那一档上。
        refs.SetBackdropAblation(BackdropAblation.Default);
        refs.SetPageContent(true);
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

        // 修复验收:当前句挂 Radius=0 与什么都不挂,两帧比一比。半径 0 是恒等变换,
        // 若两帧之差只有噪声底,就说明这次改动是**视觉零变化**的纯性能修复。
        refs.SetLyricBlur(LyricBlurMode.AllWithZeroOnSelected);
        await Task.Delay(600);
        var zeroOnSelected = ScreenCapture.GrabClient(refs.Window);

        refs.SetLyricBlur(LyricBlurMode.All);
        await Task.Delay(600);
        var noEffectOnSelected = ScreenCapture.GrabClient(refs.Window);

        if (blurred1.IsEmpty || region is null)
        {
            Log("[np-gpu] 抓屏失败或定位不到歌词区,跳过像素验证");
            return;
        }

        // 方案 B 的分叉点:BitmapCache 缓存的是"带模糊的渲染结果"(视觉不变、只是不再重算),
        // 还是干脆把 Effect 跳过了(画面没有模糊)?两种情况的 GPU 读数一样(都 -4.4%),
        // 只有像素能分开 —— 这是"能不能用"的唯一判据。
        refs.SetLyricBlur(LyricBlurMode.All);
        refs.SetLyricCache(false);
        await Task.Delay(800);
        var cacheOff = ScreenCapture.GrabClient(refs.Window);

        refs.SetLyricCache(true);
        await Task.Delay(800);
        var cacheOn = ScreenCapture.GrabClient(refs.Window);

        refs.SetLyricCache(false);
        await Task.Delay(600);

        var noise = ScreenCapture.DiffRatio(blurred1, blurred2, region);
        var effect = ScreenCapture.DiffRatio(blurred1, cleared, region);
        var restore = ScreenCapture.DiffRatio(blurred1, restored, region);
        var selectedFix = ScreenCapture.DiffRatio(zeroOnSelected, noEffectOnSelected, region);
        var cacheVsNoCache = ScreenCapture.DiffRatio(cacheOn, cacheOff, region);
        var cacheVsCleared = ScreenCapture.DiffRatio(cacheOn, cleared, region);
        Log($"[np-gpu] 歌词区 {region.Value.Width}x{region.Value.Height} 设备像素, " +
            $"带模糊行 {refs.BlurredItemCount} 条 / 共 {refs.LyricItemCount} 条");
        Log($"[np-gpu]   噪声底(带模糊 vs 带模糊)     = {Percent(noise)}");
        Log($"[np-gpu]   消融差(带模糊 vs 不带模糊) = {Percent(effect)}");
        Log($"[np-gpu]   可逆性(带模糊 vs 已恢复)   = {Percent(restore)}");
        Log($"[np-gpu]   当前句修复(挂Radius0 vs 不挂) = {Percent(selectedFix)}  ← 与噪声底同档即视觉零变化");
        Log($"[np-gpu]   方案B缓存开 vs 缓存关          = {Percent(cacheVsNoCache)}  ← ≈噪声底 ⇒ 缓存住了模糊,可用");
        Log($"[np-gpu]   方案B缓存开 vs 无模糊          = {Percent(cacheVsCleared)}  ← ≈消融差 ⇒ Effect 被跳过,画面没糊");
        var cacheVerdict = Math.Abs(cacheVsNoCache - noise) < 0.002 && cacheVsCleared > effect / 2
            ? "BitmapCache 缓存了带模糊的结果 ⇒ 视觉不变,方案可用"
            : Math.Abs(cacheVsCleared - noise) < 0.002
                ? "BitmapCache 把 Effect 一起跳过了 ⇒ 视觉变了,方案不可用"
                : "无法判定(两者都不落在噪声底或消融差上)";
        Log($"[np-gpu]   方案B判定: {cacheVerdict}");

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

    /// <summary>
    /// 用屏幕像素回答"色团换了画法之后,还是不是同一个样子"。
    ///
    /// 消融只能说明省了多少,说明不了"观感有没有变"。色团这一层的改法全是"换个方式画出
    /// 同一堆柔光",所以判据只有一条:像素差与噪声底同档。顺序照 <see cref="BlurPixelTestAsync"/>
    /// 的老规矩:先连抓两帧定噪声底,再逐档换画法各抓一帧,最后恢复再抓一帧验可逆性。
    /// 全部在**漂移停掉**的状态下抓(漂移按设计就是每帧亚像素,不停的话噪声底没有意义)。
    /// </summary>
    private static async Task BackdropPixelTestAsync(Refs refs)
    {
        if (refs.Backdrop is null)
        {
            Log("[np-gpu] 夹具里没有封面背景,跳过色团像素验证");
            return;
        }

        Log("[np-gpu] ===== 抓屏验证:色团的几种画法是不是同一个样子 =====");

        refs.Main.NowPlayingPanel = NowPlayingPanel.None;
        // 必须显式把背景层放回可见:上一档若是「减色团」,控件还是隐藏的,
        // 那样这里所有像素差会恒在 0.01%(纯噪声)—— 一个"数字很漂亮"的假证据。
        refs.Backdrop.IsVisible = true;
        // 同样先还原色团层(**在**停漂移之前,理由见 BlurPixelTestAsync 里那条注释)。
        refs.SetBackdropAblation(BackdropAblation.Default);
        refs.SetPageContent(true);
        refs.SetBackdropMotion(false);
        refs.BarAnimator?.SetActive(false);
        refs.NowPlayingAnimator?.SetActive(false);
        await Task.Delay(1500);

        refs.SetBackdropAblation(BackdropAblation.Default);
        await Task.Delay(700);
        var plain1 = ScreenCapture.GrabClient(refs.Window);
        await Task.Delay(350);
        var plain2 = ScreenCapture.GrabClient(refs.Window);

        var shots = new List<(string Label, ScreenCapture.Frame Frame)>();
        foreach (var mode in new[]
                 {
                     BackdropAblation.BitmapBlobs,
                     BackdropAblation.SolidBlobs,
                     BackdropAblation.ThreeBlobs,
                     BackdropAblation.HideIdleLayer,
                 })
        {
            refs.SetBackdropAblation(mode);
            await Task.Delay(700);
            shots.Add((Describe(mode), ScreenCapture.GrabClient(refs.Window)));
        }

        refs.SetBackdropAblation(BackdropAblation.Default);
        await Task.Delay(700);
        var restored = ScreenCapture.GrabClient(refs.Window);

        if (plain1.IsEmpty)
        {
            Log("[np-gpu] 抓屏失败,跳过色团像素验证");
            return;
        }

        var noise = ScreenCapture.DiffRatio(plain1, plain2);
        var noiseMean = ScreenCapture.MeanDelta(plain1, plain2);
        Log($"[np-gpu] 全窗对照(漂移已停), 噪声底 = {Percent(noise)} / 平均通道差 {noiseMean:F2} 级");
        foreach (var (label, frame) in shots)
            Log($"[np-gpu]   {label,-14} vs 默认 = {Percent(ScreenCapture.DiffRatio(plain1, frame))}" +
                $" / 平均通道差 {ScreenCapture.MeanDelta(plain1, frame):F2} 级");
        Log($"[np-gpu]   可逆性(默认 vs 已恢复) = {Percent(ScreenCapture.DiffRatio(plain1, restored))}" +
            $" / 平均通道差 {ScreenCapture.MeanDelta(plain1, restored):F2} 级");

        // 阈值 3 的"严口径":重采样误差每像素只差几级,应当远低于噪声底;
        // 换了颜色/边界则会成片越过 3 级。两个口径一起看,才分得清"差几级"和"差多少像素"。
        foreach (var (label, frame) in shots)
            Log($"[np-gpu]   严口径 {label,-14} vs 默认(阈值 3)= {Percent(ScreenCapture.DiffRatio(plain1, frame, null, 3))}");

        // 判定用**阈值 3 的严口径**,不用阈值 24 那个。
        // 理由:柔光色团换画法时每像素只差几级,阈值 24 的口径几乎读不到东西,于是
        // "噪声底 0.00% vs 0.01%"这种一个像素级的抖动会把阈值从 0.40% 拉到 0.02%
        // (20 倍),同一组图判词从"视觉无差异"翻成"观感变了" —— 实测踩过。
        // 改法:口径换成严的,0.40% 当作**阈值的下限**(而不是"噪声底的下限"),并用严口径的噪声底。
        var noiseStrict = ScreenCapture.DiffRatio(plain1, plain2, null, 3);
        var threshold = Math.Max(Math.Max(noise, noiseStrict) * 2, 0.004);
        Log($"[np-gpu]   噪声底(严口径 阈值 3) = {Percent(noiseStrict)}; 判定阈值 = {threshold * 100:F2}%");
        foreach (var (label, frame) in shots)
        {
            var diff = ScreenCapture.DiffRatio(plain1, frame, null, 3);
            var verdict = double.IsNaN(diff) ? "无法比较"
                : diff <= threshold ? "视觉无差异"
                : "观感变了";
            Log($"[np-gpu]   判定 {label}: {verdict}(严口径 {Percent(diff)},阈值 {threshold * 100:F2}%)");
        }

        var saved = ScreenCapture.Save(plain1, "np-gpu-backdrop-default");
        var savedBitmap = ScreenCapture.Save(shots[0].Frame, "np-gpu-backdrop-bitmap");
        if (saved.Length > 0) Log($"[np-gpu] 两张对照图(渐变填充 / 位图填充): {saved} / {savedBitmap}");

        await DriftDriverSelfCheckAsync(refs, plain1);
        await LayerTextureSelfCheckAsync(refs);
    }

    /// <summary>
    /// 「单纹理」档的自证:把替身的相位按到 0(基准位形),再和还原后同样从相位 0 起步的原件比 ——
    /// 差的只该是"渐变改纹理"那点重采样误差。差值大就说明替身没烘对
    /// (烘空了 / 位置错位 / Opacity 烘进去了),那一行省下来的 GPU 也就不是"更快"而是"少了东西"。
    /// </summary>
    private static async Task LayerTextureSelfCheckAsync(Refs refs)
    {
        refs.SetBackdropAblation(BackdropAblation.LayerTexture);
        refs.ForceProbeDriftPhase(0);
        await Task.Delay(700);
        var substitute = ScreenCapture.GrabClient(refs.Window);

        // 还原会走应用门控把生产那套重新起起来(相位从 0 起算),所以紧接着再停一次 ——
        // 两边都停在 ≈基准位形上才比得出"是不是同一张画"。
        refs.SetBackdropAblation(BackdropAblation.Default);
        refs.SetBackdropMotion(false);
        await Task.Delay(700);
        var original = ScreenCapture.GrabClient(refs.Window);

        if (substitute.IsEmpty || original.IsEmpty) return;
        var mean = ScreenCapture.MeanDelta(original, substitute);
        // 判据用**平均通道差**:同一张画的差别只是重采样(≤2 级);差到 4 级以上,
        // 可能是"两侧停在**不同相位**上"(色团整体错开 ~150px 就值 4~5 级),
        // 也可能是"替身烘得不对"—— 这一条分不出来,所以两种情况一起说。
        // 为什么要喊出来:这条自证**不稳定**,实测首轮 0.57 级(通过)、复跑 4.39 级/严口径 64.48%。
        // 读到 4 级以上时,它就不再证明"替身是同一张画",该档结论只能按 GPU 读数看。
        Log($"[np-gpu] 单纹理档自证(替身 vs 原件,同停在基准位形) = {Percent(ScreenCapture.DiffRatio(original, substitute))}" +
            $" / 平均通道差 {mean:F2} 级" +
            $" / 严口径(阈值 3) {Percent(ScreenCapture.DiffRatio(original, substitute, null, 3))}" +
            (mean <= 2 ? " ⇒ 同一张画(差的是重采样)" : " ⇒ ⚠ 超过重采样量级:相位没对齐或替身没烘对,这条自证作废"));
    }

    /// <summary>
    /// 驱动自证。要证明两件事,否则读数再漂亮也可能是在骗自己:
    /// ① 生产那套驱动**真的在出帧**(组合动画由合成器推帧,帧节奏就是它的证据);
    /// ② 被否决的"定时器"档到底跑多少次/秒 —— 这是"降频省 GPU"那条路的死因,必须数出来。
    ///
    /// 判据用**平均通道差**而不是 <c>DiffRatio</c>:柔光色团整体挪 ~150px 时,每个像素都只差
    /// 几级,带阈值(24)的那个口径会读成 0.00% 把真变化整个吞掉(实测:0.00% / 5.05 级)。
    /// 噪声底是 0.00 级 ⇒ 1 级以上就算"屏幕确实在动"。
    /// </summary>
    private static async Task DriftDriverSelfCheckAsync(Refs refs, ScreenCapture.Frame baseline)
    {
        refs.SetBackdropAblation(BackdropAblation.Default);
        refs.SetPageContent(true);
        refs.SetBackdropMotion(true);
        await Task.Delay(600);

        // 生产的驱动是组合动画,没有 tick 可数 ⇒ 用"帧节奏"证明它确实在按刷新率出帧。
        // 这一栏对组合动画有效、对 UI 线程推帧的档会被负载拖走,别混用。
        var productionCadence = await MeasureFrameIntervalAsync(refs.Window, 900);
        Log($"[np-gpu] 生产驱动(组合动画,合成器推帧)帧节奏={DescribeCadence(productionCadence)}");

        // 反面:同参数(33ms)复刻那台被否决的定时器,数它**自己**的节拍(生产的计数器已随回退删掉)。
        refs.StartSelfCheckDrift();
        await Task.Delay(1200);
        Log($"[np-gpu] 被否决的定时器驱动(名义 {ProbeDriftMs}ms / {s_probePriority})实测 " +
            $"{refs.ProbeTickRate:F1} 次/秒 —— 它省的是**帧数**不是每帧成本,少掉的帧就是肉眼看到的卡" +
            (s_probePriority == DispatcherPriority.Render ? "(与当初那版同参数)" : "(诊断档:改了优先级)"));

        refs.ForceProbeDriftPhase(0);
        await Task.Delay(500);
        var hold = ScreenCapture.GrabClient(refs.Window);

        refs.ForceProbeDriftPhase(0.62);
        await Task.Delay(500);
        var burst = ScreenCapture.GrabClient(refs.Window);

        if (!hold.IsEmpty && !burst.IsEmpty)
        {
            var moved = ScreenCapture.MeanDelta(hold, burst);
            Log($"[np-gpu] 替身驱动自证(相位 0 vs 0.62,色团应挪 ~150px) = 平均通道差 {moved:F2} 级" +
                $" / {Percent(ScreenCapture.DiffRatio(hold, burst))} " +
                $"⇒ {(moved > 1 ? "定时器写得进屏幕,这一档的读数成立(该省的钱是真的,卡的画面也是真的)" : "⚠ 屏幕没动,读数作废(不是省,是没画)")}");
            Log($"[np-gpu]   同相位与「漂移已停」那张的差(参照): {Percent(ScreenCapture.DiffRatio(baseline, hold))}");
        }

        // 还原,并**再停一次漂移** —— 还原走的是应用门控,它会把生产那套重新起起来。
        refs.SetBackdropAblation(BackdropAblation.Default);
        refs.SetPageContent(true);
        refs.SetBackdropMotion(false);
    }

    private static string Describe(BackdropAblation mode) => mode switch
    {
        BackdropAblation.BitmapBlobs => "位图填充",
        BackdropAblation.GradientBlobs => "渐变填充",
        BackdropAblation.SolidBlobs => "渐变改纯色",
        BackdropAblation.HideIdleLayer => "藏闲置层",
        BackdropAblation.LayerCache => "整层缓存",
        BackdropAblation.ThreeBlobs => "减到3个",
        BackdropAblation.StopIdleMotion => "停闲置层动画",
        BackdropAblation.EllipseCache => "椭圆缓存",
        BackdropAblation.LayerTexture => "单纹理平移",
        BackdropAblation.TimedDrift33 => "定时器33ms(已否决)",
        BackdropAblation.TimedDrift60 => "漂移60fps",
        BackdropAblation.CompositorDrift => "组合动画(生产现状)",
        _ => "默认",
    };

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
        private BackdropAblation _backdropAblation;
        private bool _idleMotionStopped;
        private bool? _lyricCached;
        private readonly Dictionary<ListBoxItem, CacheMode?> _lyricCacheModes = new();
        // 色团的"原件":Fill / CacheMode 逐实例记,恢复时原样写回(同 _lyricEffects 的理由)。
        private readonly Dictionary<Ellipse, IBrush?> _ellipseFills = [];
        private readonly Dictionary<Ellipse, CacheMode?> _ellipseCacheModes = [];
        private readonly Dictionary<Canvas, CacheMode?> _layerCacheModes = [];
        private readonly Dictionary<Ellipse, IImageBrushSource> _blobBitmaps = [];
        private readonly Dictionary<Ellipse, Color> _blobColors = [];
        private Canvas? _pageCanvas;

        /// <summary>
        /// 「单纹理」那一档塞进树里的替身:每层一个 <c>Image</c> + 它占用的纹理。
        /// 摘的时候必须连纹理一起 <c>Dispose</c> —— 两张 1000×1000 就是 8MB,
        /// 留在 Skia 的资源缓存里会一直拖着后面每一行的读数。
        /// </summary>
        private readonly List<(Panel Parent, Image Image, RenderTargetBitmap Bitmap)> _layerTextures = [];

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
                    LyricBlurMode.AllWithZeroOnSelected => true,
                    LyricBlurMode.AllWithRealOnSelected => true,
                    // 样式里 above/below1/below2 就是"当前句上下各两行";更远的 Default 态
                    // 与它们只差在透明度(0.4 vs 0.6~0.8),模糊本来就是最弱的一档。
                    LyricBlurMode.Nearest => item.Classes.Contains("above")
                                             || item.Classes.Contains("below1")
                                             || item.Classes.Contains("below2"),
                    LyricBlurMode.ExceptSelected => !item.IsSelected,
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

                // 对照口径:把修复前那条 selected 样式(半径 0 的模糊)按原样补回去。
                // 以及配对口径:给当前句一个半径正常的模糊,用来分清"半径"与"这一行"。
                if (item.IsSelected && mode is LyricBlurMode.AllWithZeroOnSelected or LyricBlurMode.AllWithRealOnSelected)
                    item.Effect = new BlurEffect
                    {
                        Radius = mode == LyricBlurMode.AllWithZeroOnSelected ? 0 : 1.5,
                    };
            }

            _blurredItems = _lyricItems.Count(item => item.Effect is not null);
        }

        /// <summary>
        /// 套用色团层的某一档改动。**每次调用都先把 10 个椭圆恢复成应用自己的样子**
        /// (Fill / CacheMode / IsVisible / 层 CacheMode 全部按首次见面记下的原件写回),
        /// 再套本档的那一处改动 —— 只有这样才能保证"每个场景都是一份完整状态",
        /// 行与行相减时差里只剩被消融的那一项。
        /// </summary>
        public void SetBackdropAblation(BackdropAblation mode)
        {
            if (Backdrop is null) return;
            _backdropAblation = mode;

            // 上一档若是「单纹理」/「限频」,先把我塞进去的东西摘干净、把 10 个椭圆交还给应用。
            // 必须在套本档改动**之前**:否则本档的基线是"我的替身还在树里"的样子。
            RestoreMechanics();

            // 上一档若停过闲置层的动画,这里必须先让它们重新跑起来 —— 恢复**只能走应用自己的
            // 启动路径**(MotionEnabled 一关一开 ⇒ StopMotion + QueueMotionStart 会重建全部 10 个
            // 椭圆的动画)。照抄关键帧自己起一遍是下策:那样量到的就不是真跑的那套动画了。
            if (_idleMotionStopped)
            {
                Backdrop.MotionEnabled = false;
                Backdrop.MotionEnabled = true;
                _idleMotionStopped = false;
            }

            var layerA = Backdrop.FindControl<Canvas>(s_layerAName);
            var layerB = Backdrop.FindControl<Canvas>(s_layerBName);

            // ① 还原:层
            foreach (var layer in new[] { layerA, layerB })
            {
                if (layer is null) continue;
                if (!_layerCacheModes.ContainsKey(layer)) _layerCacheModes[layer] = layer.CacheMode;
                layer.CacheMode = _layerCacheModes[layer];
                layer.IsVisible = true;
            }

            // ② 还原:10 个椭圆。原件逐实例记(样式/代码给的是各自独立的实例,共用会串味)。
            var ellipses = s_ellipseNames
                .Select(name => Backdrop.FindControl<Ellipse>(name))
                .Where(ellipse => ellipse is not null)
                .Select(ellipse => ellipse!)
                .ToArray();

            foreach (var ellipse in ellipses)
            {
                if (!_ellipseFills.ContainsKey(ellipse))
                {
                    _ellipseFills[ellipse] = ellipse.Fill;
                    _ellipseCacheModes[ellipse] = ellipse.CacheMode;
                }
                ellipse.Fill = _ellipseFills[ellipse];
                ellipse.CacheMode = _ellipseCacheModes[ellipse];
                ellipse.IsVisible = true;
            }

            // ③ 套本档的改动
            switch (mode)
            {
                case BackdropAblation.EllipseCache:
                    foreach (var ellipse in ellipses) ellipse.CacheMode = new BitmapCache();
                    break;

                case BackdropAblation.LayerCache:
                    foreach (var layer in new[] { layerA, layerB })
                        if (layer is not null) layer.CacheMode = new BitmapCache();
                    break;

                case BackdropAblation.BitmapBlobs:
                    foreach (var ellipse in ellipses)
                        ellipse.Fill = new ImageBrush(BlobBitmap(ellipse)) { Stretch = Stretch.Fill };
                    break;

                case BackdropAblation.GradientBlobs:
                    foreach (var ellipse in ellipses)
                        ellipse.Fill = GradientBrush(BlobColor(ellipse));
                    break;

                case BackdropAblation.SolidBlobs:
                    foreach (var ellipse in ellipses)
                    {
                        var color = BlobColor(ellipse);
                        // 渐变中间那一档的 alpha,尽量贴近原来的平均亮度,免得把"变亮也变贵"混进来。
                        ellipse.Fill = new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B));
                    }
                    break;

                case BackdropAblation.HideIdleLayer:
                {
                    // 谁在显示:A 层 Opacity=1、B 层 0(CrossFade 只翻这两个值)。
                    var idleIsA = (layerA?.Opacity ?? 1) < (layerB?.Opacity ?? 0);
                    var idle = idleIsA ? layerA : layerB;
                    if (idle is not null) idle.IsVisible = false;
                    break;
                }

                case BackdropAblation.ThreeBlobs:
                    // 合并后的下标 i%5 就是"层内第几个",藏掉第 3、5 个。
                    for (var i = 0; i < ellipses.Length; i++)
                        if (i % 5 is 2 or 4) ellipses[i].IsVisible = false;
                    break;

                case BackdropAblation.StopIdleMotion:
                {
                    var idleIsA = (layerA?.Opacity ?? 1) < (layerB?.Opacity ?? 0);
                    var idle = idleIsA ? layerA : layerB;
                    var stopped = 0;
                    foreach (var ellipse in ellipses)
                    {
                        if (!ReferenceEquals(ellipse.Parent, idle)) continue;
                        var visual = ElementComposition.GetElementVisual(ellipse);
                        visual?.StopAnimation("Translation");
                        visual?.StopAnimation("Scale");
                        stopped++;
                    }
                    _idleMotionStopped = stopped > 0;
                    break;
                }

                case BackdropAblation.LayerTexture:
                    BuildLayerTextures();
                    break;

                case BackdropAblation.TimedDrift33:
                    StartProbeDrift(ProbeDriftMs, EllipseTargets());
                    break;

                case BackdropAblation.TimedDrift60:
                    StartProbeDrift(ProbeDrift60Ms, EllipseTargets());
                    break;

                case BackdropAblation.CompositorDrift:
                    StartCompositorDrift();
                    break;
            }
        }

        /// <summary>
        /// 详情页主画布(封面/标题/按钮/面板)画不画。背景层是它的**兄弟**,不受影响 ——
        /// 这正是"色团照漂移、页面不画"这个诊断态能成立的原因。
        /// </summary>
        public void SetPageContent(bool visible)
        {
            _pageCanvas ??= NowPlaying?.FindControl<Canvas>("DesktopCanvas");
            if (_pageCanvas is not null) _pageCanvas.IsVisible = visible;
        }

        // ─────────────────── 探针自己驱动的那几档 ───────────────────
        //
        // 它们跟其它消融档有一个本质区别:其它档只是"少画一点",这几档**替掉了应用的机制本身**
        // (换渲染载体 / 换驱动方式 / 换驱动频率)。所以:
        //   ① 进来时先把应用那套停干净 —— 两套驱动叠着跑,量的就不是任何一套(走 HaltAppDrift);
        //   ② 出去时必须把 10 个椭圆和应用自己的机制**原样还回去**(走 RestoreMechanics,
        //      且要还成"进来时门控的意愿",不能一律还成动);
        //   ③ 关键帧/缓动/轨迹与应用**同值**(见 s_driftPaths/s_slowEasing):这几档要改的是
        //      "怎么画/多久画一次/谁来推",不是"动成什么形状"。若连轨迹都不一样,
        //      量出来的差里就混进了"另一套动画"。

        /// <summary>
        /// 进来时应用门控的意愿。<c>MotionEnabled</c> 是**绑定**属性,这几档为了独占驱动会把它写 false,
        /// 还原时必须还成进来时那个值 —— 一律还 true 的话,"关动态背景"那一档就变成了在测
        /// 一个用户永远遇不到的态(应用明明要求不动,却被探针打开了)。
        /// 绑定会在下一行 ApplyAsync 里重新下发,所以行与行之间不会串。
        /// </summary>
        private bool? _motionGateAtEntry;

        /// <summary>
        /// 上一格的**状态校验**结果（只在 A/B 模式里会写）：标签要求的驱动状态与实际读回的
        /// 状态是否一致。不一致的行读数**不可信**（"标签说要动、其实没动"会伪装成"省下一大截"），
        /// 所以它跟帧节奏一起进报告体检。
        /// </summary>
        public bool LastStateOk { get; set; } = true;

        /// <summary>探针自己按固定间隔推的漂移:33ms 那一档是**复刻被否决的驱动**,"60fps"那档用 16ms。</summary>
        private DispatcherTimer? _probeTimer;
        private long _probeStartTicks;
        private int _probeTicks;
        private readonly List<Visual> _driftTargets = [];

        /// <summary>定时器驱动档的**实测**节拍。别假设"间隔设了 16ms 就有 60Hz" ——
        /// 实测把间隔从 33ms 改成 16ms,GPU 读数一点没变,真实频率只能数出来。</summary>
        public double ProbeTickRate => _probeStartTicks == 0
            ? double.NaN
            : _probeTicks * 1000.0 / Math.Max(1, Environment.TickCount64 - _probeStartTicks);

        /// <summary>累计节拍数。逐行取窗口前后的差,才能得到"这一行的节拍",而不是全程平均。</summary>
        public long ProbeTicks => _probeTicks;

        /// <summary>这一行是不是真由探针的定时器在推(组合动画那几档没有 tick 可数)。</summary>
        public bool ProbeDriving => _probeTimer is { IsEnabled: true };

        /// <summary>把应用的驱动停掉并记下它原本的意愿。**不能靠 <c>StopAnimation</c>** ——
        /// 生产已换成定时器,组合动画那套停不掉它了。</summary>
        private void HaltAppDrift()
        {
            if (Backdrop is null || _motionGateAtEntry is not null) return;
            _motionGateAtEntry = Backdrop.MotionEnabled;
            Backdrop.MotionEnabled = false;
        }

        private Visual[] EllipseTargets() => s_ellipseNames
            .Select(name => (Visual?)Backdrop?.FindControl<Ellipse>(name))
            .Where(visual => visual is not null)
            .Select(visual => visual!)
            .ToArray();

        /// <summary>
        /// 用定时器直接写 <c>visual.Translation/Scale</c> 驱动漂移。33ms 那一档复刻的是**被否决的**
        /// 驱动(生产已回退到组合动画),16ms 那档只为回答"名义间隔是不是杠杆"。
        /// 两档都必须报**数出来的**节拍 —— 别拿名义间隔当频率。
        /// </summary>
        private void StartProbeDrift(int intervalMs, IReadOnlyList<Visual> targets)
        {
            HaltAppDrift();

            _driftTargets.Clear();
            _driftTargets.AddRange(targets);
            foreach (var target in _driftTargets)
            {
                var visual = ElementComposition.GetElementVisual(target);
                if (visual is null) continue;
                visual.StopAnimation("Translation");
                visual.StopAnimation("Scale");
                visual.Translation = default;
                visual.Scale = Vector3.One;
                // 呼吸要绕椭圆自己的中心;替身 Image 不需要(没有 Scale 动画)。
                if (target is Ellipse)
                    visual.CenterPoint = new Vector3((float)(visual.Size.X / 2), (float)(visual.Size.Y / 2), 0);
            }

            _probeStartTicks = Environment.TickCount64;
            _probeTicks = 0;
            _probeTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(intervalMs),
                s_probePriority,
                (_, _) => TickProbeDrift());
            _probeTimer.Start();
            TickProbeDrift();
        }

        private void TickProbeDrift()
        {
            if (_driftTargets.Count == 0) return;
            _probeTicks++;
            var phase = (Environment.TickCount64 - _probeStartTicks) % (long)s_driftCycle.TotalMilliseconds
                        / (double)s_driftCycle.TotalMilliseconds;
            for (var i = 0; i < _driftTargets.Count; i++)
            {
                var visual = ElementComposition.GetElementVisual(_driftTargets[i]);
                if (visual is null) continue;
                var pathIndex = i % s_driftPaths.Length;
                visual.Translation = TranslationAt(s_driftPaths[pathIndex], phase);
                visual.Scale = BreathAt(pathIndex, phase);
            }
        }

        /// <summary>自证用:把 10 个椭圆交给探针定时器(与生产同一机制),再把相位推到指定位置。</summary>
        public void StartSelfCheckDrift() => StartProbeDrift(ProbeDriftMs, EllipseTargets());

        public void ForceProbeDriftPhase(double phase)
        {
            if (_probeTimer is null) return;
            _probeStartTicks = Environment.TickCount64 - (long)(phase * s_driftCycle.TotalMilliseconds);
            TickProbeDrift();
        }

        /// <summary>
        /// 复刻"换驱动之前的生产行为":10 个椭圆各起 Translation + Scale 两条关键帧动画,
        /// 由合成器按刷新率推帧(本机 144Hz 屏上 ≈145fps)。
        ///
        /// 生产代码里这条路已经删掉了,所以只能复刻 —— 复刻是**必要之恶**:
        /// 关键帧、缓动、轨迹、周期全部照抄原实现,唯一差别是"现在由探针起它"。
        /// 它现在的身份是**回归对**:默认(30fps)相对它省下的,就是那次改动的收益。
        /// </summary>
        private void StartCompositorDrift()
        {
            HaltAppDrift();

            var ellipses = EllipseTargets().OfType<Ellipse>().ToArray();
            for (var i = 0; i < ellipses.Length; i++)
            {
                var ellipse = ellipses[i];
                var visual = ElementComposition.GetElementVisual(ellipse);
                if (visual is null) continue;

                var pathIndex = i % s_driftPaths.Length;
                var path = s_driftPaths[pathIndex];
                visual.StopAnimation("Translation");
                visual.StopAnimation("Scale");
                visual.Translation = default;
                visual.Scale = Vector3.One;
                visual.CenterPoint = new Vector3(
                    (float)(ellipse.Bounds.Width / 2), (float)(ellipse.Bounds.Height / 2), 0);

                var drift = visual.Compositor.CreateVector3DKeyFrameAnimation();
                drift.Target = "Translation";
                drift.Duration = s_driftCycle;
                drift.IterationBehavior = AnimationIterationBehavior.Forever;
                drift.InsertKeyFrame(0, default);
                drift.InsertKeyFrame(0.48f, path.Hold, s_slowEasing);
                drift.InsertKeyFrame(0.62f, path.Burst, s_burstEasing);
                drift.InsertKeyFrame(0.82f, path.Drift, s_slowEasing);
                drift.InsertKeyFrame(1, default, s_slowEasing);
                visual.StartAnimation("Translation", drift);

                var breath = visual.Compositor.CreateVector3DKeyFrameAnimation();
                breath.Target = "Scale";
                breath.Duration = s_driftCycle;
                breath.IterationBehavior = AnimationIterationBehavior.Forever;
                breath.InsertKeyFrame(0, Vector3.One);
                breath.InsertKeyFrame(0.48f, new Vector3(1.02f, 1.015f, 1), s_slowEasing);
                breath.InsertKeyFrame(0.62f, new Vector3(1.055f + pathIndex * 0.004f, 1.04f, 1), s_burstEasing);
                breath.InsertKeyFrame(0.82f, new Vector3(0.985f, 1.025f, 1), s_slowEasing);
                breath.InsertKeyFrame(1, Vector3.One, s_slowEasing);
                visual.StartAnimation("Scale", breath);
            }
        }

        /// <summary>
        /// 把每层栅格化成一张 1000×1000 纹理,再用一个 <c>Image</c> 顶替 5 个椭圆,漂移改成
        /// **单视觉、纯平移**。这就是原版在 DWM 里的模型(固定画布 + 合成器缩放)能凑到的最近形态。
        ///
        /// 已判负(详见枚举注释与 perf-notes):比默认更贵 3.97%。留着是为了能重跑。
        /// </summary>
        private void BuildLayerTextures()
        {
            var images = new List<Visual>();
            foreach (var name in new[] { s_layerAName, s_layerBName })
            {
                if (Backdrop?.FindControl<Canvas>(name) is not { Parent: Panel parent } canvas) continue;

                StopEllipseMotion(canvas.Children.OfType<Ellipse>());

                // 纹理要按"层自己的样子"烘。**不能直接 Render 活体画布** —— 第一版这么写,
                // 实测烘出空图:活体那块的 Opacity(交叉淡化的状态)、合成视觉上的位移、
                // 离屏渲染器看得见哪一部分,全缠在一起,而且结果随"轮到哪一层"变
                // (同一份代码:先烘的那层恒 0% 覆盖、后烘的恒 100%,与它本身可不可见无关)。
                // 改成"新建一块离屏画布、把几何与填充抄过去"这条路本项目已有先例(SeedCover),
                // 且完全不依赖活体状态。
                var bitmap = BakeLayer(canvas);
                canvas.IsVisible = false;

                var image = new Image
                {
                    Source = bitmap,
                    Width = 1000,
                    Height = 1000,
                    Opacity = canvas.Opacity,
                    IsHitTestVisible = false,
                };
                parent.Children.Add(image);
                _layerTextures.Add((parent, image, bitmap));
                images.Add(image);

                // 纹理"烘空了"是这一档最阴的失败模式:画面上少了色团 ⇒ 读数天然变好看,
                // 会被误读成"这版更快"。所以每次烘完都报一下覆盖度,让它自己作证。
                Log($"[np-gpu]   · 纹理替身 {name}: 层Opacity={canvas.Opacity:F0} " +
                    $"非透明覆盖 {TextureCoverage(bitmap):P0}(0% 就是烘空了,该行作废)");
            }

            // 替身也用**生产同一套机制**(定时器)推 —— 否则这一档的差里会混进
            // "另一条更快/更慢的驱动",而不是"换了画法"。
            StartProbeDrift(ProbeDriftMs, images);
        }

        /// <summary>
        /// 把一层的 5 个椭圆抄到一块**离屏画布**上再栅格化。几何 / 填充 / 自身 Opacity 逐项抄,
        /// 但**层的 Opacity 不烘进纹理**(它是交叉淡化的状态,由 <c>Image</c> 自己带着)。
        /// </summary>
        private static RenderTargetBitmap BakeLayer(Canvas layer)
        {
            var offscreen = new Canvas { Width = 1000, Height = 1000 };
            foreach (var ellipse in layer.Children.OfType<Ellipse>())
            {
                var copy = new Ellipse
                {
                    Width = ellipse.Width,
                    Height = ellipse.Height,
                    Fill = ellipse.Fill,
                    Opacity = ellipse.Opacity,
                };
                Canvas.SetLeft(copy, Canvas.GetLeft(ellipse));
                Canvas.SetTop(copy, Canvas.GetTop(ellipse));
                offscreen.Children.Add(copy);
            }

            offscreen.Measure(new Size(1000, 1000));
            offscreen.Arrange(new Rect(0, 0, 1000, 1000));
            var bitmap = new RenderTargetBitmap(new PixelSize(1000, 1000), new Vector(96, 96));
            bitmap.Render(offscreen);
            return bitmap;
        }

        /// <summary>停掉一批椭圆自己的组合动画。**不能靠 <c>IsVisible=false</c>** ——
        /// 项目里踩过:已经跑起来的组合动画不会因为看不见而停。</summary>
        private static void StopEllipseMotion(IEnumerable<Ellipse> ellipses)
        {
            foreach (var ellipse in ellipses)
            {
                var visual = ElementComposition.GetElementVisual(ellipse);
                visual?.StopAnimation("Translation");
                visual?.StopAnimation("Scale");
            }
        }

        /// <summary>
        /// 把应用那 5 个关键帧(0 / 0.48 / 0.62 / 0.82 / 1)按同样缓动插值。
        /// 复刻这段是**必要之恶**:限频档要改的正是"谁在推它",轨迹必须还是应用那一条。
        /// </summary>
        private static Vector3 TranslationAt((Vector3 Hold, Vector3 Burst, Vector3 Drift) path, double phase)
        {
            var keys = new (double At, Vector3 Value, Easing? Ease)[]
            {
                (0, default, null),
                (0.48, path.Hold, s_slowEasing),
                (0.62, path.Burst, s_burstEasing),
                (0.82, path.Drift, s_slowEasing),
                (1, default, s_slowEasing),
            };
            return Lerp(keys, phase);
        }

        private static Vector3 BreathAt(int pathIndex, double phase)
        {
            var keys = new (double At, Vector3 Value, Easing? Ease)[]
            {
                (0, Vector3.One, null),
                (0.48, new Vector3(1.02f, 1.015f, 1), s_slowEasing),
                (0.62, new Vector3(1.055f + pathIndex * 0.004f, 1.04f, 1), s_burstEasing),
                (0.82, new Vector3(0.985f, 1.025f, 1), s_slowEasing),
                (1, Vector3.One, s_slowEasing),
            };
            return Lerp(keys, phase);
        }

        private static Vector3 Lerp((double At, Vector3 Value, Easing? Ease)[] keys, double phase)
        {
            phase = Math.Clamp(phase, 0, 1);
            for (var i = 1; i < keys.Length; i++)
            {
                if (phase > keys[i].At) continue;
                var span = keys[i].At - keys[i - 1].At;
                if (span <= 0) return keys[i].Value;
                var t = (phase - keys[i - 1].At) / span;
                if (keys[i].Ease is { } ease) t = ease.Ease(t);
                return Vector3.Lerp(keys[i - 1].Value, keys[i].Value, (float)t);
            }
            return keys[^1].Value;
        }

        /// <summary>纹理里"画上东西了吗"—— 抽样数非透明像素占比。</summary>
        private static double TextureCoverage(RenderTargetBitmap bitmap)
        {
            const int size = 1000;
            const int stride = size * 4;
            var buffer = Marshal.AllocHGlobal(stride * size);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, size, size), buffer, stride * size, stride);
                var opaque = 0;
                var total = 0;
                for (var y = 0; y < size; y += 25)
                for (var x = 0; x < size; x += 25)
                {
                    total++;
                    if (Marshal.ReadByte(buffer, y * stride + x * 4 + 3) > 8) opaque++;
                }
                return total == 0 ? 0 : (double)opaque / total;
            }
            catch
            {
                return double.NaN;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 把"探针自己驱动"那几档塞进来的东西全部摘干净,并把 10 个椭圆与应用自己的机制还回去。
        ///
        /// **幂等**:每一行都会调它,没塞过东西时只是把可能残留的组合动画清一遍(顺手做的卫生)。
        /// 重启应用那套只能走它的门控 —— 照抄关键帧自己起一遍是下策,那样后面每一行的基线
        /// 都不再是应用的真实行为。⚠ 还的是"进来时门控的意愿",不是一律还成"动"。
        /// </summary>
        private void RestoreMechanics()
        {
            _probeTimer?.Stop();
            _probeTimer = null;
            _driftTargets.Clear();

            foreach (var (parent, image, bitmap) in _layerTextures)
            {
                ElementComposition.GetElementVisual(image)?.StopAnimation("Translation");
                parent.Children.Remove(image);
                bitmap.Dispose();
            }
            _layerTextures.Clear();

            foreach (var name in new[] { s_layerAName, s_layerBName })
                if (Backdrop?.FindControl<Canvas>(name) is { } canvas) canvas.IsVisible = true;

            // **不能**在这里对椭圆 StopAnimation 收尾：生产又是**组合动画**驱动了，外部把动画
            // 掐掉时应用自己的"已启动"标志还停在 true ⇒ 再给它置一次 MotionEnabled=true 不会
            // 触发任何动作，漂移就永久死在那儿。实测代价：同一档"照应用的样子"在一轮里读出
            // 0.58% 与 8.51% 两种值（差 15 倍）。收尾只走**一次真转换**，
            // 与上面 _idleMotionStopped 的复位是同一个套路。
            if (_motionGateAtEntry is { } gate)
            {
                _motionGateAtEntry = null;
                // 进来时被写成了 false，所以这一次转换一定生效（StopMotion + QueueMotionStart
                // 会重建全部 10 个椭圆的动画）。还原成"进来时的意愿"，不一律还成动。
                if (Backdrop is not null)
                {
                    Backdrop.MotionEnabled = false;
                    if (gate) Backdrop.MotionEnabled = true;
                }
            }
        }

        /// <summary>应用自己的状态机认为漂移在跑吗。探针**只读**，不动手。</summary>
        public bool ProductionDriftRunning => Backdrop?.IsDriftRunning ?? false;

        /// <summary>
        /// 把"应用自己的漂移该不该跑"落实到位。**只在 A/B 夹具里用。**
        ///
        /// 纠正只能靠**一次真转换** <c>false→true</c>：应用的状态机只在属性**变化**时动作，
        /// 外部（或上一档）把动画掐掉之后它内部的"已启动"标志还停在 true，
        /// 再置一次 <c>true</c> 等于什么都没做 —— 漂移会永久死在那儿。
        /// 读一下 <see cref="ProductionDriftRunning"/> 就知道该不该叫它起来。
        /// </summary>
        public void EnsureProductionDrift(bool running)
        {
            if (Backdrop is null) return;

            if (!running)
            {
                // 要"停"时,光置 false 不够:上一格的重新点火是 **Loaded 优先级的异步 post**,
                // 它会落在这一格中间,表现成"期望停、实际动"(实测 r4p1 就这么坏的)。
                // 应用只在属性**变化**时动作,所以要制造两次变化 —— true→false 才会递增
                // _motionGeneration,而那个被 post 的启动回调开头就因代次不符退出。
                if (!Backdrop.MotionEnabled) Backdrop.MotionEnabled = true;
                Backdrop.MotionEnabled = false;
                return;
            }

            if (Backdrop.IsDriftRunning) return;
            Backdrop.MotionEnabled = false;
            Backdrop.MotionEnabled = true;
        }

        /// <summary>
        /// 椭圆原本的颜色,取一次就记下来。**不能**直接读 <c>Fill</c>:生产代码把渐变预渲染成
        /// 位图之后,颜色藏在那张纹理的中心像素里 —— 读不到就会静默退回默认色,
        /// 于是所有"改 Fill"的消融都变成"换成另一种默认色",量出来的全是假数。
        /// </summary>
        private Color BlobColor(Ellipse ellipse)
        {
            if (_blobColors.TryGetValue(ellipse, out var known)) return known;

            var fill = _ellipseFills.TryGetValue(ellipse, out var original) ? original : ellipse.Fill;
            var color = ReadFillColor(fill) ?? Colors.SlateBlue;
            _blobColors[ellipse] = color;
            return color;
        }

        private static Color? ReadFillColor(IBrush? fill) => fill switch
        {
            RadialGradientBrush { GradientStops.Count: > 0 } gradient => gradient.GradientStops[0].Color,
            ImageBrush { Source: RenderTargetBitmap texture } => ReadTextureCenter(texture),
            _ => null,
        };

        /// <summary>位图的中心像素 = 渐变的圆心色。存的是预乘 alpha,读回来要还原。</summary>
        private static Color? ReadTextureCenter(RenderTargetBitmap texture)
        {
            var size = texture.PixelSize;
            if (size.Width < 2 || size.Height < 2) return null;

            var buffer = Marshal.AllocHGlobal(4);
            try
            {
                texture.CopyPixels(new PixelRect(size.Width / 2, size.Height / 2, 1, 1), buffer, 4, 4);
                var b = Marshal.ReadByte(buffer);
                var g = Marshal.ReadByte(buffer, 1);
                var r = Marshal.ReadByte(buffer, 2);
                var a = Marshal.ReadByte(buffer, 3);
                if (a == 0) return null;
                byte Unpremultiply(byte channel) => (byte)Math.Min(255, channel * 255 / a);
                return Color.FromRgb(Unpremultiply(r), Unpremultiply(g), Unpremultiply(b));
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>改动前的画法:椭圆 + 径向渐变(alpha 218 → 142 → 0)。</summary>
        private static RadialGradientBrush GradientBrush(Color color) => new()
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(218, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(142, color.R, color.G, color.B), 0.52),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };

        /// <summary>
        /// 每个椭圆一张,按"原本那条渐变"逐像素预渲染成位图 —— 换成 <c>ImageBrush</c> 之后
        /// 每帧只是采样纹理,不再求值径向渐变。尺寸取 256:色团是柔和渐变,
        /// 放大到 1000+ 看不出来,而位图再大也不会更贵(采样代价与纹理尺寸无关)。
        /// </summary>
        private IImageBrushSource BlobBitmap(Ellipse ellipse)
        {
            if (_blobBitmaps.TryGetValue(ellipse, out var cached)) return cached;

            const int size = 256;
            var color = BlobColor(ellipse);
            var square = new Rectangle { Width = size, Height = size, Fill = GradientBrush(color) };
            square.Measure(new Size(size, size));
            square.Arrange(new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            bitmap.Render(square);

            _blobBitmaps[ellipse] = bitmap;
            return bitmap;
        }

        public BackdropAblation BackdropMode => _backdropAblation;

        /// <summary>
        /// 开关歌词行的位图缓存。行内容静止(只有滚动位置在变),缓存住的是**带模糊的结果** ——
        /// 每帧不再重算模糊,只是采样那张位图。抓屏验证过:开/关像素差 0.00%(视觉零变化),
        /// 而"缓存开 vs 无模糊"差 5.68% ⇒ 不是把 Effect 跳过了。
        /// </summary>
        public void SetLyricCache(bool? enabled)
        {
            _lyricItems = RealizedLyricItems();

            foreach (var item in _lyricItems)
            {
                // 首次见面时记下"样式给的值":样式里已经默认挂了 BitmapCache,
                // 直接置 null 会把样式的东西一起抹掉,那就量不到应用的真实状态了。
                if (!_lyricCacheModes.ContainsKey(item))
                    _lyricCacheModes[item] = item.CacheMode;

                item.CacheMode = enabled switch
                {
                    true => new BitmapCache(),
                    false => null,
                    _ => _lyricCacheModes[item],
                };
            }

            _lyricCached = enabled;
        }

        public bool? LyricCached => _lyricCached;

        /// <summary>
        /// 开关色团层的漂移。
        ///
        /// ⚠ 停/起都**只走应用的门控**，别去对椭圆 <c>StopAnimation</c>：
        /// 直接停动画会让应用的"已启动"标志与屏幕状态脱节（它以为在跑，其实动画没了），
        /// 而它只在属性**变化**时才动作 ⇒ 之后再也叫不起来。2026-09-21 那版定时器驱动
        /// 更是连 <c>StopAnimation</c> 都停不掉。
        /// </summary>
        public void SetBackdropMotion(bool running)
        {
            if (Backdrop is null) return;
            Backdrop.MotionEnabled = running;
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
                $"CPU={row.Cpu,6:F2}% 帧节奏={DescribeCadence(row.FrameIntervalMs)} 驱动={DescribeDriver(row.DriftRate)}");

        // 聚焦模式(拉丁方)的报告放在"详情页·默认"之前 —— 那个模式下根本没有那一行。
        ReportAbGroups(rows);

        var baseline = Find(rows, "详情页·默认");
        var drift = Find(rows, "详情页·默认(复测)");
        var home = Find(rows, "首页·对照");
        var lyricBase = Find(rows, "歌词·默认");

        if (baseline is null)
        {
            // 聚焦模式(backdrop-ab)里根本没有"详情页·默认"这一行 —— 该模式下报告已由
            // ReportAbGroups 出完,这里安静退出;真跑全场景却缺行才是要报警的情况。
            var focusOnly = rows.Count > 0 && rows.All(row => row.Label.StartsWith("AB·", StringComparison.Ordinal));
            if (!focusOnly) Log("[np-gpu] 判定失败: 没有详情页默认行");
            return;
        }

        Log("[np-gpu] ===== 消融:每一项相对「详情页·默认」的净代价 =====");
        Ablate(rows, baseline, "详情页·关动态背景设置", "色团**漂移动画**本身(用户关掉「动态背景」开关,走真实设置链路)");
        Ablate(rows, baseline, "详情页·减色团", "封面色团背景层(10 个全屏径向渐变椭圆)");
        Ablate(rows, baseline, "详情页·色团椭圆缓存", "色团椭圆各挂 BitmapCache(栅格化后漂移只移动位图)");
        Ablate(rows, baseline, "详情页·色团整层缓存", "色团层(Canvas)挂 BitmapCache(5 次全屏半透明混合 → 1 次)");
        Ablate(rows, baseline, "详情页·色团渐变换位图", "径向渐变 → 预渲染位图(ImageBrush),内容不变");
        Ablate(rows, baseline, "详情页·色团渐变改纯色", "渐变 → 同 alpha 纯色(归因下界:渐变 shader 值多少)");
        Ablate(rows, baseline, "详情页·藏掉闲置色团层", "CrossFade 里 Opacity=0 却仍在渲染树、仍在漂移的那一层");
        Ablate(rows, baseline, "详情页·色团减到3个", "每层藏掉 2 个色团(归因:每个色团的混合代价)");
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
            Ablate(rows, lyricBase, "歌词·去当前句模糊", "当前句那一行的 BlurEffect Radius=0(视觉上根本没有模糊)");
            Ablate(rows, lyricBase, "歌词·减模糊+减色团", "歌词模糊 + 封面色团层");
            Ablate(rows, lyricBase, "歌词·无行缓存", "歌词行的 BitmapCache(摘掉它 = 加缓存之前)");
            Ablate(rows, lyricBase, "歌词·关动态背景", "色团漂移(歌词那几行的模糊原样留着)");
            Log($"[np-gpu] 歌词面板本身的代价: 歌词·默认 {lyricBase.GpuMean:F2}% " +
                $"vs 详情页·默认 {baseline.GpuMean:F2}% ⇒ {lyricBase.GpuMean - baseline.GpuMean:+0.00;-0.00}%");

            // 干净口径:两边漂移都停,差里就只剩"歌词面板(含模糊)自己"。
            var lyricNoMotion = Find(rows, "歌词·关动态背景");
            var detailNoMotion = Find(rows, "详情页·关动态背景设置");
            if (lyricNoMotion is not null && detailNoMotion is not null)
                Log($"[np-gpu] 漂移全停时歌词面板净代价: {lyricNoMotion.GpuMean - detailNoMotion.GpuMean:+0.00;-0.00}% " +
                    $"(歌词·关动态背景 {lyricNoMotion.GpuMean:F2}% vs 详情页·关动态背景设置 {detailNoMotion.GpuMean:F2}%)");

            // 歌词面板这一项修复的验收:同轮里"修复前"与"修复后"两格只差当前句那一个 Effect。
            var lyricPreFix = Find(rows, "歌词·修复前(当前句Radius0)");
            if (lyricPreFix is not null)
                Log($"[np-gpu] 歌词修复验收: 修复前 {lyricPreFix.GpuMean:F2}% → 修复后 {lyricBase.GpuMean:F2}% " +
                    $"⇒ 省 {lyricPreFix.GpuMean - lyricBase.GpuMean:+0.00;-0.00}% GPU / " +
                    $"CPU {lyricPreFix.Cpu:F2}% → {lyricBase.Cpu:F2}%" +
                    $" (只去掉了当前句那条 BlurEffect Radius=0)");

            // 归因:当前句挂"半径 0"和挂"半径 1.5"哪个贵。两者都贵 ⇒ 问题在这一行(它在动),
            // 不在半径;前者贵得多 ⇒ 半径 0 走了特殊路径。这决定以后还能不能给当前句加效果。
            var lyricReal = Find(rows, "歌词·当前句真模糊(1.5)");
            if (lyricPreFix is not null && lyricReal is not null)
                Log($"[np-gpu] 当前句归因: 半径0 {lyricPreFix.GpuMean:F2}% vs 半径1.5 {lyricReal.GpuMean:F2}% " +
                    $"vs 无效果 {lyricBase.GpuMean:F2}% ⇒ " +
                    $"{(Math.Abs(lyricPreFix.GpuMean - lyricReal.GpuMean) < 1.0 ? "两者一样贵 ⇒ 贵在'这一行',与半径无关" : "半径 0 明显更贵 ⇒ 是半径 0 的问题")}");
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

    /// <summary>
    /// 拉丁方报告:每个变体给"均值 + 组内极差 + 逐行值",差值只对默认算。
    ///
    /// 为什么这里报均值而不是配对差:顺序已按拉丁方把位置偏差**严格抵消**掉(每个变体在 5 个位置上
    /// 各坐过一次),均值差就是纯净的效应量;而逐行值必须一起打出来,因为**极差比自由度更能说明
    /// 这个差能不能信** —— 组内自己就跳 2 个点,那"差 0.5"根本不是结论。
    /// 顺带做一次**帧节奏体检**:掉帧的行读数会凭空变好看,把它们当结论就会得出"某项改动大赚"的
    /// 假结论(实测踩过:整轮末尾掉到 13~14fps,那一档的读数从 12% 塌到 5%)。
    /// </summary>
    private static void ReportAbGroups(List<Row> rows)
    {
        var baseline = rows.Where(row => row.Label.StartsWith("AB·默认", StringComparison.Ordinal)).ToList();
        if (baseline.Count == 0) return;

        static double Spread(List<Row> group) => group.Max(row => row.GpuMean) - group.Min(row => row.GpuMean);
        static string Values(List<Row> group) => string.Join(" / ", group.Select(row => $"{row.GpuMean:F2}"));

        var baselineMean = baseline.Average(row => row.GpuMean);
        Log($"[np-gpu] ===== 拉丁方:{s_abVariants.Length} 变体 × {s_abVariants.Length} 位置(每变体在每个位置上各一次)=====");
        Log($"[np-gpu]   {"AB·默认",-14} {baselineMean,6:F2}% GPU(极差 {Spread(baseline):F2}) " +
            $"CPU {baseline.Average(row => row.Cpu):F2}% 驱动 {DescribeDriver(baseline.Average(row => row.DriftRate))}");

        foreach (var variant in s_abVariants.Skip(1))
        {
            var group = rows.Where(row => row.Label.StartsWith(variant.Label, StringComparison.Ordinal)).ToList();
            if (group.Count == 0) continue;
            var mean = group.Average(row => row.GpuMean);
            Log($"[np-gpu]   {variant.Label,-14} {mean,6:F2}% GPU(极差 {Spread(group):F2}) " +
                $"CPU {group.Average(row => row.Cpu):F2}% 驱动 {DescribeDriver(group.Average(row => row.DriftRate))} " +
                $"⇒ 相对默认 {mean - baselineMean:+0.00;-0.00}%");
            Log($"[np-gpu]       逐行 {Values(group)}");
        }

        // 状态体检优先于帧节奏体检:"标签说要动、其实没动"会把读数砸到地板,那是**夹具故障**,
        // 比掉帧更会伪造出"某项改动大赚"的结论(实测踩过:同一档读出 0.58% 与 8.51%)。
        var mismatched = rows.Where(row => !row.StateOk).ToList();
        Log(mismatched.Count == 0
            ? "[np-gpu]   状态体检:每一格的驱动状态都与标签一致"
            : $"[np-gpu]   ⚠ 状态体检:{mismatched.Count} 格的状态与标签不符,这些格读数作废 —— " +
              string.Join(" / ", mismatched.Select(row => row.Label)));

        var slow = rows.Where(row => !double.IsNaN(row.FrameIntervalMs) && row.FrameIntervalMs > 30).ToList();
        Log(slow.Count == 0
            ? "[np-gpu]   帧节奏体检:全部行 ≥30fps,读数可信"
            : $"[np-gpu]   ⚠ 帧节奏体检:{slow.Count} 行掉到 30fps 以下 —— " +
              $"⚠ 只对「UI 线程推帧」的档成立(组合动画由合成器推帧,掉帧不代表它变便宜),这些行要逐档判 —— " +
              string.Join(" / ", slow.Select(row => $"{row.Label}={row.FrameIntervalMs:F0}ms")));
    }

    private static Row? Find(List<Row> rows, string label)
    {
        foreach (var row in rows) if (row.Label == label) return row;
        return null;
    }

    private static string DescribeCadence(double intervalMs) =>
        double.IsNaN(intervalMs) || intervalMs <= 0 ? "未知" : $"{intervalMs:F1}ms({1000 / intervalMs:F0}fps)";

    /// <summary>
    /// 漂移驱动的实测节拍。<c>NaN</c> = 由合成器推帧(生产现状,没有 tick 可数,等价量看"帧节奏");
    /// 有值 = 探针复刻的 UI 线程定时器档,这个数就是它的上限(实测 12.5~14 次/秒)。
    /// </summary>
    private static string DescribeDriver(double rate) => double.IsNaN(rate)
        ? "合成器推帧"
        : $"{rate:F1}次/秒(UI线程定时器)";

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

    /// <summary>把 <c>ALY_NP_DRIFT_PRIORITY</c> 的文本映射成优先级;给不出映射就返回 null(保持默认)。</summary>
    private static DispatcherPriority? ParsePriority(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "render" or "7" => DispatcherPriority.Render,
        "normal" or "9" => DispatcherPriority.Normal,
        "input" or "5" => DispatcherPriority.Input,
        "loaded" or "6" => DispatcherPriority.Loaded,
        "background" or "4" => DispatcherPriority.Background,
        _ => null,
    };

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
        double FrameIntervalMs,
        /// <summary>本格窗口内漂移驱动的**实测更新次数/秒**。<c>NaN</c> = 该格由合成器推帧
        /// （生产现状与"关动态背景"那几档，没有 tick 可数，等价量看"帧节奏"）；
        /// 有值 = 探针复刻的 UI 线程定时器档，它 ≈ 这一格的整窗重绘次数。</summary>
        double DriftRate,
        /// <summary>本格"标签要求的驱动状态"与"读回的实际状态"是否一致（只 A/B 模式会判）。
        /// <c>false</c> 表示这一格的读数不可信，不是它省了钱。</summary>
        bool StateOk = true);
}
