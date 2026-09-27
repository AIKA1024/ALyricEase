using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>封面取色回归探针(--palette):验证 AlbumCoverBackground.ExtractPalette 的
/// 直接像素采样路径(Bitmap.CopyPixels,替代 Android 上不可靠的离屏 Image+RenderTargetBitmap)
/// 与"缺失槽位只在已提取色相家族内变体"的补色规则 —— 不得产出封面里没有的色相。
/// 背景: 真机(Android)曾频繁出现封面里没有的蓝紫色团,即上述两条回退路径所致。</summary>
internal static class PaletteProbe
{
    public static void Run()
    {
        var extract = typeof(AlbumCoverBackground).GetMethod("ExtractPalette",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (extract is null)
        {
            Console.WriteLine("[palette] FAIL: ExtractPalette not found");
            Environment.ExitCode = 1;
            return;
        }

        // 纯绿封面:所有色团必须绿色主导(旧 Shift 混固定蓝/紫会产出 B>R 的冷色,断言抓得住)
        var green = Extract(extract, (40, 200, 80));
        var greenOk = green.Length == 4 && green.All(IsDominant(ColorChannel.G));
        Console.WriteLine("[palette] green cover -> " + Describe(green) + " ok=" + greenOk);

        // 左红右蓝封面:必须同时出现红主导与蓝主导的色团,且不掺绿色主导
        var redBlue = Extract(extract, (220, 45, 60), (45, 60, 220));
        var redOk = redBlue.Any(IsDominant(ColorChannel.R));
        var blueOk = redBlue.Any(IsDominant(ColorChannel.B));
        var noGreen = redBlue.All(c => !IsDominant(ColorChannel.G)(c));
        var rbOk = redBlue.Length == 4 && redOk && blueOk && noGreen;
        Console.WriteLine("[palette] red/blue cover -> " + Describe(redBlue) + " ok=" + rbOk);

        Environment.ExitCode = greenOk && rbOk ? 0 : 1;
    }

    private static Color[] Extract(MethodInfo extract, params (byte R, byte G, byte B)[] halves)
    {
        const int size = 64;
        var stride = size * 4;
        var buffer = Marshal.AllocHGlobal(stride * size);
        try
        {
            unsafe
            {
                var p = (byte*)buffer;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var (r, g, b) = halves[Math.Min(x * halves.Length / size, halves.Length - 1)];
                    var o = y * stride + x * 4;
                    p[o] = b;
                    p[o + 1] = g;
                    p[o + 2] = r;
                    p[o + 3] = 255;
                }
            }

            using var bitmap = new Bitmap(
                PixelFormat.Bgra8888, AlphaFormat.Opaque, buffer,
                new PixelSize(size, size), new Vector(96, 96), stride);
            return (Color[])(extract.Invoke(null, [bitmap]) ??
                             throw new InvalidOperationException("extract returned null"));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private enum ColorChannel { R, G, B }

    private static Func<Color, bool> IsDominant(ColorChannel channel) => color =>
    {
        var (r, g, b) = (color.R, color.G, color.B);
        var max = Math.Max(r, Math.Max(g, b));
        return channel switch
        {
            ColorChannel.R => r == max,
            ColorChannel.G => g == max,
            _ => b == max,
        };
    };

    private static string Describe(Color[] colors) =>
        string.Join(", ", colors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
}
