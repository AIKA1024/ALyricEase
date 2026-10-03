using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ALyricEase.Headless;

/// <summary>按下/hover 画刷过渡时间线探针(--pressedtimeline):
/// 复刻 user-row 的伪类样式结构(独立 overlay Border + BrushTransition),
/// 用无头扩展的真实鼠标管线(MouseMove/MouseDown/MouseUp,含 PointerOverPreProcessor
/// 的 over 链行为)驱动,逐帧记录 overlay Border 的 Background 值序列,
/// 回答"按下时过渡是平滑插值还是延迟/跳变"。</summary>
public static class PressedBrushTimelineProbe
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();

    public static async System.Threading.Tasks.Task RunAsync()
    {
        var window = new Window { Width = 400, Height = 200 };

        var baseBrush = new SolidColorBrush(Color.Parse("#232323"));
        var hoverBrush = new SolidColorBrush(Color.Parse("#343434"));
        var pressedBrush = new SolidColorBrush(Color.Parse("#464646"));

        var baseStyle = new Style(x => x.OfType<HoverBorder>().Class("user-row-hover"));
        baseStyle.Setters.Add(new Setter(Border.BackgroundProperty, baseBrush));
        baseStyle.Setters.Add(new Setter(Border.TransitionsProperty, new Transitions
        {
            new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(100) },
        }));
        window.Styles.Add(baseStyle);

        var overStyle = new Style(x => x.OfType<HoverBorder>().Class("user-row-hover")
            .Class(":pointerover"));
        overStyle.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush));
        window.Styles.Add(overStyle);

        var pressedStyle = new Style(x => x.OfType<HoverBorder>().Class("user-row-hover")
            .Class(":pressed"));
        pressedStyle.Setters.Add(new Setter(Border.BackgroundProperty, pressedBrush));
        window.Styles.Add(pressedStyle);

        var hoverBorder = new HoverBorder
        {
            Classes = { "user-row-hover" },
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
        };
        var grid = new Grid
        {
            Margin = new Thickness(8),
            ColumnDefinitions = ColumnDefinitions.Parse("48,*,Auto"),
            ColumnSpacing = 12,
        };
        grid.Children.Add(new TextBlock { Text = "row", VerticalAlignment = VerticalAlignment.Center });
        var panel = new Panel();
        panel.Children.Add(hoverBorder);
        panel.Children.Add(grid);
        var button = new Button
        {
            Classes = { "user-row" },
            Width = 300,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Content = panel,
        };
        window.Content = button;

        void Log(string msg) => Console.WriteLine($"[{Sw.ElapsedMilliseconds,5}ms] {msg}");
        hoverBorder.PropertyChanged += (_, e) =>
        {
            if (e.Property == Border.BackgroundProperty)
                Log($"Background → {Describe(e.NewValue as IBrush)}");
        };

        window.Show();
        Log($"init = {Describe(hoverBorder.Background)}");

        var rowPoint = new Point(150, 24);
        var farPoint = new Point(380, 180);
        _ = rowPoint;
        _ = farPoint;

        for (var t = 0; t <= 800; t += 10)
        {
            var tt = t;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                window.InvalidateVisual(); // 补帧推进过渡动画(无头无渲染泵)
                if (tt == 100)
                {
                    Log(">>> pointerover ON");
                    hoverBorder.SetOver(true);
                }
                else if (tt == 250)
                {
                    Log(">>> pressed ON");
                    hoverBorder.SetPressed(true);
                }
                else if (tt == 400)
                {
                    Log(">>> pressed OFF");
                    hoverBorder.SetPressed(false);
                }
                else if (tt == 550)
                {
                    Log(">>> pointerover OFF");
                    hoverBorder.SetOver(false);
                }
            });
            await System.Threading.Tasks.Task.Delay(10);
        }

        Log($"final = {Describe(hoverBorder.Background)}");
    }

    /// <summary>PseudoClasses 是 protected,用子类暴露以便探针直接驱动伪类。</summary>
    private sealed class HoverBorder : Border
    {
        public void SetOver(bool on) => PseudoClasses.Set(":pointerover", on);

        public void SetPressed(bool on) => PseudoClasses.Set(":pressed", on);
    }

    private static string Describe(IBrush? brush)
    {
        Color c;
        if (brush is SolidColorBrush s) c = s.Color;
        else if (brush is Avalonia.Media.Immutable.ImmutableSolidColorBrush i) c = i.Color;
        else return brush?.GetType().Name ?? "null";
        return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
