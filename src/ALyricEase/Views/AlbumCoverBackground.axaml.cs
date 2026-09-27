using System;
using System.Collections.Generic;
using System.IO;
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
///
/// <para>
/// ⚠ 漂移**必须**由宿主按"详情页是否打开"绑 <see cref="MotionEnabled"/> 关掉。
/// 原因是实测出来的：这个控件由播放详情页覆盖层承载，而覆盖层在真实 MainWindow 里是
/// **常驻**的（只靠 RenderTransform 移出窗外），所以色团从应用启动起就一直在动 ——
/// 哪怕详情页根本没打开、哪怕整层移在窗外看不见。横跨整窗的组合动画会让合成器
/// 每帧把整幅窗口重画一遍，代价与"看不看得见"无关：
/// 实测首页（详情页关闭）GPU 8.36% / CPU 23.4%，把漂移停掉后降到 0.89% / 1.5%。
/// 反过来，图层**画出来**只值 1.65%，"让它一直动"值 7.3% —— 钱花在动上，不在画上。
/// </para>
///
/// <para>
/// ⚠ **驱动方式试过一次"降频省电"，已回退 —— 别再试**（2026-09-21，拉丁方 4 变体 × 4 位置）：
/// 把 20 条组合动画换成 33ms 的 <c>DispatcherTimer</c> 直接写 <c>visual.Translation/Scale</c>，
/// GPU 12.06% → 1.86%、CPU 22.8% → 4.4%，但**画面肉眼可见地卡**。原因两条：
/// ① 那台定时器实测只跑到 <b>12.5~14 次/秒</b>（名义 33ms 不是实际值 —— 间隔改成 16ms 读数一样、
/// 优先级 Render↔Normal 也一样，说明贴在某个上限上）；
/// ② 相位取自 15.6ms 分辨率的 <c>Environment.TickCount64</c>，位置被量化成阶梯，速度一顿一顿。
/// </para>
///
/// <para>
/// 之所以"降频必然换来卡"：这一层的代价**几乎只与"整窗重绘多少次"成正比**，与"每帧画什么"无关。
/// 同轮实测：色团整层不画只值 <b>0.85%</b>、详情页主画布整块不画只值 <b>+0.11%</b>、
/// "渐变→纯色 / 渐变→预渲染位图"都落在噪声里；换算到**每帧**，组合动画 0.16% GPU/帧、
/// 定时器 0.13% GPU/帧，几乎一样。⇒ 省 GPU 只能少出帧，流畅只能按刷新率出帧，**二者不可兼得**。
/// 而组合动画由合成器推帧，**UI 线程再忙也不掉帧**；定时器会被 UI 线程饿死（空载就已经只有 14 次/秒），
/// 播放中只会更差。要降这一层的钱，只剩"别在看不见的时候动"（<see cref="MotionEnabled"/> 门控，已做）。
/// </para>
/// </summary>
public partial class AlbumCoverBackground : UserControl
{
    public static readonly StyledProperty<IImage?> CoverProperty =
        AvaloniaProperty.Register<AlbumCoverBackground, IImage?>(nameof(Cover));

    /// <summary>
    /// 是否让色团漂移。默认 true 保留设计原意；宿主负责在"看不见这个背景"时置 false。
    /// 注意这**只是**省掉动画开销：静止的色团层几乎不要钱（实测详情页可见且漂移停掉时 0.93% GPU），
    /// 所以关掉它不会改变任何观感，只会在切回来时从当前位置继续。
    /// </summary>
    public static readonly StyledProperty<bool> MotionEnabledProperty =
        AvaloniaProperty.Register<AlbumCoverBackground, bool>(nameof(MotionEnabled), defaultValue: true);

    private static readonly Color[] s_defaultPalette =
    [
        Color.FromRgb(53, 74, 112),
        Color.FromRgb(78, 52, 112),
        Color.FromRgb(39, 92, 112),
        Color.FromRgb(106, 53, 91),
    ];

    private static readonly TimeSpan s_motionCycle = TimeSpan.FromSeconds(26);

    // 色团漂移路标间必须恒速:显式线性缓动(不带 easing 的 InsertKeyFrame 默认行为不可靠)。
    private static readonly LinearEasing s_linearEasing = new();

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

    public bool MotionEnabled
    {
        get => GetValue(MotionEnabledProperty);
        set => SetValue(MotionEnabledProperty, value);
    }

    /// <summary>
    /// 状态机是否认为"动画已启动"。**只给探针读**：夹具必须能读回真实状态，否则
    /// "标签说要动、其实没动"这种失败会伪装成"省下一大截"（实测同一档在一轮里读出过
    /// 0.58% 与 8.51% 两种值，差 15 倍）。
    /// ⚠ 它只反映状态机，不反推"屏幕上真有动画在跑"：若外部直接对椭圆 <c>StopAnimation</c>，
    /// 这里仍是 true —— 所以停必须走 <see cref="MotionEnabled"/>，别去停动画本身。
    /// </summary>
    internal bool IsDriftRunning => _motionStarted;

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
        else if (change.Property == MotionEnabledProperty)
            ApplyMotionPreference();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ApplyMotionPreference();
        QueuePaletteRefresh();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => StopMotion();

    /// <summary>宿主开关与挂载状态一有变化就重算：两者任一不满足都该停。</summary>
    private void ApplyMotionPreference()
    {
        if (MotionEnabled) QueueMotionStart();
        else StopMotion();
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
            || !MotionEnabled || !this.IsAttachedToVisualTree()) return;

        var generation = ++_motionGeneration;
        _motionStartQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _motionStartQueued = false;
            if (generation != _motionGeneration || !MotionEnabled || !this.IsAttachedToVisualTree()) return;
            if (TryStartMotion()) return;
            _motionStartAttempts++;

            if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
            topLevel.RequestAnimationFrame(_ =>
            {
                if (generation == _motionGeneration && MotionEnabled && this.IsAttachedToVisualTree())
                    QueueMotionStart();
            });
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 停掉漂移并把"已启动"标记复位。停止是**隐式**的（任一门控条件不满足就停），
    /// 但重新开始必须显式走到这里 —— 与 ProgressRenderAnimator 同一套约定：
    /// 漏掉复位会让"关掉再打开"变成单向开关，之后再也不会动。
    /// </summary>
    private void StopMotion()
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

    private bool TryStartMotion()
    {
        if (_motionStarted) return true;

        var all = GetLayer(true).Concat(GetLayer(false)).ToArray();
        var visuals = all.Select(ElementComposition.GetElementVisual).ToArray();
        if (visuals.Any(visual => visual is null)) return false;

        // 26s 周期改为"椭圆轨道匀速循环"(8 路标 + 线性插值,闭合回路):
        // 旧实现前 12.5s 是 Hold 段(仅 12px),肉眼里就是"有些时间不动"。
        // 椭圆长轴沿各色团主方向(Burst 方向),短轴 0.6 倍,周长 ~500px / 26s ≈ 20px/s —— 全程恒速可见。
        // A/B 调色层使用同一轨道,切歌交叉淡化时不会跳位。
        var paths = new Vector3[]
        {
            new(148, 58, 0), new(-86, 146, 0), new(-152, -70, 0), new(108, -128, 0), new(-104, 92, 0),
        };

        for (var i = 0; i < all.Length; i++)
        {
            var ellipse = all[i];
            var visual = visuals[i]!;
            var pathIndex = i % paths.Length;

            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            visual.Translation = default;
            visual.Scale = Vector3.One;
            visual.CenterPoint = new Vector3(
                (float)(ellipse.Bounds.Width / 2),
                (float)(ellipse.Bounds.Height / 2),
                0);

            // 椭圆轨道:长轴沿 Burst 方向,幅值取其 0.75 倍。
            var burst = paths[pathIndex];
            var dir = Vector3.Normalize(new Vector3(burst.X, burst.Y, 0));
            var perp = new Vector3(-dir.Y, dir.X, 0);
            var amp = new Vector3(burst.X, burst.Y, 0).Length() * 0.75f;

            // ⚠ 每个路标必须显式传 LinearEasing:不带 easing 的 InsertKeyFrame 不能假定是线性,
            //   段内减速会造成"走一段慢下来"的脉动感(2026-09-23 用户实测)。
            // ⚠ 必须包含进度 0 的原点路标(k=0):漏掉它会让动画在第一个路标上冻住 1/8 周期,
            // 且循环回卷时从终点瞬移回起点 —— 实测观感"一顿一顿"(2026-09-23)。
            var drift = visual.Compositor.CreateVector3DKeyFrameAnimation();
            drift.Target = "Translation";
            drift.Duration = s_motionCycle;
            drift.IterationBehavior = AnimationIterationBehavior.Forever;
            for (var k = 0; k <= 8; k++)
            {
                var angle = k * Math.PI / 4;
                var offset = new Vector3(
                    (float)((Math.Sin(angle) * dir.X - (1 - Math.Cos(angle)) * perp.X) * amp),
                    (float)((Math.Sin(angle) * dir.Y - (1 - Math.Cos(angle)) * perp.Y) * amp),
                    0);
                drift.InsertKeyFrame(k / 8f, offset, s_linearEasing);
            }
            visual.StartAnimation("Translation", drift);
            // 不再叠加缩放呼吸:胀缩脉动会让匀速漂移读成"走一段慢下来"(2026-09-23 用户反馈)。
            // (visual.Scale 已在上面复位为 1,不再启动 Scale 动画。)
        }

        _motionStarted = true;
        return true;
    }

    private static Color[] ExtractPalette(IImage cover)
    {
        const int size = 48;
        // 直接读 Bitmap 像素采样(居中方裁剪,等价于原 UniformToFill):
        // 旧实现经"离屏 Image + RenderTargetBitmap 渲染 + CopyPixels"取像素 —— 该离屏渲染
        // 路径在 Android 上不可靠(渲染为空/全透明,像素全被 alpha 过滤),导致取色数不足,
        // 频繁落入默认蓝紫调色板或 Shift 的固定混色,出现封面里完全没有的颜色(真机实测)。
        if (cover is not Bitmap bitmap)
            return [.. s_defaultPalette];

        var srcW = bitmap.PixelSize.Width;
        var srcH = bitmap.PixelSize.Height;
        if (srcW <= 0 || srcH <= 0) return [.. s_defaultPalette];

        var stride = srcW * 4;
        var buffer = Marshal.AllocHGlobal(stride * srcH);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, srcW, srcH), buffer, stride * srcH, stride);

            // ⚠ CopyPixels 按位图自身格式输出字节 —— Skia 解码格式随平台 N32:Windows=Bgra8888、
            //   Android=Rgba8888。字节序固定按 BGRA 读会在 Android 上 R/B 对调,红色封面提取成
            //   蓝色色团(真机实测)。红色通道偏移由 1×1 纯红 PNG 的解码结果自校准(结果缓存)。
            var redOffset = GetRedByteOffset();
            var blueOffset = 2 - redOffset;

            // UniformToFill 居中方裁剪:取中心 side×side 区域,在 48×48 网格上隔行采样
            var side = Math.Min(srcW, srcH);
            var offsetX = (srcW - side) / 2;
            var offsetY = (srcH - side) / 2;

            var bins = new Dictionary<int, PaletteBin>();
            for (var y = 0; y < size; y += 2)
            for (var x = 0; x < size; x += 2)
            {
                var sx = offsetX + x * side / size;
                var sy = offsetY + y * side / size;
                var offset = sy * stride + sx * 4;
                var b = Marshal.ReadByte(buffer, offset + blueOffset);
                var g = Marshal.ReadByte(buffer, offset + 1);
                var r = Marshal.ReadByte(buffer, offset + redOffset);
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
            // 补齐缺失槽位只在已提取的色系内做明暗变体(同色相家族):
            // 旧实现往固定蓝/紫色混(Shift),会给红/绿色封面掺出封面没有的蓝紫色团(真机实测)。
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

    /// <summary>红色通道在 CopyPixels 输出中的字节偏移(0=Rgba8888 / 2=Bgra8888),进程内缓存。
    /// 自校准:程序内生成 1×1 纯红 PNG(编码像素确定),经平台 Skia 解码后 CopyPixels 读回,
    /// 看 255 落在字节 0(Rgba)还是字节 2(Bgra) —— 与封面位图的解码格式同源,平台差异自动适配。</summary>
    private static int? _redByteOffset;

    private static int GetRedByteOffset()
    {
        if (_redByteOffset.HasValue) return _redByteOffset.Value;

        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            // 生成 1×1 纯红 PNG:裸 BGRA 红 → 平台编码器出 PNG(PNG 无歧义,R=255,G=B=0)
            Marshal.WriteByte(buffer, 0, 0);
            Marshal.WriteByte(buffer, 1, 0);
            Marshal.WriteByte(buffer, 2, 255);
            Marshal.WriteByte(buffer, 3, 255);
            byte[] png;
            using (var src = new Bitmap(
                       Avalonia.Platform.PixelFormat.Bgra8888,
                       Avalonia.Platform.AlphaFormat.Opaque,
                       buffer,
                       new PixelSize(1, 1), new Avalonia.Vector(96, 96), 4))
            using (var ms = new MemoryStream())
            {
                src.Save(ms, PngBitmapEncoderOptions.Default);
                png = ms.ToArray();
            }

            // 经平台解码(与封面同一 N32 路径)后读回,定位红色字节
            using var decoded = new Bitmap(new MemoryStream(png));
            var check = Marshal.AllocHGlobal(4);
            try
            {
                decoded.CopyPixels(new PixelRect(0, 0, 1, 1), check, 4, 4);
                var b0 = Marshal.ReadByte(check, 0);
                var b2 = Marshal.ReadByte(check, 2);
                _redByteOffset = b0 == 255 ? 0 : 2;
            }
            finally
            {
                Marshal.FreeHGlobal(check);
            }
        }
        catch
        {
            _redByteOffset = 2; // 校准失败保守回落 BGRA(桌面端正确)
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return _redByteOffset.Value;
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

    /// <summary>补齐第 3/4 色团:在已提取色的色相家族内做明暗变体(亮/暗交替)。
    /// ⚠ 不得往固定色相混(旧实现混固定蓝/紫) —— 会给暖色封面掺出封面里没有的冷色色团。</summary>
    private static Color Shift(Color color, int index) => index % 2 == 0
        ? Color.FromRgb(
            (byte)(color.R + (255 - color.R) * 0.25),
            (byte)(color.G + (255 - color.G) * 0.25),
            (byte)(color.B + (255 - color.B) * 0.25))
        : Color.FromRgb((byte)(color.R * 0.62), (byte)(color.G * 0.62), (byte)(color.B * 0.62));
}
