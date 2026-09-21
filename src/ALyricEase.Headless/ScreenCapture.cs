using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace ALyricEase.Headless;

/// <summary>
/// 抓"合成器真正呈现出来的那一帧"(GDI BitBlt),以及 BGRA 逐像素比对。
///
/// 与 <c>RenderTargetBitmap</c> 的分工必须分清:
/// - RTB 是"按当前可视树**重画一遍**",用来验布局与内容;
/// - 抓屏拿的是**屏幕上实际显示的画面**,用来验"某个视觉特性到底有没有画出来"。
///   两者会不一致(比如合成器上还挂着上一帧、或某层根本没生效)——
///   要证明"某条 Effect 是白付钱",只有抓屏能作证。
///
/// 使用前务必让窗口**可见且没被遮挡**:BitBlt 抓的是桌面像素,窗口被挡住就抓到别人。
/// </summary>
internal static class ScreenCapture
{
    /// <summary>一帧客户端区域像素:BGRA、top-down、行距 Stride(可能大于 Width*4)。</summary>
    public readonly record struct Frame(byte[] Bgra, int Width, int Height, int Stride)
    {
        public bool IsEmpty => Bgra.Length == 0 || Width <= 0 || Height <= 0;
    }

    /// <summary>抓窗口客户端区域。设备像素尺度已按 RenderScaling 折算。</summary>
    public static Frame GrabClient(Window window)
    {
        var scale = window.RenderScaling;
        var width = (int)Math.Round(window.ClientSize.Width * scale);
        var height = (int)Math.Round(window.ClientSize.Height * scale);
        if (width <= 0 || height <= 0) return new Frame([], 0, 0, 0);

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var origin = new NativePoint();
        ClientToScreen(handle, ref origin);
        var pixels = GrabScreen(origin.X, origin.Y, width, height, out var stride);
        return new Frame(pixels, width, height, stride);
    }

    /// <summary>
    /// 区域内"差异显著"的像素占比(0~1)。阈值取通道最大差,用来滤掉抗锯齿与
    /// 亚像素取整带来的 1~2 级抖动 —— 不设阈值的话,连"完全一样的两帧"都会报出差异。
    /// </summary>
    public static double DiffRatio(Frame first, Frame second, PixelRect? region = null, int threshold = 24)
    {
        if (first.IsEmpty || second.IsEmpty) return double.NaN;
        if (first.Width != second.Width || first.Height != second.Height) return double.NaN;

        var area = region ?? new PixelRect(0, 0, first.Width, first.Height);
        var x0 = Math.Clamp(area.X, 0, first.Width);
        var y0 = Math.Clamp(area.Y, 0, first.Height);
        var x1 = Math.Clamp(area.X + area.Width, x0, first.Width);
        var y1 = Math.Clamp(area.Y + area.Height, y0, first.Height);
        if (x1 <= x0 || y1 <= y0) return double.NaN;

        long total = 0;
        long different = 0;
        for (var y = y0; y < y1; y++)
        {
            var rowStart = y * first.Stride;
            for (var x = x0; x < x1; x++)
            {
                var offset = rowStart + x * 4;
                var db = Math.Abs(first.Bgra[offset] - second.Bgra[offset]);
                var dg = Math.Abs(first.Bgra[offset + 1] - second.Bgra[offset + 1]);
                var dr = Math.Abs(first.Bgra[offset + 2] - second.Bgra[offset + 2]);
                if (Math.Max(db, Math.Max(dg, dr)) > threshold) different++;
                total++;
            }
        }

        return total == 0 ? double.NaN : (double)different / total;
    }

    /// <summary>
    /// 区域内每像素"最大通道差"的**平均值**(0~255),连续量。
    ///
    /// 为什么要有它:<see cref="DiffRatio"/> 带阈值,只回答"有多少像素差得**明显**",
    /// 用来判"某个 Effect 有没有画出来"够用;但判"两种画法是不是同一张画"时它会失灵
    /// —— 重采样误差每像素只差几级,全落在阈值下面,读数恒 0.00%,什么也没证明。
    /// 平均差则能给出"到底差多少级",1~2 级是重采样误差,10 级以上才是真的换了样子。
    /// </summary>
    public static double MeanDelta(Frame first, Frame second, PixelRect? region = null)
    {
        if (first.IsEmpty || second.IsEmpty) return double.NaN;
        if (first.Width != second.Width || first.Height != second.Height) return double.NaN;

        var area = region ?? new PixelRect(0, 0, first.Width, first.Height);
        var x0 = Math.Clamp(area.X, 0, first.Width);
        var y0 = Math.Clamp(area.Y, 0, first.Height);
        var x1 = Math.Clamp(area.X + area.Width, x0, first.Width);
        var y1 = Math.Clamp(area.Y + area.Height, y0, first.Height);
        if (x1 <= x0 || y1 <= y0) return double.NaN;

        long total = 0;
        long sum = 0;
        for (var y = y0; y < y1; y++)
        {
            var rowStart = y * first.Stride;
            for (var x = x0; x < x1; x++)
            {
                var offset = rowStart + x * 4;
                var db = Math.Abs(first.Bgra[offset] - second.Bgra[offset]);
                var dg = Math.Abs(first.Bgra[offset + 1] - second.Bgra[offset + 1]);
                var dr = Math.Abs(first.Bgra[offset + 2] - second.Bgra[offset + 2]);
                sum += Math.Max(db, Math.Max(dg, dr));
                total++;
            }
        }

        return total == 0 ? double.NaN : (double)sum / total;
    }

    /// <summary>落盘成 PNG(临时目录),供人工核对。</summary>
    public static string Save(Frame frame, string name)
    {
        if (frame.IsEmpty) return "";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name + ".png");
        var pointer = Marshal.AllocHGlobal(frame.Bgra.Length);
        try
        {
            Marshal.Copy(frame.Bgra, 0, pointer, frame.Bgra.Length);
            using var image = new Avalonia.Media.Imaging.Bitmap(
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Premul,
                pointer,
                new PixelSize(frame.Width, frame.Height),
                new Vector(96, 96),
                frame.Stride);
            image.Save(path);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return path;
    }

    /// <summary>把窗口坐标(逻辑像素)里的一个矩形折算成设备像素矩形。</summary>
    public static PixelRect ToDeviceRect(Window window, double x, double y, double width, double height)
    {
        var scale = window.RenderScaling;
        return new PixelRect(
            (int)Math.Round(x * scale),
            (int)Math.Round(y * scale),
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static byte[] GrabScreen(int x, int y, int width, int height, out int stride)
    {
        stride = width * 4;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020 /*SRCCOPY*/);

        var header = new BitmapInfoHeader
        {
            Size = 40, Width = width, Height = -height, /* top-down */ Planes = 1, BitCount = 32, Compression = 0,
        };
        var bytes = new byte[stride * height];
        GetDIBits(memory, bitmap, 0, height, bytes, ref header, 0 /*DIB_RGB_COLORS*/);

        SelectObject(memory, previous);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size; public int Width; public int Height; public short Planes;
        public short BitCount; public int Compression; public int SizeImage;
        public int XPelsPerMeter; public int YPelsPerMeter; public int ClrUsed; public int ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr srcDc, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, int start, int lines, byte[] buffer, ref BitmapInfoHeader bi, int usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
