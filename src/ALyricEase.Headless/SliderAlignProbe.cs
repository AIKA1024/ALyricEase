using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>滑杆对齐行为探针(--slideralign):量 Slider 在 *(弹性)列 + MaxWidth 钳制下的
/// 实际排布位置,决定设置页淡化时长滑杆的对齐写法。</summary>
internal static class SliderAlignProbe
{
    public static void Run()
    {
        var window = new Window { Width = 1000, Height = 200 };
        var slider = new Slider { MaxWidth = 280, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            Children =
            {
                new TextBlock { Text = "icon" },
                new TextBlock { Text = "title", [Grid.ColumnProperty] = 1 },
                new TextBlock { Text = "3.0 秒", [Grid.ColumnProperty] = 2 },
                slider,
            },
        };
        slider.SetValue(Grid.ColumnProperty, 3);
        window.Content = grid;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var width in new[] { 1000.0, 400.0, 320.0 })
        {
            window.Width = width;
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine($"[slideralign] window={width} gridW={grid.Bounds.Width:0} " +
                              $"sliderX={slider.Bounds.X:0} sliderW={slider.Bounds.Width:0} " +
                              $"gridRight={grid.Bounds.Width:0}");
        }
    }
}
