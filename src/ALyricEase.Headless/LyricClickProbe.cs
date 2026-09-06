using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ALyricEase.Models;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>歌词列表鼠标点击回归：按下阶段不滚动，松开后同一次点击发出 seek。</summary>
internal static class LyricClickProbe
{
    public static int Run()
    {
        var view = new LyricView();
        var list = view.FindControl<ListBox>("LyricList")!;
        var lines = Enumerable.Range(0, 8)
            .Select(i => new LyricLine(TimeSpan.FromSeconds(i * 10), $"第 {i + 1} 行"))
            .ToArray();
        list.ItemsSource = lines;

        long? requestedPosition = null;
        var requestCount = 0;
        view.SeekRequested += (_, e) =>
        {
            requestCount++;
            requestedPosition = e.PositionMs;
        };

        var window = new Window
        {
            Width = 600,
            Height = 500,
            Content = view
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();

            const int targetIndex = 2;
            list.ScrollIntoView(targetIndex);
            Dispatcher.UIThread.RunJobs();
            if (list.ContainerFromIndex(targetIndex) is not ListBoxItem target)
                throw new InvalidOperationException("目标歌词容器未物化。");

            var point = target.TranslatePoint(
                new Point(target.Bounds.Width / 2, target.Bounds.Height / 2),
                window) ?? throw new InvalidOperationException("歌词坐标换算失败。");

            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            if (list.SelectedIndex != 0)
                throw new InvalidOperationException("鼠标按下提前改变了选中歌词，会在松开前触发滚动。");

            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var expectedPosition = lines[targetIndex].TimeMs;
            if (requestCount != 1 || requestedPosition != expectedPosition)
                throw new InvalidOperationException(
                    $"单击 seek 异常：count={requestCount}, position={requestedPosition}, expected={expectedPosition}。");

            Console.WriteLine($"[lyric-click] PASS 一次点击发出一次 seek，position={requestedPosition}ms");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lyric-click] FAIL {ex.Message}");
            return 1;
        }
        finally
        {
            window.Close();
        }
    }
}
