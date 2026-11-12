using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Diagnostics;

/// <summary>
/// Android 真机滚动 A/B:同一个 <see cref="ScrollViewer"/> 上交替切换
/// <see cref="VirtualizingStackPanel.CacheLength"/>,用同一份 rAF 驱动脚本滚同一段距离,
/// 比较帧间隔与分配量。
///
/// 存在的理由:Avalonia 的滚动是**布局驱动**的 —— <c>Offset</c> 变化 ⇒ <c>InvalidateArrange()</c>
/// ⇒ 下一帧整屏重新 Arrange(见 ScrollContentPresenter.OnPropertyChanged),不是合成层平移。
/// 所以 <c>CacheLength</c> 是笔交易:调大省掉"滚出视口就回收、滚回来重建 + 重绑 + 重取封面",
/// 但每帧要 Arrange 的元素数也翻倍。哪边赢没有任何理论解,只能在目标机上量。
///
/// 桌面那套 PRD/GPU 口径(<c>--home-scroll-gpu</c>)在安卓上取不到,这里改用 rAF 帧间隔:
/// UI 线程被 Arrange 占住时 rAF 回调会延后,**帧间隔就是这个线程的可用性**,与"画了什么"无关。
/// 每轮都从 Offset=0 起滚同一段距离、同一像素步长 ⇒ 帧数相同,用时差异即帧率差异。
///
/// 触发方式(须先手动进入有长列表的页面,探针在当前页面就地跑,不导航):
/// <code>
/// adb shell am start -n com.aika1024.alyricease/crc64ea7453dff8e094fe.MainActivity --es aly_probe scroll
/// adb logcat -d -s ALyricEaseProbe:I
/// </code>
/// activity 全名用 <c>adb shell cmd package resolve-activity --brief com.aika1024.alyricease</c> 取
/// (旧文档里的 <c>ALyricEase.MainActivity</c> 是 .NET 9 时代的名字,已失效)。
/// </summary>
internal static class DeviceScrollProbe
{
    internal const string IntentExtraKey = "aly_probe";
    internal const string IntentExtraValue = "scroll";

    private const string Tag = "ALyricEaseProbe";

    /// <summary>每帧位移(px)。固定像素步长 ⇒ 各轮帧数一致,总用时直接反映帧率。</summary>
    private const double StepPx = 14;

    /// <summary>一轮滚过的距离 = 视口高度 × 该倍数(并以可滚范围封顶)。</summary>
    private const double DistanceViewports = 6;

    /// <summary>每轮总位移的硬上限,避免超长列表把一轮拖到一分钟。</summary>
    private const double MaxDistancePx = 6000;

    /// <summary>切换 CacheLength 后等多久再开测:要等 InvalidateMeasure → Measure/Arrange 走完、
    /// 回收池状态稳定,否则测到的是上一档的残留。</summary>
    private const int SettleMs = 350;

    /// <summary>A/B 轮次。特意排成 0 → 0.5 → 0.5 → 0(前后对夹),
    /// 让发热降频与封面缓存预热这两条单调漂移在两组内互相抵消。</summary>
    private static readonly double[] Rounds = [0, 0.5, 0.5, 0];

    public static void Run() => Dispatcher.UIThread.Post(() => _ = RunAsync());

    private static async Task RunAsync()
    {
        try
        {
            Emit("===== 真机滚动 A/B (CacheLength) =====");

            var top = FindTopLevel();
            if (top is null)
            {
                Emit("失败: 拿不到 TopLevel(SingleView 尚未挂载?)");
                return;
            }

            var sv = FindScroller(top);
            if (sv is null)
            {
                Emit("失败: 当前页面没有可纵向滚动的 ScrollViewer");
                Emit("提示: 先手动进入曲目较多的歌单页/最近播放页,再发本 intent");
                return;
            }

            var vsp = sv.GetVisualDescendants().OfType<VirtualizingStackPanel>().FirstOrDefault();
            var page = sv.GetVisualAncestors().OfType<UserControl>().FirstOrDefault()?.GetType().Name ?? "?";

            var distance = Math.Min(Math.Min(sv.ScrollBarMaximum.Y, MaxDistancePx),
                Math.Max(sv.Viewport.Height, 1) * DistanceViewports);

            Emit($"page    : {page}  classes=[{string.Join(' ', sv.Classes)}]");
            Emit($"scroller: extent={Fmt(sv.Extent.Height)} viewport={Fmt(sv.Viewport.Height)} " +
                 $"maxY={Fmt(sv.ScrollBarMaximum.Y)} 每轮位移={Fmt(distance)}");
            Emit($"panel   : {vsp?.GetType().Name ?? "(无虚拟化面板)"} " +
                 $"CacheLength={vsp?.CacheLength.ToString("F2") ?? "-"}");

            if (vsp is null)
            {
                Emit("失败: 该 ScrollViewer 内没有 VirtualizingStackPanel,测不了 CacheLength");
                return;
            }

            if (distance < sv.Viewport.Height)
            {
                Emit("失败: 可滚距离不足一屏,先换个更长的列表");
                return;
            }

            await EmitBaselineAsync(top);

            var results = new List<RoundResult>(Rounds.Length);
            for (var i = 0; i < Rounds.Length; i++)
            {
                var round = await RunRoundAsync(top, sv, vsp, Rounds[i], distance, i + 1);
                results.Add(round);
                Emit(round.Describe());
            }

            EmitSummary(results);

            await EmitJitterAsync(top, sv, vsp);

            Emit("===== 自检结束 =====");
        }
        catch (Exception exception)
        {
            Emit("自检异常: " + exception);
        }
    }

    /// <summary>空转基线:只跑 rAF 不滚动。它给出"这台机器上 UI 线程的上限帧率",
    /// 每一轮的 fps 都要对着它读 —— 低于基线的那部分才是滚动本身的开销。</summary>
    private static async Task EmitBaselineAsync(TopLevel top)
    {
        var intervals = new List<double>(256);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        var last = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();

        void Tick(TimeSpan ts)
        {
            if (frames > 0) intervals.Add((ts - last).TotalMilliseconds);
            last = ts;
            if (++frames >= 120)
            {
                sw.Stop();
                done.TrySetResult();
                return;
            }

            top.RequestAnimationFrame(Tick);
        }

        top.RequestAnimationFrame(Tick);
        await done.Task;

        Emit("baseline: " + Stats.Describe("空转", frames, sw.Elapsed.TotalSeconds, intervals));
    }

    private static async Task<RoundResult> RunRoundAsync(
        TopLevel top, ScrollViewer sv, VirtualizingStackPanel vsp,
        double cacheLength, double distance, int index)
    {
        vsp.CacheLength = cacheLength;
        sv.Offset = new Vector(sv.Offset.X, 0);

        // 切档 + 回到顶部都要走完一次 Measure/Arrange 才算生效
        await Task.Delay(SettleMs);

        var intervals = new List<double>(512);
        var spikes = new List<Spike>(16);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allocBefore = GC.GetTotalAllocatedBytes();
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);

        var frames = 0;
        var y = 0.0;
        var last = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();

        void Tick(TimeSpan ts)
        {
            if (frames > 0)
            {
                var dt = (ts - last).TotalMilliseconds;
                intervals.Add(dt);
                // 尖峰带上位移位置:若集中在 60px 的整数倍,基本可以指向"新行实化/回收";
                // 若散布无规律,更像 GC 或解码。
                if (dt > 33.3) spikes.Add(new Spike(y, dt));
            }

            last = ts;
            frames++;

            // 虚拟化列表的 Extent 会随容器实化/回收变化,每帧重新封顶,避免空转
            var max = sv.ScrollBarMaximum.Y;
            y = Math.Min(y + StepPx, Math.Min(distance, max));

            if (y >= distance || y >= max)
            {
                sv.Offset = new Vector(sv.Offset.X, y);
                sw.Stop();
                done.TrySetResult();
                return;
            }

            sv.Offset = new Vector(sv.Offset.X, y);
            top.RequestAnimationFrame(Tick);
        }

        top.RequestAnimationFrame(Tick);
        await done.Task;

        var alloc = GC.GetTotalAllocatedBytes() - allocBefore;
        return new RoundResult(index, cacheLength, frames, sw.Elapsed.TotalSeconds, intervals,
            y, alloc, spikes,
            GC.CollectionCount(0) - gc0,
            GC.CollectionCount(1) - gc1,
            GC.CollectionCount(2) - gc2);
    }

    /// <summary>
    /// 判别实验:停在某处后只在 40px 内往返(小于行高 60 ⇒ 不跨行边界,不新建也不回收容器)。
    /// 与 baseline、与正常滚动的两点对照即可分离瓶颈:
    /// - jitter ≈ baseline ⇒ 每帧 Arrange 本身很便宜,滚动掉帧是"容器实化/回收"一次性付的;
    /// - jitter ≈ 滚动       ⇒ 每帧 Arrange 就是成本主体,只能靠减少每行元素数(精简模板)来治。
    /// </summary>
    private static async Task EmitJitterAsync(TopLevel top, ScrollViewer sv, VirtualizingStackPanel vsp)
    {
        const double baseY = 2000;
        const double amplitude = 40;
        const int frames = 336;

        if (sv.ScrollBarMaximum.Y < baseY + amplitude) return;

        vsp.CacheLength = 0;
        sv.Offset = new Vector(sv.Offset.X, baseY);
        await Task.Delay(SettleMs);

        var intervals = new List<double>(512);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var last = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();

        void Tick(TimeSpan ts)
        {
            if (count > 0) intervals.Add((ts - last).TotalMilliseconds);
            last = ts;
            count++;

            if (count >= frames)
            {
                sw.Stop();
                done.TrySetResult();
                return;
            }

            var y = baseY + amplitude * Math.Sin(count * 0.08);
            sv.Offset = new Vector(sv.Offset.X, y);
            top.RequestAnimationFrame(Tick);
        }

        top.RequestAnimationFrame(Tick);
        await done.Task;

        Emit("jitter  : 同已实化区域往返(不新建容器) " +
             Stats.Describe(string.Empty, count, sw.Elapsed.TotalSeconds, intervals));
    }

    private static void EmitSummary(List<RoundResult> results)
    {
        foreach (var group in results.GroupBy(r => r.CacheLength).OrderBy(g => g.Key))
        {
            var fps = group.Select(r => r.Fps).ToArray();
            var p95 = group.Select(r => Stats.Percentile(r.Intervals, 0.95)).ToArray();
            var alloc = group.Select(r => r.AllocatedBytes).ToArray();
            Emit($"summary : CacheLength={group.Key:F1} fps={string.Join("/", fps.Select(v => v.ToString("F1")))} " +
                 $"(均值 {fps.Average():F1}) p95={string.Join("/", p95.Select(v => v.ToString("F1")))} " +
                 $"分配/轮={string.Join("/", alloc.Select(Fmt))}");
        }

        if (results.Select(r => r.CacheLength).Distinct().Count() == 2)
        {
            var a = results.Where(r => r.CacheLength == 0).ToArray();
            var b = results.Where(r => r.CacheLength != 0).ToArray();
            if (a.Length > 0 && b.Length > 0)
            {
                var delta = b.Average(r => r.Fps) - a.Average(r => r.Fps);
                Emit($"compare : 0.5 相对 0 的帧率差 = {delta:+0.0;-0.0;0.0} fps " +
                     $"({(delta >= 0 ? "更顺" : "更卡")}, 阈值看 p95 与掉帧数)");
            }
        }
    }

    private static TopLevel? FindTopLevel()
    {
        // Android.App.Application 同名,必须写全名
        if (global::Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime { MainView: { } view })
            return TopLevel.GetTopLevel(view);

        return null;
    }

    /// <summary>选当前页面里"还能往下滚最多"的那个纵向 ScrollViewer —— 手机布局下页面级滚动
    /// 容器就是它,嵌套的横向区块(纵向 Disabled)自然被排除。</summary>
    private static ScrollViewer? FindScroller(TopLevel top)
        => top.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Where(sv => sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
            .Where(sv => sv.ScrollBarMaximum.Y > sv.Viewport.Height)
            .OrderByDescending(sv => sv.ScrollBarMaximum.Y)
            .FirstOrDefault();

    private static string Fmt(double value) => value >= 1024
        ? $"{value / 1024:F1}k"
        : value.ToString("F0");

    private static string Fmt(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):F1}MB"
        : $"{bytes / 1024.0:F0}KB";

    private static void Emit(string message) => global::Android.Util.Log.Info(Tag, message);

    /// <summary>一次超过两帧的停顿。Y 是发生时的滚动位移(px)。</summary>
    private readonly record struct Spike(double Y, double Ms);

    private readonly record struct RoundResult(
        int Index, double CacheLength, int Frames, double Seconds,
        List<double> Intervals, double Distance, long AllocatedBytes, List<Spike> Spikes,
        int Gen0, int Gen1, int Gen2)
    {
        public double Fps => Seconds > 0 ? Frames / Seconds : 0;

        public string Describe()
        {
            var text = new StringBuilder();
            text.Append($"round {Index} : CacheLength={CacheLength:F1} 位移={Distance:F0}px ");
            text.Append(Stats.Describe(string.Empty, Frames, Seconds, Intervals));
            text.Append($" 分配={Fmt(AllocatedBytes)} gc={Gen0}/{Gen1}/{Gen2}");
            if (Spikes.Count > 0)
            {
                var top = Spikes.OrderByDescending(s => s.Ms).Take(3);
                text.Append(" 尖峰: " + string.Join(" ", top.Select(s => $"{s.Ms:F0}ms@{s.Y:F0}px")));
            }

            return text.ToString();
        }
    }

    private static class Stats
    {
        public static string Describe(string label, int frames, double seconds, List<double> intervals)
        {
            var prefix = label.Length > 0 ? label + ": " : string.Empty;
            var fps = seconds > 0 ? frames / seconds : 0;
            if (intervals.Count == 0) return $"{prefix}{frames} 帧 / {seconds:F2}s = {fps:F1}fps";

            var sorted = intervals.ToArray();
            Array.Sort(sorted);
            var over33 = sorted.Count(v => v > 33.3);
            var over50 = sorted.Count(v => v > 50);
            return $"{prefix}{frames} 帧 / {seconds:F2}s = {fps:F1}fps " +
                   $"p50={Percentile(sorted, 0.5):F1} p95={Percentile(sorted, 0.95):F1} " +
                   $"max={sorted[^1]:F1} >33ms={over33} >50ms={over50}";
        }

        public static double Percentile(IReadOnlyList<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            var index = (int)Math.Round((sorted.Count - 1) * p, MidpointRounding.AwayFromZero);
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }
    }
}
