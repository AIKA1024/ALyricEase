using System.Diagnostics;
using System.Runtime.InteropServices;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

internal static class PresentationProbe
{
    public static async Task RunAsync(Window window, PlayerViewModel player, Action<string> log)
    {
        using var warm = MakeCover(Colors.Coral, Colors.Gold);
        using var cool = MakeCover(Colors.MidnightBlue, Colors.Teal);
        var previous = player.Cover;
        using var lyrics = new DesktopLyricViewModel(ServiceLocator.Get<AppStateStore>(), player, ServiceLocator.Get<LyricViewModel>());
        var lyricWindow = new DesktopLyricWindow { DataContext = lyrics };
        try
        {
            player.Cover = cool;
            await Task.Delay(1200);
            await SampleAsync("main", window, log);
            lyricWindow.Show();
            await Task.Delay(800);
            await SampleAsync("two-windows", window, log);
            var backdrop = window.GetVisualDescendants().OfType<AlbumCoverBackground>().Single();
            var baseLayer = backdrop.FindControl<Border>("BaseLayer")!;
            var colors = new HashSet<Color>();
            for (var i = 0; i < 6; i++)
            {
                player.Cover = i % 2 == 0 ? warm : cool;
                await SampleAsync("cover-change", window, log, 500);
                await SampleAsync("settled", window, log, 500);
                if (baseLayer.Background is ISolidColorBrush brush) colors.Add(brush.Color);
            }
            if (colors.Count != 2)
                throw new InvalidOperationException($"Expected two alternating base colors, got {colors.Count}.");
            await CheckRightEdgeAsync(window, log);
        }
        finally
        {
            lyricWindow.Close();
            player.Cover = previous;
        }
    }

    private static RenderTargetBitmap MakeCover(Color left, Color right)
    {
        var fill = new LinearGradientBrush { GradientStops = { new GradientStop(left, 0), new GradientStop(right, 1) } };
        var surface = new Border { Width = 600, Height = 600, Background = fill };
        surface.Measure(new Size(600, 600));
        surface.Arrange(new Rect(0, 0, 600, 600));
        var bitmap = new RenderTargetBitmap(new PixelSize(600, 600));
        bitmap.Render(surface);
        return bitmap;
    }

    // QPC timestamps align these intervals with PresentMon's per-swapchain presentation events.
    private static async Task SampleAsync(string phase, Window window, Action<string> log, int durationMs = 3000)
    {
        var start = Stopwatch.GetTimestamp();
        var last = start;
        var gaps = new List<double>();
        var done = new TaskCompletionSource();
        void OnFrame(TimeSpan _)
        {
            var now = Stopwatch.GetTimestamp();
            gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;
            if (Stopwatch.GetElapsedTime(start, now).TotalMilliseconds >= durationMs)
                done.TrySetResult();
            else window.RequestAnimationFrame(OnFrame);
        }
        window.RequestAnimationFrame(OnFrame);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        var longFrames = gaps.Count(gap => gap > 1.5 * median);
        log($"[presentation] phase={phase} qpc={start}..{last} frames={gaps.Count} long={longFrames} " +
            $"p50={median:F1} p95={gaps[(int)((gaps.Count - 1) * 0.95)]:F1} max={gaps[^1]:F1}ms");
    }

    private static async Task CheckRightEdgeAsync(Window window, Action<string> log)
    {
        var overlay = window.FindControl<Grid>("NowPlayingOverlay")!;
        var marker = new Border { Width = 8, Height = 24, Background = Brushes.Magenta, HorizontalAlignment = HorizontalAlignment.Right, ZIndex = 1 };

        overlay.Children.Add(marker);
        var width = window.Width;
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        bool VisibleAtEdge()
        {
            if (!GetClientRect(hwnd, out var client))
                throw new InvalidOperationException("Cannot read native client bounds.");
            var point = window.PointToScreen(new Point(client.Right / window.RenderScaling - 3,
                client.Bottom / window.RenderScaling / 2));
            var pixel = ScreenCapture.GrabScreenRect(point.X, point.Y, 1, 1);
            return !pixel.IsEmpty && pixel.Bgra[0] > 220 && pixel.Bgra[1] < 40 && pixel.Bgra[2] > 220;
        }
        try
        {
            await Task.Delay(300);
            if (!VisibleAtEdge()) throw new InvalidOperationException("Right edge marker is not visible before resize.");
            var misses = 0;
            for (var i = 1; i <= 20; i++)
            {
                window.Width = width - (i <= 10 ? i : 20 - i) * 8;
                await Task.Delay(16);
                if (!VisibleAtEdge()) misses++;
            }
            await Task.Delay(300);
            if (!VisibleAtEdge()) throw new InvalidOperationException("Right edge marker did not settle.");
            log($"[presentation] resize edge misses={misses}/20");
        }
        finally
        {
            window.Width = width;
            overlay.Children.Remove(marker);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out ClientRect rect);
}
