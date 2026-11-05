using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ALyricEase.Headless;

/// <summary>
/// 淡入的**时序**是否干净 —— 真窗口 + 外部抓屏（--fade-timing-real）。
///
/// 只断言"开始=0、结束=1"是不够的：真正的缺陷是**先渲染一帧全亮、再跳回 0 淡入**
/// （<see cref="FadeInWhenShown"/> 把压基值排到 Render 优先级，若该作业晚于这次渲染，
/// 就会闪一帧）。用户看到的就是"还是一下子出来，有过渡但很奇怪"。
///
/// 做法：结构与用户页一致（Panel &gt; ScrollViewer &gt; 内容根），内容 = 白色块，
/// 窗口背景纯黑 ⇒ 平均亮度就是内容的不透明度。切换 IsVisible 后每 ~10ms 抓一帧，
/// 判定：① 首帧亮度不能接近终值（否则=瞬切）② 序列不能出现"先冲高再掉下来"（否则=闪帧）。
/// </summary>
internal static class FadeTimingProbe
{
    public static async Task<int> RunAsync()
    {
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new Border { Background = Brushes.White, Height = 170 },
                new Border { Background = Brushes.White, Height = 110 },
            },
        };
        FadeInWhenShown.SetIsEnabled(content, true);
        content.IsVisible = false;

        var layout = new Panel
        {
            Children =
            {
                new ScrollViewer { Content = content },
            },
        };

        var window = new Window
        {
            Width = 420,
            Height = 280,
            Content = layout,
            Background = Brushes.Black,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();
        window.Activate();
        await Task.Delay(700); // 首帧稳定

        var baseline = MeanLuma(ScreenCapture.GrabClient(window));
        if (baseline > 10)
        {
            // 抓屏抓的是桌面像素:窗口被别的窗口挡住/在别的虚拟桌面时会抓到别人的画面,
            // 序列毫无意义。宁可标记"环境不可用",也不要给出误导性的 FAIL。
            Console.WriteLine($"[fade-timing] SKIP 抓屏不可用(基线={baseline},窗口被遮挡?)");
            window.Close();
            return 2;
        }
        content.IsVisible = true;

        var luma = new List<int>();
        for (var i = 0; i < 26; i++)
        {
            luma.Add(MeanLuma(ScreenCapture.GrabClient(window)));
            await Task.Delay(10);
        }

        var last = luma.Max();
        var first = luma[0];

        // 可信度门槛:内容整块可见时白块应接近 246。达不到就说明窗口被部分遮挡
        // (BitBlt 抓桌面像素),此时任何"回落"都可能是别人的窗口,不能当缺陷判。
        if (last < 200)
        {
            Console.WriteLine($"[fade-timing] SKIP 抓屏不完整(峰值={last},窗口被部分遮挡?)");
            window.Close();
            return 2;
        }

        // "先冲高再掉下来" = 闪帧：某帧明显高于后续帧
        var dipAfterRise = false;
        for (var i = 1; i < luma.Count - 1; i++)
            if (luma[i] > first + 25 && luma[i] - luma[i + 1] > 20 && luma[i + 1] < last * 0.85)
                dipAfterRise = true;

        Console.WriteLine($"[fade-timing] 基线={baseline} 序列 [{string.Join(", ", luma)}]");
        Console.WriteLine($"[fade-timing] 首帧={first} 峰值={last} 相对首帧={Pct(first, last)}%");

        var instant = first >= last * 0.85;
        Console.WriteLine(instant
            ? "[fade-timing] FAIL 首帧就接近终值(瞬切/压 0 太晚)"
            : "[fade-timing] PASS 首帧仍在透明端");
        Console.WriteLine(dipAfterRise
            ? "[fade-timing] FAIL 序列先冲高再回落(闪了一帧全亮)"
            : "[fade-timing] PASS 亮度单调上升");

        var ok = !instant && !dipAfterRise;
        Console.WriteLine(ok
            ? "[fade-timing] PASS 淡入时序干净"
            : "[fade-timing] FAIL 淡入时序有问题");

        window.Close();
        return ok ? 0 : 1;
    }

    private static int Pct(int value, int max) => max <= 0 ? 0 : (int)Math.Round(100.0 * value / max);

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

}
