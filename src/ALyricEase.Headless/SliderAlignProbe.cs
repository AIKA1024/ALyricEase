using ALyricEase.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;

namespace ALyricEase.Headless;

internal static class SliderAlignProbe
{
    public static void Run()
    {
        var settings = new SettingsView();
        var row = settings.FindControl<FlexPanel>("CrossfadeRow")!;
        var title = settings.FindControl<TextBlock>("CrossfadeTitle")!;
        var cell = settings.FindControl<Grid>("CrossfadeValueCell")!;
        var seconds = settings.FindControl<TextBlock>("CrossfadeSecondsText")!;
        var slider = settings.FindControl<Slider>("CrossfadeSlider")!;
        row.IsVisible = true;
        var window = new Window { Width = 1000, Height = 620, Content = settings };
        window.Show();
        foreach (var fontSize in new[] { 13, 24 })
        {
            title.FontSize = fontSize + 1;
            cell.SetValue(TextElement.FontSizeProperty, (double)fontSize);
            foreach (var width in new[] { 1000, 400, 320, 1000 })
            {
                window.Width = width;
                Rect? first = null;
                foreach (var value in new[] { 3.0, 3.5, 11.5, 12.0 })
                {
                    slider.Value = value;
                    seconds.Text = $"{value:0.#}s";
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var labelsRight = cell.TranslatePoint(new Point(cell.Bounds.Width, 0), row)!.Value.X + cell.Margin.Right;
                    var remaining = row.Bounds.Width - labelsRight;
                    var wrapped = remaining < 80;
                    var expectedWidth = Math.Min(280, wrapped ? row.Bounds.Width : remaining);
                    var bounds = new Rect(slider.TranslatePoint(default, row)!.Value, slider.Bounds.Size);
                    var labelsBottom = cell.TranslatePoint(new Point(0, cell.Bounds.Height), row)!.Value.Y;
                    if (wrapped && bounds.Y < labelsBottom)
                        throw new InvalidOperationException("Slider overlaps labels on the wrapped row.");
                    if (expectedWidth <= 0 || Math.Abs(bounds.Width - expectedWidth) > 1 ||
                        Math.Abs(bounds.Right - row.Bounds.Width) > 1 || (first.HasValue && first.Value != bounds))
                        throw new InvalidOperationException($"width={width} font={fontSize} value={value} " +
                            $"row={row.Bounds} labelsRight={labelsRight} slider={bounds} initial={first}");
                    first = bounds;
                }
                Console.WriteLine($"[slideralign] width={width} font={fontSize} row={row.Bounds.Width} slider={first}");
            }
        }
        window.Close();
        Console.WriteLine("[slideralign] PASS");
    }
}
