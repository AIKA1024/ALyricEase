using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace ALyricEase.Headless;

/// <summary>
/// 转圈是否真的在转 —— 真窗口 + **外部抓屏**验证(--spinner-motion-real)。
///
/// 为什么不能用截图/属性断言:合成动画跑在渲染线程,<c>CompositionVisual</c> 的值不写回 UI 线程,
/// <c>RenderTargetBitmap.Render()</c> 也拍不到动画结果(实测两帧完全一致)。唯一可信的观测是**屏幕上
/// 真实的像素** ⇒ 用 <see cref="ScreenCapture.GrabClient"/> 隔 150ms 连抓几帧,数变化像素。
///
/// 两件事一起验:
///   ① 加载中:相邻帧必须明显不同(弧线在转),且 <see cref="UiFramePacer"/> 持有者为 0
///      —— 说明没把 UI 线程从静息里拽起来;
///   ② 加载结束(IsActive=false):连抓两帧必须**逐像素相同** —— 说明门控真的停了,
///      而不是"隐藏了还在转"(Avalonia 对隐藏仍挂在树上的元素照旧推进动画时钟)。
/// </summary>
internal static class SpinnerMotionProbe
{
    private const int SampleGapMs = 150;
    private const int SampleCount = 6;

    public static async Task<int> RunAsync()
    {
        var spinner = new Ellipse
        {
            Width = 40,
            Height = 40,
            StrokeThickness = 4,
            Stroke = Brushes.Teal,
            StrokeDashArray = new AvaloniaList<double> { 8, 14 },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        spinner.Classes.Add("spinner");
        SpinnerRotation.SetIsActive(spinner, true);

        var window = new Window
        {
            Width = 240,
            Height = 160,
            Content = spinner,
            Background = Brushes.Black,
            // 抓屏抓的是屏幕上真实像素：必须置顶，否则会被用户正在用的窗口盖住
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        window.Activate();
        await Task.Delay(900); // 首帧 + 合成动画起播

        var frames = new List<ScreenCapture.Frame>();
        for (var i = 0; i < SampleCount; i++)
        {
            frames.Add(ScreenCapture.GrabClient(window));
            await Task.Delay(SampleGapMs);
        }

        var moving = 0;
        for (var i = 1; i < frames.Count; i++)
            if (ChangedPixels(frames[i - 1], frames[i]) > 10) moving++;

        // 抓帧本身要先可信:窗口必须真的在屏幕上、且画出了弧线(否则"0 变化"只是抓了个黑块)
        var ink = frames.Count > 0 ? InkPixels(frames[0]) : 0;
        var saved = frames.Count > 0 ? ScreenCapture.Save(frames[0], "spinner-motion-first") : "";
        Console.WriteLine($"[spinner-motion] 首帧 {frames.FirstOrDefault().Width}x{frames.FirstOrDefault().Height} " +
                          $"墨迹像素={ink} position={window.Position} topmost={window.Topmost} 存档={saved}");

        Console.WriteLine($"[spinner-motion] 旋转采样 {moving}/{frames.Count - 1} 帧有变化 " +
                          $"(单帧变化像素最多 {Enumerable.Range(1, frames.Count - 1).Max(i => ChangedPixels(frames[i - 1], frames[i]))})");
        Console.WriteLine($"[spinner-motion] pacer {UiFramePacer.Describe()}");
        var spinning = moving >= (frames.Count - 1) * 2 / 3;
        Console.WriteLine(spinning
            ? "[spinner-motion] PASS 合成动画在转"
            : "[spinner-motion] FAIL 合成动画没转(检查 AnchorPoint/StartAnimation 属性名)");

        // 停:门控必须真的停,不能只是藏起来
        SpinnerRotation.SetIsActive(spinner, false);
        await Task.Delay(250);
        var stoppedA = ScreenCapture.GrabClient(window);
        await Task.Delay(400);
        var stoppedB = ScreenCapture.GrabClient(window);
        var residual = ChangedPixels(stoppedA, stoppedB);
        Console.WriteLine(residual >= 0
            ? $"[spinner-motion] 停止后残留变化像素={residual}"
            : "[spinner-motion] 抓帧失败");
        var stopped = residual == 0;
        Console.WriteLine(stopped
            ? "[spinner-motion] PASS 加载结束后不再转"
            : "[spinner-motion] FAIL 停了还在转(门控没生效)");

        // —— 内容淡入:同一个窗口换成白块,IsVisible=false → true,连抓几帧看亮度是否渐变 ——
        var block = new Border { Background = Brushes.White, Width = 200, Height = 120 };
        block.IsVisible = false;
        FadeInWhenShown.SetIsEnabled(block, true);
        window.Content = block;
        await Task.Delay(200);

        block.IsVisible = true;
        var luma = new List<int>();
        for (var i = 0; i < 7; i++)
        {
            luma.Add(MeanLuma(ScreenCapture.GrabClient(window)));
            await Task.Delay(30);
        }

        var first = luma[0];
        var last = luma[^1];
        var middles = luma.Count(v => v > first + 8 && v < last - 8);
        Console.WriteLine($"[spinner-motion] 淡入亮度序列 [{string.Join(", ", luma)}] 中间值={middles} 个");
        var faded = last > first + 40 && middles >= 1;
        Console.WriteLine(faded
            ? "[spinner-motion] PASS 内容淡入是渐变(不是瞬切)"
            : "[spinner-motion] FAIL 没看到渐变(检查 FadeInWhenShown 是否触发)");

        window.Close();
        return spinning && stopped && faded ? 0 : 1;
    }

    /// <summary>抓帧平均亮度(每 4 像素抽样):淡入过程应当单调上升。</summary>
    private static int MeanLuma(ScreenCapture.Frame frame)
    {
        if (frame.IsEmpty) return -1;
        long sum = 0;
        var count = 0;
        for (var y = 0; y < frame.Height; y += 4)
        {
            var row = y * frame.Stride;
            for (var x = 0; x < frame.Width; x += 4)
            {
                var i = row + x * 4;
                sum += (frame.Bgra[i] + frame.Bgra[i + 1] + frame.Bgra[i + 2]) / 3;
                count++;
            }
        }
        return count == 0 ? -1 : (int)(sum / count);
    }

    /// <summary>非背景像素数(任一通道 &gt; 40):用来确认抓帧真的拍到了弧线,而不是一块纯黑。</summary>
    private static int InkPixels(ScreenCapture.Frame frame)
    {
        if (frame.IsEmpty) return 0;
        var ink = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            var row = y * frame.Stride;
            for (var x = 0; x < frame.Width; x++)
            {
                var i = row + x * 4;
                if (frame.Bgra[i] > 40 || frame.Bgra[i + 1] > 40 || frame.Bgra[i + 2] > 40) ink++;
            }
        }
        return ink;
    }

    /// <summary>两张同尺寸抓帧里"明显不同"的像素数(每通道阈值 8,滤掉压缩/抖动噪声)。任一张为空或尺寸不同返回 -1。</summary>
    private static int ChangedPixels(ScreenCapture.Frame a, ScreenCapture.Frame b)
    {
        if (a.IsEmpty || b.IsEmpty || a.Width != b.Width || a.Height != b.Height) return -1;
        var changed = 0;
        for (var y = 0; y < a.Height; y++)
        {
            var rowA = y * a.Stride;
            var rowB = y * b.Stride;
            for (var x = 0; x < a.Width; x++)
            {
                var ia = rowA + x * 4;
                var ib = rowB + x * 4;
                if (Math.Abs(a.Bgra[ia] - b.Bgra[ib]) > 8
                    || Math.Abs(a.Bgra[ia + 1] - b.Bgra[ib + 1]) > 8
                    || Math.Abs(a.Bgra[ia + 2] - b.Bgra[ib + 2]) > 8)
                    changed++;
            }
        }
        return changed;
    }
}
