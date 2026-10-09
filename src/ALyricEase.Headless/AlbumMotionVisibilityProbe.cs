using ALyricEase.Views;
using Avalonia.Controls;

namespace ALyricEase.Headless;

/// <summary>用真窗口验证播放详情页背景动画会随宿主窗口的可呈现状态启停。</summary>
internal static class AlbumMotionVisibilityProbe
{
    private const int TimeoutMs = 3000;

    public static async Task<int> RunRealAsync()
    {
        var background = new AlbumCoverBackground
        {
            MotionEnabled = true
        };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            ShowActivated = false,
            Content = background
        };

        try
        {
            window.Show();
            if (!await ExpectAsync(background, expected: true, "显示后启动")) return 1;

            window.WindowState = WindowState.Minimized;
            if (!await ExpectAsync(background, expected: false, "最小化后停止")) return 1;

            window.WindowState = WindowState.Normal;
            window.Show();
            if (!await ExpectAsync(background, expected: true, "还原后恢复")) return 1;

            window.Hide();
            if (!await ExpectAsync(background, expected: false, "隐藏后停止")) return 1;

            window.Show();
            if (!await ExpectAsync(background, expected: true, "重新显示后恢复")) return 1;

            Console.WriteLine("[album-motion-visibility] PASS");
            return 0;
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<bool> ExpectAsync(
        AlbumCoverBackground background,
        bool expected,
        string label)
    {
        var deadline = Environment.TickCount64 + TimeoutMs;
        while (background.IsDriftRunning != expected && Environment.TickCount64 < deadline)
            await Task.Delay(25);

        var actual = background.IsDriftRunning;
        Console.WriteLine($"[album-motion-visibility] {label}: expected={expected}, actual={actual}");
        return actual == expected;
    }
}
