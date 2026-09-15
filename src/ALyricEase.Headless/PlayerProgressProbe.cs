using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.ViewModels;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>进度条渲染变换、拖动 seek 与释放后 hover 气泡的无头回归。</summary>
internal static class PlayerProgressProbe
{
    public static int Run()
    {
        var player = ServiceLocator.Get<PlayerViewModel>();
        var audio = ServiceLocator.Get<IAudioPlayer>();
        player.CurrentSong = new Song
        {
            Id = 30,
            Name = "进度条探针",
            Source = (MusicSource)99,
            DurationMs = 100_000,
        };
        player.DurationMs = 100_000;
        player.ScrubPositionMs = 0;
        player.IsPlaying = false;

        var bar = new PlayerProgressBar
        {
            Width = 400,
            Height = 28,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            DataContext = player,
            DurationMs = player.DurationMs,
            ScrubPositionMs = player.ScrubPositionMs,
            ShowBubble = true,
        };
        var window = new Window { Width = 400, Height = 120, Content = bar };

        try
        {
            window.Show();
            Drain();

            var track = bar.FindControl<Grid>("Track")!;
            var fill = bar.FindControl<Border>("Fill")!;
            var thumb = bar.FindControl<Grid>("Thumb")!;
            var bubble = bar.FindControl<Border>("Bubble")!;
            if (track.Bounds.Width < 399)
                throw new InvalidOperationException($"轨道宽度异常：{track.Bounds.Width:F1}");
            if (fill.RenderTransform is not ScaleTransform fillScale
                || thumb.RenderTransform is not TranslateTransform thumbTranslation
                || bubble.RenderTransform is not TranslateTransform bubbleTranslation)
                throw new InvalidOperationException("进度视觉仍在使用布局属性，而非 Scale/Translate 渲染变换。");

            var start = ToWindow(track, window, 0.25);
            var target = ToWindow(track, window, 0.75);
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(target);
            Drain();

            AssertNear(player.ScrubPositionMs, 75_000, 250, "拖动位置");
            AssertNear(fillScale.ScaleX, 0.75, 0.005, "填充缩放");
            AssertNear(thumbTranslation.X, track.Bounds.Width * 0.75 - 10, 0.5, "小球平移");

            window.MouseUp(target, MouseButton.Left);
            Drain();
            AssertNear(audio.PositionMs, 75_000, 250, "松开 seek");
            if (!bubble.IsVisible)
                throw new InvalidOperationException("小球处松开后 hover 气泡没有保持显示。");
            AssertNear(bubbleTranslation.X, track.Bounds.Width * 0.75 - 29, 0.5, "气泡跟随");

            window.MouseMove(ToWindow(track, window, 0.1));
            Drain();
            if (bubble.IsVisible)
                throw new InvalidOperationException("鼠标离开小球后气泡没有隐藏。");

            window.MouseMove(target);
            Drain();
            if (!bubble.IsVisible)
                throw new InvalidOperationException("渲染平移后的小球没有参与 hover 命中测试。");
            AssertNear(fill.Bounds.Width, track.Bounds.Width, 0.5, "填充布局宽度");
            if (thumb.Margin != default)
                throw new InvalidOperationException($"小球仍在使用布局 Margin：{thumb.Margin}");

            var predicted = ProgressRenderAnimator.PredictPosition(
                10_000, 100_000, isPlaying: true, isScrubbing: false,
                TimeSpan.FromMilliseconds(1_000d / ProgressRenderAnimator.TargetFramesPerSecond));
            AssertNear(predicted, 10_016.667, 0.01, "60 FPS 预测步长");

            Console.WriteLine(
                $"[player-progress] PASS fps={ProgressRenderAnimator.TargetFramesPerSecond}, " +
                $"seek={audio.PositionMs}ms, thumbX={thumbTranslation.X:F1}, bubbleFollow=True");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[player-progress] FAIL {ex.Message}");
            return 1;
        }
        finally
        {
            window.Close();
        }
    }

    private static Point ToWindow(Control track, Window window, double ratio)
        => track.TranslatePoint(new Point(track.Bounds.Width * ratio, track.Bounds.Height / 2), window)
           ?? throw new InvalidOperationException("进度条坐标换算失败。");

    private static void AssertNear(double actual, double expected, double tolerance, string label)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{label}异常：actual={actual:F3}, expected={expected:F3}");
    }

    private static void Drain()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }
}
