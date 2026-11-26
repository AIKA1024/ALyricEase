using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Models;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>歌词列表鼠标点击回归：按下阶段不滚动，松开后同一次点击发出 seek。</summary>
internal static class LyricClickProbe
{
    public static int Run()
    {
        var view = new LyricView { Classes = { "detail" } };
        var list = view.FindControl<ListBox>("LyricList")!;
        var lines = Enumerable.Range(0, 40)
            .Select(i => new LyricLine(TimeSpan.FromSeconds(i * 10), $"第 {i + 1} 行"))
            .ToArray();
        list.ItemsSource = lines;

        long? requestedPosition = null;
        var requestCount = 0;
        view.SeekRequested += (_, e) =>
        {
            requestCount++;
            requestedPosition = e.PositionMs;
            list.SelectedIndex = Array.FindIndex(lines, line => line.TimeMs == e.PositionMs);
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
            view.OnPageShown();
            Dispatcher.UIThread.RunJobs();
            list.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();

            var scroll = list.FindDescendantOfType<ScrollViewer>()!;
            list.ScrollIntoView(15);
            Dispatcher.UIThread.RunJobs();
            var center = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Viewport.Height / 2), window)!.Value;
            window.MouseWheel(center, new Vector(0, -1), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var target = list.GetRealizedContainers().OfType<ListBoxItem>().First(item =>
            {
                var y = item.TranslatePoint(new Point(0, item.Bounds.Height / 2), scroll)!.Value.Y;
                return y > scroll.Viewport.Height * 2 / 3 && y < scroll.Viewport.Height;
            });
            var targetIndex = list.IndexFromContainer(target);

            for (var click = 1; click <= 2; click++)
            {
                if (click == 2)
                {
                    window.MouseWheel(center, new Vector(0, 1), RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs();
                }
                var point = target.TranslatePoint(
                    new Point(target.Bounds.Width / 2, target.Bounds.Height / 2),
                    window) ?? throw new InvalidOperationException("歌词坐标换算失败。");
                if (Math.Abs(point.Y - center.Y) < 5)
                    throw new InvalidOperationException("点击前目标行应偏离中心。");
                var beforePress = list.SelectedIndex;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                if (list.SelectedIndex != beforePress)
                    throw new InvalidOperationException("鼠标按下提前改变了选中歌词。");

                window.MouseUp(point, MouseButton.Left);
                WaitForScrollAnimation();
                if (requestCount != click || requestedPosition != lines[targetIndex].TimeMs)
                    throw new InvalidOperationException($"第 {click} 次单击 seek 异常。");
                var y = target.TranslatePoint(new Point(0, target.Bounds.Height / 2), window)!.Value.Y;
                if (Math.Abs(y - center.Y) > 2)
                    throw new InvalidOperationException($"第 {click} 次点击后未居中，目标行中心 {y:F1}，视口中心 {center.Y:F1}。");
            }

            Console.WriteLine("[lyric-click] PASS 手动滚动后点击其他行或当前行均跳播一次并居中");
            for (var index = lines.Length - 3; index < lines.Length; index++)
            {
                list.SelectedIndex = index;
                WaitForScrollAnimation();
                var row = (ListBoxItem)list.ContainerFromIndex(index)!;
                var y = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), scroll)!.Value.Y;
                if (Math.Abs(y - scroll.Viewport.Height / 2) > 2)
                    throw new InvalidOperationException($"第 {index + 1} 行未居中，行中心 {y:F1}，视口中心 {scroll.Viewport.Height / 2:F1}。");
            }
            Console.WriteLine("[lyric-click] PASS 自动跟随时最后三句均可居中");
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

    private static void WaitForScrollAnimation()
    {
        for (var frame = 0; frame < 32; frame++)
        {
            Thread.Sleep(20);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
