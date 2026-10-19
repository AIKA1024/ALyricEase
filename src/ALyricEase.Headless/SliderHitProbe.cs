using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>音量滑杆刻度命中探针(--sliderhit):还原 PlayerBar/NowPlaying 的 Slider 配置,
/// 统计刻度带里每一像素真正命中的元素,并在各类命中点上按下/拖动,看 Value 与 hover 样式是否被改写。</summary>
internal static class SliderHitProbe
{
    public static void Run()
    {
        var theme = (ControlTheme?)Application.Current?.FindResource("WinUISliderTheme");
        Console.WriteLine($"[sliderhit] theme={(theme is null ? "null" : "ok")}");

        var vertical = new Slider
        {
            Theme = theme,
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            Height = 150,
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            TickPlacement = TickPlacement.Outside,
            TickFrequency = 20,
        };
        var horizontal = new Slider
        {
            Theme = theme,
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            Width = 200,
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            TickPlacement = TickPlacement.Outside,
            TickFrequency = 20,
        };

        var root = new StackPanel { Spacing = 24, Margin = new Thickness(20) };
        root.Children.Add(horizontal);
        root.Children.Add(vertical);

        var window = new Window { Width = 420, Height = 460, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        RunSlider(window, "h", horizontal, new Point(0, 12));
        RunSlider(window, "v", vertical, new Point(0, 12));

        Console.WriteLine("[sliderhit] done");
    }

    private static void HoverControl(Window window, string tag, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? new Point(double.NaN, double.NaN);
        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine($"[sliderhit] hover {tag}@({center.X:0},{center.Y:0}) " +
                          $"target={Describe(window.InputHitTest(center) as Control)} over={control.IsPointerOver}");
        window.MouseMove(new Point(4, 4));
        Dispatcher.UIThread.RunJobs();
    }

    private static void RunSlider(Window window, string tag, Slider slider, Point drag)
    {
        var track = slider.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        var thumb = track?.Thumb;
        Console.WriteLine($"[sliderhit] {tag} slider={Fmt(B(slider, window))} track={Fmt(B(track, window))} " +
                          $"thumb={Fmt(B(thumb, window))}");
        if (thumb is not null) HoverControl(window, $"{tag} thumb", thumb);
        HoverControl(window, $"{tag} slider", slider);

        // 从滑块开始拖动,再把指针移到刻度带上:值是否继续跟随?
        if (thumb is not null && track is not null)
        {
            var thumbCenter = InWindow(thumb, window, new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
            var bar = slider.GetVisualDescendants().OfType<TickBar>().FirstOrDefault(b => b.IsVisible);
            if (bar is not null)
            {
                var barPoint = InWindow(bar, window, new Point(bar.Bounds.Width / 2, bar.Bounds.Height * 0.15));
                var before = slider.Value;
                window.MouseMove(thumbCenter);
                window.MouseDown(thumbCenter, MouseButton.Left);
                var down = slider.Value;
                window.MouseMove(barPoint);
                var moved = slider.Value;
                window.MouseUp(barPoint, MouseButton.Left);
                Console.WriteLine($"[sliderhit] {tag} dragThumbToTick value {before:0} -> down {down:0} -> moved {moved:0} " +
                                  $"(thumb=({thumbCenter.X:0},{thumbCenter.Y:0}) tick=({barPoint.X:0},{barPoint.Y:0}))");
                slider.Value = 50;
                Dispatcher.UIThread.RunJobs();
            }
        }

        // hover 是否真的点亮轨道:比较轨道底色。
        var trackBase = slider.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "TrackBaseBackground");
        if (trackBase is not null && thumb is not null)
        {
            var rest = trackBase.Background is ISolidColorBrush r0 ? r0.Color.ToString() : "-";
            var tickBar = slider.GetVisualDescendants().OfType<TickBar>().FirstOrDefault(b => b.IsVisible);
            if (tickBar is not null)
            {
                var p = InWindow(tickBar, window, new Point(tickBar.Bounds.Width / 2, tickBar.Bounds.Height * 0.15));
                window.MouseMove(p);
                Dispatcher.UIThread.RunJobs();
                var tickHover = trackBase.Background is ISolidColorBrush r1 ? r1.Color.ToString() : "-";
                var thumbCenter = InWindow(thumb, window, new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2));
                window.MouseMove(thumbCenter);
                Dispatcher.UIThread.RunJobs();
                var thumbHover = trackBase.Background is ISolidColorBrush r2 ? r2.Color.ToString() : "-";
                window.MouseMove(new Point(4, 4));
                Dispatcher.UIThread.RunJobs();
                Console.WriteLine($"[sliderhit] {tag} trackBaseBg rest={rest} tickHover={tickHover} thumbHover={thumbHover}");
            }
        }

        foreach (var bar in slider.GetVisualDescendants().OfType<TickBar>().Where(b => b.IsVisible))
        {
            var barEnters = 0;
            var sliderEnters = 0;
            bar.PointerEntered += (_, _) => barEnters++;
            slider.PointerEntered += (_, _) => sliderEnters++;
            var rect = B(bar, window);
            var stats = new Dictionary<string, int>();
            var samples = new Dictionary<string, Point>();

            for (var y = rect.Y + 0.5; y < rect.Bottom; y += 1)
            {
                for (var x = rect.X + 0.5; x < rect.Right; x += 1)
                {
                    var p = new Point(x, y);
                    var top = window.InputHitTest(p) as Control;
                    var key = Describe(top);
                    stats[key] = stats.GetValueOrDefault(key) + 1;
                    samples.TryAdd(key, p);
                }
            }

            Console.WriteLine($"[sliderhit] {tag} {bar.Name} {bar.Placement} rect={Fmt(rect)}");
            foreach (var kv in stats.OrderByDescending(k => k.Value))
                Console.WriteLine($"[sliderhit]   hit[{kv.Key}] = {kv.Value}px  sample=({samples[kv.Key].X:0},{samples[kv.Key].Y:0})");

            foreach (var kv in stats.OrderByDescending(k => k.Value))
            {
                var p = samples[kv.Key];
                var before = slider.Value;
                var thumbBgBefore = thumb?.Background is ISolidColorBrush b0 ? b0.Color.ToString() : "-";
                window.MouseMove(p);
                Dispatcher.UIThread.RunJobs();
                var thumbBgHover = thumb?.Background is ISolidColorBrush b1 ? b1.Color.ToString() : "-";
                var sliderOverHover = slider.IsPointerOver;
                var barOverHover = bar.IsPointerOver;
                var thumbOverHover = thumb?.IsPointerOver ?? false;
                window.MouseDown(p, MouseButton.Left);
                var afterDown = slider.Value;
                var sliderOverDown = slider.IsPointerOver;
                window.MouseMove(p + drag);
                var afterMove = slider.Value;
                window.MouseUp(p + drag, MouseButton.Left);
                window.MouseMove(new Point(5, 5));
                Dispatcher.UIThread.RunJobs();
                var thumbBgAfter = thumb?.Background is ISolidColorBrush b2 ? b2.Color.ToString() : "-";
                Console.WriteLine($"[sliderhit]   {kv.Key} @({p.X:0},{p.Y:0}) value {before:0} -> {afterDown:0} -> {afterMove:0}; " +
                                  $"sliderOver hover={sliderOverHover} down={sliderOverDown} barOver={barOverHover} thumbOver={thumbOverHover} " +
                                  $"enters={barEnters}/{sliderEnters} " +
                                  $"thumbBg rest={thumbBgBefore} hover={thumbBgHover} after={thumbBgAfter}");
                slider.Value = 50;
            }
        }
    }

    private static Rect B(Visual? visual, Visual root)
    {
        if (visual is null) return default;
        var p = visual.TranslatePoint(new Point(0, 0), root) ?? new Point(double.NaN, double.NaN);
        return new Rect(p, visual.Bounds.Size);
    }

    private static Point InWindow(Visual v, Visual root, Point local)
        => v.TranslatePoint(local, root) ?? new Point(double.NaN, double.NaN);

    private static string Fmt(Rect r) => $"({r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0})";

    private static string Describe(Control? c)
    {
        if (c is null) return "<null>";
        var chain = new List<string>();
        Visual? v = c;
        for (var i = 0; i < 4 && v is not null; i++)
        {
            chain.Add($"{v.GetType().Name}#{v.Name}");
            v = v.GetVisualParent();
        }
        return string.Join("<", chain);
    }
}
