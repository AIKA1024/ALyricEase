using System;
using System.Diagnostics;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>进度时间轴回归:喂一段**模拟器实测**的安卓位置序列,断言画出来的位置单调不减。
///
/// <para>
/// 起因(2026-10-28 用户反馈"安卓端进度条一直被扯回去,Windows 很顺滑"):安卓
/// <c>ExoPlayer.CurrentPosition</c> 是 ~250ms 一级的量化台阶,而 UI 每帧按"上一个样本 +
/// 墙钟"预测。原来的 <see cref="ProgressRenderAnimator"/> 收到样本就**直接渲染**,
/// 于是每 0.8 秒把球往回拽 50~200ms。Windows 的 <c>PlaybackSession.Position</c> 是插值过的连续值,
/// 同样的代码看不出问题。
/// </para>
/// <para>
/// 本探针同时跑两条模型:旧行为(样本直赋)必须**回退**,新时钟必须**单调不减**;
/// 再加上 seek / 暂停 / 恢复三种不连续场景,保证"不回退"没有把真正的跳转也吃掉。
/// </para>
/// </summary>
internal static class ProgressClockProbe
{
    private const double FrameMs = 1000d / 60d;

    // 模拟器实测(200ms 采样,位置以 ~253ms 为台阶,每 4 拍有一次落在同一级被去重跳过)。
    private static readonly (double T, double P)[] Recorded =
    {
        (0, 0), (79, 254), (434, 508), (638, 761), (838, 1014), (1039, 1016), (1240, 1268),
        (1441, 1520), (1643, 1773), (2044, 2027), (2245, 2279), (2445, 2532), (2648, 2785),
        (3048, 3038), (3248, 3291), (3448, 3544), (3652, 3798), (4053, 4051), (4253, 4304),
        (4453, 4557), (4656, 4809), (5057, 5064),
    };

    public static int Run()
    {
        try
        {
            var (oldWorst, oldCount) = SimulateOldBehaviour();
            var (clockWorst, clockCount, finalPosition, rateMin, rateMax) = SimulateClock();

            Console.WriteLine($"[progress-clock] 旧行为(样本直赋): 回退 {oldCount} 次, 最差 {oldWorst:F1}ms");
            Console.WriteLine($"[progress-clock] 新时钟: 回退 {clockCount} 次, 最差 {clockWorst:F1}ms, " +
                              $"终点 {finalPosition:F0}ms(末样本 {Recorded[^1].P:F0}ms), " +
                              $"速度范围 [{rateMin:F3}, {rateMax:F3}]");

            if (oldCount == 0 || oldWorst > -100)
                throw new InvalidOperationException(
                    $"这段实测数据本应复现「被扯回去」(旧行为回退 >100ms),实测 {oldCount} 次 / 最差 {oldWorst:F1}ms" +
                    " —— 样本序列或旧行为模型失效,回归失去意义。");

            if (clockCount != 0)
                throw new InvalidOperationException($"新时钟仍回退 {clockCount} 次(最差 {clockWorst:F1}ms)");

            var drift = Math.Abs(finalPosition - Recorded[^1].P);
            if (drift > 500)
                throw new InvalidOperationException($"新时钟终点偏离末样本 {drift:F0}ms(应 <500ms,台阶本身就有 ~250ms)");

            if (rateMin < 0.9 || rateMax > 1.1)
                throw new InvalidOperationException($"速度修正越界: [{rateMin:F3}, {rateMax:F3}]");

            CheckDiscontinuities();
            CheckPauseAndResume();

            Console.WriteLine("[progress-clock] PASS 播放中单调、seek/暂停/恢复仍即时对齐");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[progress-clock] FAIL {ex.Message}");
            return 1;
        }
    }

    /// <summary>旧实现:样本到手就当位置画,之后按墙钟预测 —— 每次拿到"上一级台阶"就回退一次。</summary>
    private static (double Worst, int Count) SimulateOldBehaviour()
    {
        var worst = 0d;
        var count = 0;
        var lastRendered = 0d;
        var sampleIndex = 0;
        double sampleP = 0, sampleT = 0;

        for (var t = 0d; t <= Recorded[^1].T; t += FrameMs)
        {
            while (sampleIndex < Recorded.Length && Recorded[sampleIndex].T <= t)
            {
                sampleP = Recorded[sampleIndex].P;
                sampleT = Recorded[sampleIndex].T;
                sampleIndex++;
            }

            var rendered = sampleP + Math.Max(0, t - sampleT);
            var delta = rendered - lastRendered;
            if (delta < 0)
            {
                count++;
                worst = Math.Min(worst, delta);
            }

            lastRendered = rendered;
        }

        return (worst, count);
    }

    private static (double Worst, int Count, double Final, double RateMin, double RateMax) SimulateClock()
    {
        var clock = new PlaybackProgressClock();
        var origin = Stopwatch.GetTimestamp();
        var worst = 0d;
        var count = 0;
        var lastRendered = clock.Sample(Recorded[0].P, running: true, At(origin, Recorded[0].T));
        var rateMin = double.MaxValue;
        var rateMax = double.MinValue;
        var sampleIndex = 1;

        for (var t = 0d; t <= Recorded[^1].T; t += FrameMs)
        {
            while (sampleIndex < Recorded.Length && Recorded[sampleIndex].T <= t)
            {
                var sample = Recorded[sampleIndex];
                lastRendered = clock.Sample(sample.P, running: true, At(origin, sample.T));
                rateMin = Math.Min(rateMin, clock.Rate);
                rateMax = Math.Max(rateMax, clock.Rate);
                sampleIndex++;
            }

            var rendered = clock.PositionAt(At(origin, t));
            var delta = rendered - lastRendered;
            if (delta < -0.001)
            {
                count++;
                worst = Math.Min(worst, delta);
            }

            lastRendered = rendered;
        }

        return (worst, count, lastRendered, rateMin, rateMax);
    }

    /// <summary>真正的不连续必须立刻对齐:向前 seek、向后 seek 都不该被"不回退"规则吞掉。</summary>
    private static void CheckDiscontinuities()
    {
        var clock = new PlaybackProgressClock();
        var origin = Stopwatch.GetTimestamp();
        clock.Sample(60_000, running: true, At(origin, 0));
        clock.Sample(60_200, running: true, At(origin, 200));

        var forward = clock.Sample(125_000, running: true, At(origin, 400));
        AssertNear(forward, 125_000, 1, "向前 seek 5s");

        var backward = clock.Sample(5_000, running: true, At(origin, 600));
        AssertNear(backward, 5_000, 1, "向后 seek 120s");

        // 切歌:ScrubPositionMs 先清零,再起播(与 VM 清 0 的行为一致)
        var switched = clock.Sample(0, running: true, At(origin, 800));
        AssertNear(switched, 0, 1, "切歌清零");
    }

    /// <summary>暂停期间不推进、不因为播放器微调位置而回退;恢复时对齐到新样本。</summary>
    private static void CheckPauseAndResume()
    {
        var clock = new PlaybackProgressClock();
        var origin = Stopwatch.GetTimestamp();
        clock.Sample(30_000, running: true, At(origin, 0));
        var paused = clock.Sample(30_400, running: false, At(origin, 400));
        AssertNear(paused, 30_400, 1, "暂停对齐");

        var afterOneSecond = clock.PositionAt(At(origin, 1_400));
        AssertNear(afterOneSecond, 30_400, 1, "暂停 1s 后不推进");

        // 暂停期间播放器把位置改了 150ms(解码器重排),不该被当成回跳把球拽回去
        var nudged = clock.Sample(30_250, running: false, At(origin, 1_500));
        AssertNear(nudged, 30_400, 1, "暂停期间忽略位置微调");

        // 但暂停里 seek 是真跳转(通知栏/歌词点击),必须立刻对齐
        var pausedSeek = clock.Sample(95_000, running: false, At(origin, 1_900));
        AssertNear(pausedSeek, 95_000, 1, "暂停中 seek");

        var resumed = clock.Sample(95_400, running: true, At(origin, 2_600));
        AssertNear(resumed, 95_400, 1, "恢复播放对齐");
    }

    private static long At(long origin, double milliseconds)
        => origin + (long)(milliseconds / 1000d * Stopwatch.Frequency);

    private static void AssertNear(double actual, double expected, double tolerance, string label)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{label}异常: actual={actual:F1}, expected={expected:F1}");
    }
}
