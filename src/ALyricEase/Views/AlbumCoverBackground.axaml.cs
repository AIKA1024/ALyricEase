using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>
/// 从封面提取四个代表色，绘制成原版 AlbumCoverBackgroundControl 风格的动态柔光色团。
/// 色团在合成线程缓慢漂移；切歌时双层交叉淡化，避免整张模糊封面造成的纹理噪声。
/// </summary>
public partial class AlbumCoverBackground : UserControl
{
    public static readonly StyledProperty<IImage?> CoverProperty =
        AvaloniaProperty.Register<AlbumCoverBackground, IImage?>(nameof(Cover));

    private static readonly Color[] s_defaultPalette =
    [
        Color.FromRgb(53, 74, 112),
        Color.FromRgb(78, 52, 112),
        Color.FromRgb(39, 92, 112),
        Color.FromRgb(106, 53, 91),
    ];

    private static readonly SplineEasing s_slowEasing = new(0.62, 0.03, 0.38, 0.97);
    // 加速段也从低速平顺进入，避免关键帧一切换就突然前冲。
    private static readonly SplineEasing s_burstEasing = new(0.42, 0, 0.58, 1);
    private static readonly TimeSpan s_motionCycle = TimeSpan.FromSeconds(26);

    private bool _showingLayerA = true;
    private bool _motionStarted;
    private bool _motionStartQueued;
    private int _motionStartAttempts;
    private int _motionGeneration;
    private int _paletteVersion;

    public IImage? Cover
    {
        get => GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }

    public AlbumCoverBackground()
    {
        InitializeComponent();
        ApplyPalette(GetLayer(true), s_defaultPalette);
        ApplyPalette(GetLayer(false), s_defaultPalette);
        BaseLayer.Background = new SolidColorBrush(Darken(s_defaultPalette[0], 0.48));
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CoverProperty)
            QueuePaletteRefresh();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _motionStartAttempts = 0;
        QueueMotionStart();
        QueuePaletteRefresh();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _motionGeneration++;
        _motionStartQueued = false;
        _motionStartAttempts = 0;
        foreach (var ellipse in GetLayer(true).Concat(GetLayer(false)))
        {
            var visual = ElementComposition.GetElementVisual(ellipse);
            visual?.StopAnimation("Translation");
            visual?.StopAnimation("Scale");
        }
        _motionStarted = false;
    }

    private void QueuePaletteRefresh()
    {
        var version = ++_paletteVersion;
        Dispatcher.UIThread.Post(() =>
        {
            var cover = Cover;
            if (version != _paletteVersion || cover is null || !this.IsAttachedToVisualTree()) return;
            var palette = ExtractPalette(cover);
            if (version == _paletteVersion)
                CrossFadeTo(palette);
        }, DispatcherPriority.Background);
    }

    private void CrossFadeTo(Color[] palette)
    {
        var targetIsA = !_showingLayerA;
        ApplyPalette(GetLayer(targetIsA), palette);
        PaletteLayerA.Opacity = targetIsA ? 1 : 0;
        PaletteLayerB.Opacity = targetIsA ? 0 : 1;
        BaseLayer.Background = new SolidColorBrush(Darken(palette[0], 0.48));
        _showingLayerA = targetIsA;
    }

    private Ellipse[] GetLayer(bool layerA) => layerA ? [A0, A1, A2, A3, A4] : [B0, B1, B2, B3, B4];

    private static void ApplyPalette(Ellipse[] ellipses, IReadOnlyList<Color> palette)
    {
        for (var i = 0; i < ellipses.Length; i++)
        {
            var color = i < palette.Count
                ? palette[i]
                : Blend(palette[0], palette[Math.Min(2, palette.Count - 1)]);
            ellipses[i].Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(218, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(142, color.R, color.G, color.B), 0.52),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            };
        }
    }

    /// <summary>
    /// UserControl 的 Attached 早于子元素合成视觉就绪。延后到 Loaded 优先级启动；
    /// 若渲染后端仍未建立子视觉，则在下一帧重试，避免把“没有启动任何动画”误记为已启动。
    /// </summary>
    private void QueueMotionStart()
    {
        if (_motionStarted || _motionStartQueued || _motionStartAttempts >= 8
            || !this.IsAttachedToVisualTree()) return;

        var generation = ++_motionGeneration;
        _motionStartQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _motionStartQueued = false;
            if (generation != _motionGeneration || !this.IsAttachedToVisualTree()) return;
            if (TryStartMotion()) return;
            _motionStartAttempts++;

            if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
            topLevel.RequestAnimationFrame(_ =>
            {
                if (generation == _motionGeneration && this.IsAttachedToVisualTree())
                    QueueMotionStart();
            });
        }, DispatcherPriority.Loaded);
    }

    private bool TryStartMotion()
    {
        if (_motionStarted) return true;

        var all = GetLayer(true).Concat(GetLayer(false)).ToArray();
        var visuals = all.Select(ElementComposition.GetElementVisual).ToArray();
        if (visuals.Any(visual => visual is null)) return false;

        // 统一周期让所有色团共享一次整体呼吸：约 12.5 秒缓移后才短暂提速，随后长时间回缓。
        // 旧实现为 13~17 秒错峰循环且每圈有两次加速，多个色团叠加后会显得加速过于频繁。
        // A/B 调色层使用同一轨迹，切歌交叉淡化时不会跳位。
        var paths = new (Vector3 Hold, Vector3 Burst, Vector3 Drift)[]
        {
            (new Vector3(12, 5, 0), new Vector3(148, 58, 0), new Vector3(64, 142, 0)),
            (new Vector3(-7, 13, 0), new Vector3(-86, 146, 0), new Vector3(-154, 32, 0)),
            (new Vector3(-14, -6, 0), new Vector3(-152, -70, 0), new Vector3(22, -154, 0)),
            (new Vector3(9, -12, 0), new Vector3(108, -128, 0), new Vector3(158, 28, 0)),
            (new Vector3(-10, 8, 0), new Vector3(-104, 92, 0), new Vector3(82, 132, 0)),
        };

        for (var i = 0; i < all.Length; i++)
        {
            var ellipse = all[i];
            var visual = visuals[i]!;
            var pathIndex = i % paths.Length;
            var path = paths[pathIndex];

            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            visual.Translation = default;
            visual.Scale = Vector3.One;
            visual.CenterPoint = new Vector3(
                (float)(ellipse.Bounds.Width / 2),
                (float)(ellipse.Bounds.Height / 2),
                0);

            var drift = visual.Compositor.CreateVector3DKeyFrameAnimation();
            drift.Target = "Translation";
            drift.Duration = s_motionCycle;
            drift.IterationBehavior = AnimationIterationBehavior.Forever;
            drift.InsertKeyFrame(0, default);
            drift.InsertKeyFrame(0.48f, path.Hold, s_slowEasing);
            drift.InsertKeyFrame(0.62f, path.Burst, s_burstEasing);
            drift.InsertKeyFrame(0.82f, path.Drift, s_slowEasing);
            drift.InsertKeyFrame(1, default, s_slowEasing);
            visual.StartAnimation("Translation", drift);

            // 椭圆本身没有方向感，轻微呼吸比旋转更自然，也能让大面积同色封面看出动态。
            var breath = visual.Compositor.CreateVector3DKeyFrameAnimation();
            breath.Target = "Scale";
            breath.Duration = s_motionCycle;
            breath.IterationBehavior = AnimationIterationBehavior.Forever;
            breath.InsertKeyFrame(0, Vector3.One);
            breath.InsertKeyFrame(0.48f, new Vector3(1.02f, 1.015f, 1), s_slowEasing);
            breath.InsertKeyFrame(0.62f, new Vector3(1.055f + pathIndex * 0.004f, 1.04f, 1), s_burstEasing);
            breath.InsertKeyFrame(0.82f, new Vector3(0.985f, 1.025f, 1), s_slowEasing);
            breath.InsertKeyFrame(1, Vector3.One, s_slowEasing);
            visual.StartAnimation("Scale", breath);
        }

        _motionStarted = true;
        return true;
    }

    private static Color[] ExtractPalette(IImage cover)
    {
        const int size = 48;
        const int stride = size * 4;
        var image = new Image { Source = cover, Stretch = Stretch.UniformToFill, Width = size, Height = size };
        image.Measure(new Size(size, size));
        image.Arrange(new Rect(0, 0, size, size));

        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Avalonia.Vector(96, 96));
        bitmap.Render(image);
        var buffer = Marshal.AllocHGlobal(stride * size);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size, size), buffer, stride * size, stride);
            var bins = new Dictionary<int, PaletteBin>();
            for (var y = 0; y < size; y += 2)
            for (var x = 0; x < size; x += 2)
            {
                var offset = y * stride + x * 4;
                var b = Marshal.ReadByte(buffer, offset);
                var g = Marshal.ReadByte(buffer, offset + 1);
                var r = Marshal.ReadByte(buffer, offset + 2);
                var a = Marshal.ReadByte(buffer, offset + 3);
                if (a < 160) continue;
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var lightness = (max + min) / 510.0;
                var saturation = max == 0 ? 0 : (max - min) / (double)max;
                if (lightness < 0.035 || lightness > 0.97) continue;

                var key = (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;
                if (!bins.TryGetValue(key, out var bin)) bins[key] = bin = new PaletteBin();
                bin.Add(r, g, b, saturation, lightness);
            }

            var candidates = bins.Values.OrderByDescending(bin => bin.Score).Select(bin => bin.Color).ToList();
            var selected = new List<Color>(4);
            foreach (var candidate in candidates)
            {
                if (selected.All(existing => ColorDistance(existing, candidate) >= 58))
                    selected.Add(Vibrant(candidate));
                if (selected.Count == 4) break;
            }

            if (selected.Count == 0) return [.. s_defaultPalette];
            var extractedCount = selected.Count;
            while (selected.Count < 4)
            {
                var source = selected[selected.Count % extractedCount];
                selected.Add(Shift(source, selected.Count));
            }
            return [.. selected];
        }
        catch
        {
            return [.. s_defaultPalette];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed class PaletteBin
    {
        private int _count;
        private int _r;
        private int _g;
        private int _b;
        private double _quality;

        public void Add(byte r, byte g, byte b, double saturation, double lightness)
        {
            _count++;
            _r += r;
            _g += g;
            _b += b;
            var middleLightness = 1 - Math.Abs(lightness - 0.5) * 1.35;
            _quality += 0.3 + saturation * 1.25 + Math.Max(0, middleLightness) * 0.35;
        }

        public double Score => _quality * Math.Sqrt(_count);
        public Color Color => Color.FromRgb((byte)(_r / _count), (byte)(_g / _count), (byte)(_b / _count));
    }

    private static double ColorDistance(Color a, Color b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return Math.Sqrt(dr * dr * 0.3 + dg * dg * 0.59 + db * db * 0.11);
    }

    private static Color Vibrant(Color color)
    {
        var mean = (color.R + color.G + color.B) / 3.0;
        byte Boost(byte channel) => (byte)Math.Clamp(mean + (channel - mean) * 1.28 + 8, 18, 238);
        return Color.FromRgb(Boost(color.R), Boost(color.G), Boost(color.B));
    }

    private static Color Darken(Color color, double factor) => Color.FromRgb(
        (byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

    private static Color Blend(Color first, Color second) => Color.FromRgb(
        (byte)((first.R + second.R) / 2),
        (byte)((first.G + second.G) / 2),
        (byte)((first.B + second.B) / 2));

    private static Color Shift(Color color, int index)
    {
        var mix = index % 2 == 0 ? Color.FromRgb(45, 94, 128) : Color.FromRgb(112, 52, 116);
        return Color.FromRgb(
            (byte)((color.R * 2 + mix.R) / 3),
            (byte)((color.G * 2 + mix.G) / 3),
            (byte)((color.B * 2 + mix.B) / 3));
    }
}
