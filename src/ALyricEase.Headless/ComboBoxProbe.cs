using System;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// WinUI 风格 ComboBox 弹出层探针(--combobox)：真窗口 + 抓屏，量三件事。
///
/// 1. 边距：选中块的左右留缝(5px)、弹出层内边距(ItemsPresenter 上下 4px)。
/// 2. 位置：选中项与 ComboBox 对齐；空间不足时 Overlay 必须停在安全区域边缘，横向左边缘对齐。
/// 3. 动画：t=0 内容必须完全不可见；中途 RevealProgress 单调增；阴影在揭示结束后才淡入。
///
/// 环境变量：ALY_CB_COUNT(项数)、ALY_CB_SELECTED(选中下标)、ALY_CB_LIGHT=1(浅色)、
/// ALY_CB_CYCLES(开合次数，验偏移不累积)、ALY_CB_SHOTS=0(不抓屏，只量帧节奏/几何)、
/// ALY_CB_OVERLAY=1(Android 同款宿主)、ALY_CB_WIDTH/ALY_CB_HEIGHT(客户区尺寸)。
///
/// 帧节奏基线(165Hz 屏)：p50≈6ms、p95≈7ms、≤18ms ≥95%；结尾 1~3 帧大间隔是弹层关闭，
/// 首帧的 ~100ms 是弹层窗口创建。若 p95 回到 ~90ms，说明帧泵租约没生效(见 UiFramePacer)。
///
/// 抓屏用 <see cref="ScreenCapture.GrabClient"/>：BitBlt 读的是屏幕像素，弹窗是独立窗口，
/// 只要落在主窗口客户区内就能被一起抓到，所以窗口要开得比弹层大。
/// </summary>
internal static class ComboBoxProbe
{
    private static readonly string[] Fonts =
    [
        "Arial", "Comic Sans MS", "Courier New", "Segoe UI", "Times New Roman",
    ];

    public static async Task<int> RunAsync()
    {
        var light = Environment.GetEnvironmentVariable("ALY_CB_LIGHT") == "1";
        var cycles = Math.Max(1, EnvInt("ALY_CB_CYCLES", 2));
        var shots = Environment.GetEnvironmentVariable("ALY_CB_SHOTS") != "0";

        var window = new Window
        {
            Width = EnvInt("ALY_CB_WIDTH", 760),
            Height = EnvInt("ALY_CB_HEIGHT", 520),
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse(light ? "#F3F3F3" : "#1F1F1F")),
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowActivated = true,
        };

        var count = EnvInt("ALY_CB_COUNT", Fonts.Length);
        var selected = EnvInt("ALY_CB_SELECTED", 2);
        var items = count == Fonts.Length
            ? Fonts
            : Enumerable.Range(1, count).Select(i => $"Item {i}").ToArray();

        var combo = new ComboBox
        {
            Width = 202,
            Height = 32,
            ItemsSource = items,
            SelectedIndex = selected,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(120, EnvInt("ALY_CB_TOP", 180), 0, 0),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Font", Foreground = new SolidColorBrush(Color.Parse(light ? "#1B1B1B" : "#FFFFFF")) },
                combo,
                new TextBlock
                {
                    Text = "You can set the font used for this text.",
                    Foreground = new SolidColorBrush(Color.Parse(light ? "#5D5D5D" : "#C2C2C2")),
                },
            },
        };

        window.Content = panel;
        window.Show();
        window.Topmost = true;
        window.Activate();
        await DrainAsync(600);

        var popup = combo.GetLogicalDescendants().OfType<Popup>().FirstOrDefault()
                    ?? combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault();

        if (popup is null)
        {
            Console.WriteLine("[combobox] 找不到 PART_Popup");
            return 1;
        }

        popup.ShouldUseOverlayLayer = Environment.GetEnvironmentVariable("ALY_CB_OVERLAY") == "1";
        var failures = 0;
        var root = popup.Child as Panel;
        var surface = root?.Children.OfType<RevealBorder>().FirstOrDefault();
        var shadow = root?.Children.OfType<Border>().FirstOrDefault(b => b.Name == "PART_DropDownShadow");

        Console.WriteLine($"[combobox] 弹层结构: root={(root is null ? "null" : "ok")} surface={(surface is null ? "null" : "ok")} shadow={(shadow is null ? "null" : "ok")}");

        var closedFrame = shots ? ScreenCapture.GrabClient(window) : default;
        Console.WriteLine($"[combobox] closed png={(shots ? ScreenCapture.Save(closedFrame, "cbprobe-closed") : "skipped")}");

        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            var openedAt = -1L;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var firstRevealAt = -1L;
            var frameTimes = new System.Collections.Generic.List<double>();
            void OnFrame(TimeSpan _)
            {
                frameTimes.Add(sw.Elapsed.TotalMilliseconds);
                if (sw.ElapsedMilliseconds < 900)
                {
                    TopLevel.GetTopLevel(surface)?.RequestAnimationFrame(OnFrame);
                }
            }
            void OnOpened(object? s, EventArgs e)
            {
                openedAt = sw.ElapsedMilliseconds;
                TopLevel.GetTopLevel(surface)?.RequestAnimationFrame(OnFrame);
            }
            void OnSurfaceChanged(object? s, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == RevealBorder.RevealProgressProperty && surface is not null &&
                    surface.RevealProgress > 0 && firstRevealAt < 0)
                {
                    firstRevealAt = sw.ElapsedMilliseconds;
                }
            }
            popup.Opened += OnOpened;
            if (surface is not null) surface.PropertyChanged += OnSurfaceChanged;
            combo.IsDropDownOpen = true;

            var elapsed = 0;
            foreach (var at in new[] { 0, 60, 120, 200, 320, 460, 700 })
            {
                await Task.Delay(at - elapsed);
                elapsed = at;

                var frame = shots ? ScreenCapture.GrabClient(window) : default;
                var path = shots ? ScreenCapture.Save(frame, $"cbprobe-{cycle}-{at:000}") : "skipped";

                var progress = surface?.RevealProgress ?? -1;
                var shadowOpacity = shadow?.Opacity ?? -1;
                var comboOrigin = combo.PointToScreen(default);
                var surfaceOrigin = surface?.PointToScreen(default) ?? default;
                var itemTop = -1.0;
                var itemLeft = -1.0;
                var itemWidth = -1.0;
                var itemHeight = -1.0;
                var itemScreenTop = -1.0;
                var index = combo.SelectedIndex >= 0 ? combo.SelectedIndex : 0;
                if (combo.ContainerFromIndex(index) is Control container &&
                    surface is not null && surface.Bounds.Height > 0)
                {
                    var local = container.TranslatePoint(default, surface);
                    itemTop = local?.Y ?? -1;
                    itemLeft = local?.X ?? -1;
                    itemWidth = container.Bounds.Width;
                    itemHeight = container.Bounds.Height;
                    itemScreenTop = container.PointToScreen(default).Y;
                }

                if (at >= 700)
                {
                    Console.WriteLine($"[combobox] c{cycle} pacer@open: {ALyricEase.Infrastructure.UiFramePacer.Describe()}");
                }

                var aligned = itemScreenTop >= 0 && Math.Abs(itemScreenTop - comboOrigin.Y) <= 1.5;
                if (at == 700 && surface is not null)
                {
                    var scale = window.RenderScaling;
                    var centerError = Math.Abs(itemScreenTop + itemHeight * scale / 2 -
                                               comboOrigin.Y - combo.Bounds.Height * scale / 2);
                    var leftError = Math.Abs(surfaceOrigin.X - comboOrigin.X);
                    var positionError = centerError;
                    if (popup.IsUsingOverlayLayer)
                    {
                        // 首尾项无法继续滚动时，完整弹层可能无法让选中项盖住触发器。
                        // 此时验证 Slide 将弹层限制在窗口安全区域，不能要求它越界。
                        var safeArea = new Rect(window.ClientSize)
                            .Deflate(window.InsetsManager?.SafeAreaPadding ?? default);
                        var top = window.PointToScreen(safeArea.TopLeft).Y;
                        var bottom = window.PointToScreen(safeArea.BottomRight).Y - surface.Bounds.Height * scale;
                        var desiredTop = comboOrigin.Y + (combo.Bounds.Height / 2 - itemTop - itemHeight / 2) * scale;
                        var expectedTop = Math.Clamp(desiredTop, top, Math.Max(top, bottom));
                        positionError = Math.Abs(surfaceOrigin.Y - expectedTop);
                    }
                    if (positionError > 1.5 || leftError > 1.5 || surface.RevealProgress < 1)
                    {
                        failures++;
                        Console.WriteLine($"[combobox] FAIL overlay={popup.IsUsingOverlayLayer} positionError={positionError:0.0}px centerError={centerError:0.0}px leftError={leftError:0.0}px");
                    }
                }

                var shadowDelta = double.NaN;
                if (surface is not null && at >= 460)
                {
                    var clientOrigin = window.PointToScreen(default);
                    var scale = window.RenderScaling;
                    if (scale <= 0) scale = 1.0;
                    var left = (surfaceOrigin.X + 24 - clientOrigin.X) / scale;
                    var top = (surfaceOrigin.Y + surface.Bounds.Height + 2 - clientOrigin.Y) / scale;
                    var width = Math.Max(8, surface.Bounds.Width - 48);
                    var rect = ScreenCapture.ToDeviceRect(window, left, top, width, 12);
                    if (shots) shadowDelta = ScreenCapture.MeanDelta(closedFrame, frame, rect);
                }
                Console.WriteLine(
                    $"[combobox] c{cycle} t={sw.ElapsedMilliseconds,4}ms openedAt={openedAt} firstRevealAt={firstRevealAt} reveal={progress:0.000} shadow={shadowOpacity:0.000} " +
                    $"vOffset={popup.VerticalOffset:0.0} hOffset={popup.HorizontalOffset:0.0} " +
                    $"combo=({comboOrigin.X:0},{comboOrigin.Y:0}) {combo.Bounds.Width:0}x{combo.Bounds.Height:0} " +
                    $"surface=({surfaceOrigin.X:0},{surfaceOrigin.Y:0}) {surface?.Bounds.Width:0}x{surface?.Bounds.Height:0} " +
                    $"itemInSurface=({itemLeft:0},{itemTop:0}) {itemWidth:0}x{itemHeight:0} " +
                    $"aligned={aligned} shadowDelta={shadowDelta:0.00} png={path}");
            }

            var intervals = frameTimes.Zip(frameTimes.Skip(1), (a, b) => b - a).ToArray();

            if (intervals.Length > 0)
            {
                var sorted = intervals.OrderBy(x => x).ToArray();
                Console.WriteLine(
                    $"[combobox] c{cycle} frames n={intervals.Length} " +
                    $"p50={sorted[sorted.Length / 2]:0.0}ms p95={sorted[(int)(sorted.Length * 0.95)]:0.0}ms " +
                    $"max={sorted[^1]:0.0}ms mean={intervals.Average():0.0}ms " +
                    $"le18={intervals.Count(x => x <= 18.0) * 100 / intervals.Length}% " +
                    $"all=[{string.Join(",", intervals.Select(x => x.ToString("0")))}]");
            }

            popup.Opened -= OnOpened;
            if (surface is not null) surface.PropertyChanged -= OnSurfaceChanged;
            combo.IsDropDownOpen = false;
            await DrainAsync(260);
            Console.WriteLine($"[combobox] c{cycle} pacer: {ALyricEase.Infrastructure.UiFramePacer.Describe()}");
        }

        window.Close();
        return failures == 0 ? 0 : 1;
    }

    private static async Task DrainAsync(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(16);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static int EnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value >= 0 ? value : fallback;
    }
}
