using System;
using System.Diagnostics;

namespace ALyricEase.Views;

/// <summary>
/// 把播放器上报的离散进度样本(安卓端实测是 ~250ms 一级的**量化台阶**)变成一条
/// **连续、且播放中绝不回退**的视觉时间轴。
///
/// <para>
/// 为什么不能收到样本就直接当位置用:进度条每帧按"上次样本 + 墙钟经过时间"预测,
/// 而安卓的 <c>ExoPlayer.CurrentPosition</c> 只在音频硬件报上新位置时才跳一级
/// (2026-10-28 模拟器实测:253ms 一级,每 800ms 有一拍落在同一级、被去重逻辑跳过)。
/// 于是每来一个样本,预测值都比样本**超前 50~200ms** —— 直接赋值就是"每 0.8 秒把球往回拽 200ms",
/// 也就是用户说的"安卓进度条一直被扯回去"。Windows 侧 <c>PlaybackSession.Position</c>
/// 是插值过的连续值,同样的代码看不出问题,差别只在样本质量。
/// </para>
///
/// <para>
/// 做法:样本只当"观测值"。每次采样算出 <c>误差 = 样本 − 预测</c>:
/// <list type="bullet">
/// <item>误差很大 ⇒ 真的不连续(seek / 切歌 / 循环重播),直接对齐;</item>
/// <item>误差很小 ⇒ 用 ±8% 以内的速度偏移把误差慢慢吃掉,视觉上**永不回跳**,
/// 也不会像"死区直接丢弃"那样把偏差永久攒着。</item>
/// </list>
/// 播放中位置因此单调不减(速度下限 0.92)，而长期速率仍等于播放器的真实速率。
/// </para>
///
/// 纯逻辑、不碰 UI，回归见 <c>ALyricEase.Headless --progressclock</c>。
/// </summary>
internal sealed class PlaybackProgressClock
{
    /// <summary>样本与预测差这么多以上就是不连续(seek / 换歌 / 缓冲后跳位),直接对齐而不是滑过去。</summary>
    internal const double DiscontinuityMs = 900;

    /// <summary>误差按这个时间窗口追平:误差 e ⇒ 视觉速度 1 + e/900。</summary>
    private const double SlewWindowMs = 900;

    /// <summary>视觉速度相对 1x 的最大偏移。8% 在进度条上肉眼不可见,但足以吃掉采样抖动。</summary>
    private const double MaxRateDelta = 0.08;

    private double _anchorMs;
    private long _anchorTimestamp;
    private double _rate = 1;
    private bool _running;
    private bool _primed;

    /// <summary>当前视觉速度(1 = 与墙钟同速)。仅供诊断/回归断言。</summary>
    internal double Rate => _rate;

    /// <summary>时间轴是否处于推进状态(播放且未拖动)。</summary>
    internal bool IsRunning => _running;

    /// <summary>某个时刻的预测位置(未按时长截断)。</summary>
    internal double PositionAt(long timestamp)
    {
        if (!_running) return _anchorMs;
        var elapsed = Stopwatch.GetElapsedTime(_anchorTimestamp, timestamp).TotalMilliseconds;
        return _anchorMs + Math.Max(0, elapsed) * _rate;
    }

    /// <summary>接收一个权威进度样本，返回这一刻应该画的位置。</summary>
    /// <param name="sampleMs">播放器上报的位置(毫秒)。</param>
    /// <param name="running">播放器是否在推进(暂停 / 拖动时为 false)。</param>
    /// <param name="timestamp">采样时刻(<see cref="Stopwatch.GetTimestamp"/>)。</param>
    internal double Sample(double sampleMs, bool running, long timestamp)
    {
        if (!double.IsFinite(sampleMs))
            return _anchorMs;

        if (!_primed)
        {
            Align(sampleMs, running, timestamp);
            return _anchorMs;
        }

        if (!running)
        {
            // 暂停:播放器可能把位置微调几十毫秒(解码器重排),不值得把球拽回去;
            // 但暂停中 seek 是真跳转(幅度远大于台阶),必须立刻对齐。
            if (_running || Math.Abs(sampleMs - _anchorMs) >= DiscontinuityMs)
                Align(sampleMs, false, timestamp);
            return _anchorMs;
        }

        if (!_running)
        {
            // 恢复播放 / 拖动结束:上一段基线已经不可信,重新起一条。
            Align(sampleMs, true, timestamp);
            return _anchorMs;
        }

        var predicted = PositionAt(timestamp);
        var error = sampleMs - predicted;
        if (Math.Abs(error) >= DiscontinuityMs)
        {
            Align(sampleMs, true, timestamp);
            return _anchorMs;
        }

        // 保持视觉连续:锚点取当前预测值(不回退),只改速度把误差关掉。
        _rate = 1 + Math.Clamp(error / SlewWindowMs, -MaxRateDelta, MaxRateDelta);
        _anchorMs = predicted;
        _anchorTimestamp = timestamp;
        return predicted;
    }

    /// <summary>直接定位(拖动进度条):立刻摆到该位置并停止推进。</summary>
    internal void SetPosition(double positionMs, long timestamp)
    {
        if (double.IsFinite(positionMs)) Align(positionMs, false, timestamp);
    }

    /// <summary>把锚点挪到当前预测值上，速度与推进状态保持不变。</summary>
    /// <remarks>帧循环重启(重新可见/重新激活)时用:不能重置成旧样本，否则会退回到样本位置。</remarks>
    internal void Rebase(long timestamp)
    {
        if (_primed && _running) _anchorMs = PositionAt(timestamp);
        _anchorTimestamp = timestamp;
    }

    /// <summary>离开视觉树:下一次样本重新建立基线。</summary>
    internal void Reset()
    {
        _anchorMs = 0;
        _anchorTimestamp = 0;
        _rate = 1;
        _running = false;
        _primed = false;
    }

    private void Align(double positionMs, bool running, long timestamp)
    {
        _anchorMs = positionMs;
        _anchorTimestamp = timestamp;
        _rate = 1;
        _running = running;
        _primed = true;
    }
}
