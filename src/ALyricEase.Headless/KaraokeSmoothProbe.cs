using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Controls;
using ALyricEase.Models;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>
/// 逐字位置的"流畅度"回归(--karaoke-smooth):模拟真实播放链路,验证亮区不会因为
/// 采样抖动/回退而停顿或倒退。
///
/// <para>
/// 背景:桌面歌词被反馈"每句都卡一下、随机卡住"。根因有两条,都在
/// <see cref="KaraokeText"/> 里:① 每次采样(PositionMs 变化,5Hz)都把渲染位置硬重置成
/// 采样值,而采样本身有台阶、会回退 ⇒ 每 200ms 往回退一截;② 帧链靠
/// <c>RequestAnimationFrame</c> 自我续期,断一次就永久冻住,`_looping` 标志还会阻止重启。
/// </para>
///
/// <para>
/// 这个探针用 <see cref="AvaloniaHeadlessPlatform.ForceRenderTimerTick"/> 手动驱动 render tick,
/// 按 16ms 一帧推进,并每 200ms 喂一个**带 120~400ms 回退**的采样(真实播放器就是这么抖的),
/// 然后统计渲染位置序列:
/// 单调不减、每帧推进量接近真实间隔、没有长停滞。
/// </para>
/// </summary>
internal static class KaraokeSmoothProbe
{
    private const int FrameMs = 16;
    private const int DurationMs = 4000;
    private const int SampleIntervalMs = 200;

    public static int Run()
    {
        const string text = "深海中那点光在闪烁";
        var words = new List<LyricWord>();
        long t = 0;
        foreach (var ch in text)
        {
            words.Add(new LyricWord(ch.ToString(), t, t + 220));
            t += 220;
        }

        var line = new LyricLine(TimeSpan.Zero, text) { Words = words };
        var view = new LyricView { FontScale = 1, IsPageActive = true };
        view.Classes.Add("detail");

        var list = view.FindControl<ListBox>("LyricList")!;
        list.ItemsSource = new[] { line };

        var window = new Window
        {
            Width = 640,
            Height = 240,
            Background = Brushes.Black,
            Content = view,
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            view.UpdateLayout();

            if (list.ContainerFromIndex(0) is not ListBoxItem container)
                throw new InvalidOperationException("第 0 行容器未物化。");

            var karaoke = container.GetVisualDescendants().OfType<KaraokeText>().First();
            karaoke.IsPlaying = true;      // 允许跑帧循环(真实播放态)
            Dispatcher.UIThread.RunJobs();

            var samples = new List<double>(DurationMs / FrameMs + 8);
            var random = new Random(20261118);
            var clock = Stopwatch.StartNew();
            var nextSampleDue = 0.0;
            var sampleIndex = 0;
            var lastSample = 0L;
            var backwardSamples = 0;

            while (clock.ElapsedMilliseconds < DurationMs)
            {
                var nowMs = clock.Elapsed.TotalMilliseconds;

                // ---- 模拟播放器轮询:5Hz,且带回退噪声(每 4 拍有一次大的) ----
                if (nowMs >= nextSampleDue)
                {
                    var truth = (long)nowMs;
                    var jitter = sampleIndex % 4 == 3
                        ? -random.Next(120, 400)   // 台阶/噪声造成的回退
                        : random.Next(-40, 40);
                    var sampled = Math.Max(0, truth + jitter);
                    if (sampled < lastSample) backwardSamples++;
                    lastSample = sampled;

                    karaoke.PositionMs = sampled;
                    nextSampleDue += SampleIntervalMs;
                    sampleIndex++;
                }

                Thread.Sleep(FrameMs);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Dispatcher.UIThread.RunJobs();

                samples.Add(karaoke.RenderedPositionMsForProbe);
            }

            var elapsedMs = clock.Elapsed.TotalMilliseconds;
            var single = Report(samples, backwardSamples, karaoke, elapsedMs);
            var switching = RunLineSwitch();
            return single != 0 ? single : switching;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 切行段:每句结束换到下一行(IsActive 逐个切换),验证"换行"本身不会把亮区拽回去。
    /// 只比较**同一个控件**的相邻两帧 —— 跨控件比位置没有意义(各自有各自的渲染位置)。
    /// </summary>
    private static int RunLineSwitch()
    {
        const string template = "深海中那点光在闪烁";
        const int wordMs = 180;
        const int gapMs = 120;
        var lineMs = template.Length * wordMs;

        var stack = new Panel { Background = Brushes.Black };
        var inner = new StackPanel { Spacing = 6, Margin = new Thickness(12) };
        stack.Children.Add(inner);

        var controls = new List<KaraokeText>();
        for (var i = 0; i < 3; i++)
        {
            var words = new List<LyricWord>();
            long t = i * (lineMs + gapMs);
            foreach (var ch in template)
            {
                words.Add(new LyricWord(ch.ToString(), t, t + wordMs));
                t += wordMs;
            }

            var karaoke = new KaraokeText
            {
                Text = template,
                Words = words,
                FontSize = 24,
                AccentBrush = Brushes.White,
                DimBrush = new SolidColorBrush(Color.FromArgb(0x59, 255, 255, 255)),
                TextAlignment = TextAlignment.Left,
                IsPageActive = true,
            };
            controls.Add(karaoke);
            inner.Children.Add(karaoke);
        }

        var window = new Window { Width = 560, Height = 180, Background = Brushes.Black, Content = stack };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var clock = Stopwatch.StartNew();
            var nextSampleDue = 0.0;
            var current = -1;
            var lastInLine = double.NaN;
            var regressions = 0;
            var worst = 0.0;
            var frames = 0;
            var switches = 0;

            while (clock.Elapsed.TotalMilliseconds < 4200)
            {
                var nowMs = clock.Elapsed.TotalMilliseconds;
                if (nowMs >= nextSampleDue)
                {
                    var pos = (long)nowMs;
                    var idx = Math.Min(controls.Count - 1, (int)(pos / (lineMs + gapMs)));
                    if (idx != current)
                    {
                        if (current >= 0) controls[current].IsActive = false;
                        controls[idx].IsActive = true;
                        current = idx;
                        lastInLine = double.NaN; // 换控件了,不跨行比较
                        switches++;
                    }

                    foreach (var c in controls) c.PositionMs = pos;
                    nextSampleDue += SampleIntervalMs;
                }

                Thread.Sleep(FrameMs);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Dispatcher.UIThread.RunJobs();

                if (current < 0) continue;
                var rendered = controls[current].RenderedPositionMsForProbe;
                if (!double.IsNaN(lastInLine))
                {
                    frames++;
                    var d = rendered - lastInLine;
                    if (d < -0.001)
                    {
                        regressions++;
                        worst = Math.Min(worst, d);
                    }
                }

                lastInLine = rendered;
            }

            Console.WriteLine($"[smooth:switch] 切行 {switches} 次 / 同控件帧对 {frames} 对, " +
                              $"倒退={regressions}（最大 {worst:0.0}ms）");

            if (regressions == 0)
            {
                Console.WriteLine("[smooth:switch] PASS: 换行不会拖动亮区");
                return 0;
            }

            Console.WriteLine("[smooth:switch] FAIL: 换行把亮区拽回去了");
            return 1;
        }
        finally
        {
            window.Close();
        }
    }

    private static int Report(List<double> samples, int backwardSamples, KaraokeText karaoke, double elapsedMs)
    {
        Console.WriteLine($"[smooth] 帧数={samples.Count} 采样次数={samples.Count / 12} " +
                          $"其中回退采样={backwardSamples}");

        // 逐帧增量:负数=倒退,0=停顿
        var deltas = new List<double>(samples.Count);
        var regressions = 0;
        var stalls = 0;
        var worstBack = 0.0;
        for (var i = 1; i < samples.Count; i++)
        {
            var d = samples[i] - samples[i - 1];
            deltas.Add(d);
            if (d < -0.001)
            {
                regressions++;
                worstBack = Math.Min(worstBack, d);
            }
            else if (d < 0.001)
            {
                stalls++;
            }
        }

        var total = samples[^1] - samples[0];
        // 用真实经过时间算速率:Thread.Sleep(16) 在 Windows 上会被量化到 ~31ms,
        // 拿"帧数 × 16"当分母会算出 2x 的假结果。
        var speed = total / elapsedMs;

        var sorted = deltas.OrderBy(d => d).ToList();
        var p50 = sorted[sorted.Count / 2];
        var p90 = sorted[(int)(sorted.Count * 0.9)];
        var max = sorted[^1];

        Console.WriteLine($"[smooth] 渲染位置 {samples[0]:0} → {samples[^1]:0} " +
                          $"(共 {total:0}ms / 实际 {elapsedMs:0}ms,速率 {speed:0.000}x)");
        Console.WriteLine($"[smooth] 每帧推进 p50={p50:0.0} p90={p90:0.0} max={max:0.0} ms");
        Console.WriteLine($"[smooth] 倒退帧={regressions}（最大回退 {worstBack:0.0}ms） 停滞帧={stalls}");
        Console.WriteLine($"[smooth] 帧链仍在排队={karaoke.IsFramePendingForProbe}");

        var failures = new List<string>();
        if (regressions > 0) failures.Add($"亮区倒退 {regressions} 帧");
        if (stalls > samples.Count * 0.05) failures.Add($"停滞帧过多({stalls}/{samples.Count})");
        if (speed < 0.9 || speed > 1.1) failures.Add($"推进速率异常({speed:0.000}x)");
        if (!karaoke.IsFramePendingForProbe) failures.Add("帧链已断(没有下一帧在排队)");

        if (failures.Count == 0)
        {
            Console.WriteLine("[smooth] PASS: 采样抖动/回退不会造成亮区停顿或倒退");
            return 0;
        }

        Console.WriteLine("[smooth] FAIL: " + string.Join("；", failures));
        return 1;
    }
}
