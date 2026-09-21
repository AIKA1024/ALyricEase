using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 给 UI 线程按 ~60Hz 投一个空作业的"帧泵"。
///
/// <para>
/// **为什么需要它(实测结论,别当成迷信)** —— 起因是用户反馈"进度条动的卡卡的,但如果鼠标
/// 一直移动就会变顺"。用 <c>--progress-cadence-real</c> 探针把这条反馈量成了数字:
/// 静息时进度条**每秒只真正写入视觉 8.7 次**、帧间隔 p90 79ms、8 秒里 101 次 ≥50ms 的停顿;
/// 鼠标一灌就变成 30~34 次/秒、p90 6.6ms。
/// </para>
///
/// <para>
/// 逐条排除后,根因收敛到"**UI 线程静息时长睡,而 WM_TIMER 唤不醒它**":
/// </para>
/// <list type="bullet">
/// <item>从后台线程 <c>Dispatcher.UIThread.Post</c> 一个空作业,到它被执行只花 **0.1ms**
///   ⇒ 消息泵本身没坏、UI 线程也不是在忙。</item>
/// <item>可三个不同优先级(<c>Send</c>/<c>Render</c>/<c>Background</c>)的 100ms
///   <see cref="DispatcherTimer"/> 静息时**同样晚 56ms**(准点率与优先级无关)</item>
/// <item>渲染帧请求同样要等 ~79ms ⇒ 跟 <see cref="DispatcherTimer"/> 在等**同一次唤醒**。</item>
/// <item>**只要外部投递一到,帧立刻出来,DispatcherTimer 也立刻变准**(56ms → 11ms)。
///   ⇒ 唯一能可靠打断这次等待的是"投递的消息",而 <c>WM_TIMER</c> 不是。</item>
/// <item><c>timeBeginPeriod(1)</c>(进程本来就是 1ms,抬到 0.5ms 也一样)与关掉电源节流
///   (EcoQoS)都对它**毫无影响** ⇒ 与定时器分辨率、电源管理无关,别再往这两个方向查。</item>
/// </list>
///
/// <para>
/// 也**不是**我们自己的代码在拖累:同一个探针里挂了一个"什么都不做"的裸
/// <c>RequestAnimationFrame</c> 循环,它的间隔分布与进度条动画器逐位相同。
/// </para>
///
/// <para>
/// 实测收益(同轮配对,8 秒/相位):写入 **8.7 → 32~42 次/秒**,帧间隔 p90 **79ms → 30ms**,
/// ≥50ms 停顿 **101 → 0 次**,最坏间隔 93ms → 33ms。
/// </para>
///
/// <para>
/// ⚠ **按需开火**(<see cref="Acquire"/>/<see cref="Release"/> 改 <see cref="_wanted"/>):
/// 开着时 UI 线程不再进入长睡,空闲占用会略升,所以只有"确实有东西要按帧动"时才置位。
/// **线程本身只起一次、之后一直活着** —— 早前按持有数起停线程的写法在
/// "循环重启 = 先 let go 再 acquire"的路径上会反复 Stop/Start,既漏线程又可能把自己停死。
/// 代价只是一个每 16ms 醒一次的后台线程(空闲时 ≈0% CPU),换来彻底无状态。
/// </para>
///
/// <para>
/// 诊断旋钮:<c>ALY_NO_PACER=1</c> 关掉泵,用来做"有泵 / 无泵"的同口径对照;
/// <see cref="Describe"/> 把内部状态与投递计数报给探针(别再靠读数字猜它有没有在跑)。
/// </para>
/// </summary>
internal static class UiFramePacer
{
    /// <summary>投递周期。60FPS 的帧预算就是 16.7ms,取 16ms 略快一点足以覆盖睡眠误差。</summary>
    private const int IntervalMs = 16;

    /// <summary>小于这个余量就不睡,避免把 0.4ms 的余量睡成 1ms 而长期偏慢。</summary>
    private const double MinSleepMs = 0.5;

    private static readonly object Gate = new();
    private static readonly bool Disabled = Environment.GetEnvironmentVariable("ALY_NO_PACER") == "1";

    private static volatile bool _wanted;
    private static volatile bool _threadStarted;
    private static int _demand;
    private static long _posts;

    /// <summary>按帧动的东西 +1。第一次调用把泵线程起起来(只起一次)。</summary>
    public static void Acquire()
    {
        if (Disabled) return;
        bool start;
        lock (Gate)
        {
            // ⚠ 必须是**计数**,不能是单个布尔:应用里同时有两个动画器(底部条 + 详情页),
            // 各自独立持有/归还。用布尔的话任意一个归还都会把另一个也关掉 ——
            // 实测症状就是泵跑两三秒后停死(累计投递冻结),而读数看起来"泵完全没用"。
            _demand++;
            _wanted = true;
            start = !_threadStarted;
            if (start) _threadStarted = true;
        }

        if (start)
            new Thread(Loop) { IsBackground = true, Name = "ALyricEase.UiFramePacer" }.Start();
    }

    /// <summary>按帧动的东西 -1。最后一个归还者才停投递(线程留着,之后只睡不投)。</summary>
    public static void Release()
    {
        if (Disabled) return;
        lock (Gate)
        {
            if (_demand > 0) _demand--;
            _wanted = _demand > 0;
        }
    }

    /// <summary>只给探针读的内部状态。数字不对时先看这里,别去猜读数含义
    /// (第一次接线的 bug 就是靠这行抓到的:wanted 被提前打成 False)。</summary>
    internal static string Describe()
        => Disabled ? "已禁用(ALY_NO_PACER=1)"
            : $"持有者={_demand} wanted={_wanted} 线程已起={_threadStarted} 累计投递={Interlocked.Read(ref _posts)}";

    /// <summary>
    /// 按 <see cref="Stopwatch"/> 累积时刻表投递,而不是"睡固定时长" ——
    /// 后者会把每次睡眠的量化误差累加成持续偏慢(整首歌下来进度条会明显滞后)。
    /// 没人要的时候只睡不投,不产生任何跨线程消息。
    /// </summary>
    private static void Loop()
    {
        var step = (long)(IntervalMs / 1000.0 * Stopwatch.Frequency);
        var next = Stopwatch.GetTimestamp();
        while (true)
        {
            next += step;
            var remainingMs = (next - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > MinSleepMs) Thread.Sleep((int)Math.Ceiling(remainingMs));

            if (!_wanted) continue;
            // 空作业就够:作用是打断 UI 线程的长睡,让它把已排队的渲染请求/WM_TIMER 作业一次性做掉。
            Dispatcher.UIThread.Post(static () => { }, DispatcherPriority.Render);
            Interlocked.Increment(ref _posts);
        }
    }
}
