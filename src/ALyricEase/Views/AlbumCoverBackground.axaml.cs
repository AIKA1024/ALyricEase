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

    private static readonly SplineEasing s_driftEasing = new(0.45, 0.05, 0.55, 0.95);

    private bool _showingLayerA = true;
    private bool _motionStarted;
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
        StartMotion();
        QueuePaletteRefresh();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        foreach (var ellipse in GetLayer(true).Concat(GetLayer(false)))
            ElementComposition.GetElementVisual(ellipse)?.StopAnimation("Translation");
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

    private Ellipse[] GetLayer(bool layerA) => layerA ? [A0, A1, A2, A3] : [B0, B1, B2, B3];

    private static void ApplyPalette(Ellipse[] ellipses, IReadOnlyList<Color> palette)
    {
        for (var i = 0; i < ellipses.Length; i++)
        {
            var color = palette[i % palette.Count];
            ellipses[i].Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(238, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(176, color.R, color.G, color.B), 0.55),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            };
        }
    }

    private void StartMotion()
    {
        if (_motionStarted) return;
        _motionStarted = true;

        var all = GetLayer(true).Concat(GetLayer(false)).ToArray();
        var waypoints = new (Vector3 A, Vector3 B)[]
        {
            (new Vector3(95, 55, 0), new Vector3(35, 125, 0)),
            (new Vector3(-80, 95, 0), new Vector3(-135, 20, 0)),
            (new Vector3(-105, -65, 0), new Vector3(25, -120, 0)),
            (new Vector3(90, -85, 0), new Vector3(135, 25, 0)),
        };

        for (var i = 0; i < all.Length; i++)
        {
            var visual = ElementComposition.GetElementVisual(all[i]);
            if (visual is null) continue;
            var path = waypoints[i % waypoints.Length];
            var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
            animation.Target = "Translation";
            animation.Duration = TimeSpan.FromSeconds(18 + i % 4 * 3);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.InsertKeyFrame(0, default);
            animation.InsertKeyFrame(0.33f, path.A, s_driftEasing);
            animation.InsertKeyFrame(0.66f, path.B, s_driftEasing);
            animation.InsertKeyFrame(1, default, s_driftEasing);
            visual.StartAnimation("Translation", animation);
        }
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

    private static Color Shift(Color color, int index)
    {
        var mix = index % 2 == 0 ? Color.FromRgb(45, 94, 128) : Color.FromRgb(112, 52, 116);
        return Color.FromRgb(
            (byte)((color.R * 2 + mix.R) / 3),
            (byte)((color.G * 2 + mix.G) / 3),
            (byte)((color.B * 2 + mix.B) / 3));
    }
}
