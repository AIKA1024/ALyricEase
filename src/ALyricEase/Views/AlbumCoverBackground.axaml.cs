using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using ALyricEase.Infrastructure;
using ALyricEase.Services.Audio;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Views;

/// <summary>
/// 从封面提取四个代表色，绘制成原版 AlbumCoverBackgroundControl 风格的动态柔光色团。
/// 色团通过逐帧更新合成属性缓慢漂移；切歌时双层交叉淡化，避免整张模糊封面造成的纹理噪声。
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
/// 现实现使用 <c>TopLevel.RequestAnimationFrame</c> 的高精度时间步长更新合成属性，避开旧定时器的低频和量化；
/// 同时保留“看不见就不分析、不请求下一帧”的 <see cref="MotionEnabled"/> 门控。
/// </para>
/// </summary>
public partial class AlbumCoverBackground : UserControl
{
    public static readonly StyledProperty<IImage?> CoverProperty =
        AvaloniaProperty.Register<AlbumCoverBackground, IImage?>(nameof(Cover));

    /// <summary>
    /// 是否让色团漂移。默认 false，宿主只在详情页真正可见时置 true；这也保证 Android
    /// 不会因为控件刚构造、绑定尚未落值而提前申请音频分析权限。
    /// 注意这**只是**省掉动画开销：静止的色团层几乎不要钱（实测详情页可见且漂移停掉时 0.93% GPU），
    /// 所以关掉它不会改变任何观感，只会在切回来时从当前位置继续。
    /// </summary>
    public static readonly StyledProperty<bool> MotionEnabledProperty =
        AvaloniaProperty.Register<AlbumCoverBackground, bool>(nameof(MotionEnabled), defaultValue: false);

    private static readonly Color[] s_defaultPalette =
    [
        Color.FromRgb(53, 74, 112),
        Color.FromRgb(78, 52, 112),
        Color.FromRgb(39, 92, 112),
        Color.FromRgb(106, 53, 91),
    ];

    private static readonly TimeSpan s_motionCycle = TimeSpan.FromSeconds(26);
    private static readonly Vector3[] s_motionPaths =
    [
        new(148, 58, 0), new(-86, 146, 0), new(-152, -70, 0), new(108, -128, 0), new(-104, 92, 0),
    ];

    private bool _showingLayerA = true;
    private bool _motionStarted;
    private bool _motionStartQueued;
    private int _motionStartAttempts;
    private int _motionGeneration;
    private int _paletteVersion;
    private readonly IAudioPlayer? _audioPlayer;
    private CompositionVisual[]? _motionVisuals;
    private TopLevel? _motionTopLevel;
    private TopLevel? _observedTopLevel;
    private TimeSpan _lastMotionTimestamp;
    private bool _hasMotionTimestamp;
    private bool _resumeFromPresentationPause;
    private double _motionPhase;
    private double _smoothedEnergy;
    private double _previousRawEnergy;
    private double _energyBurst;
    private double _burstCooldown;
    private double _burstSuppressionSeconds;
    private double _smoothedSpeed = 0.52;

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
        try
        {
            _audioPlayer = ServiceLocator.Get<IAudioPlayer>();
        }
        catch
        {
            // 设计器/孤立控件测试可能没有建立 DI；此时保留普通慢速漂移。
        }
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
        ObserveTopLevel(TopLevel.GetTopLevel(this));
        ApplyMotionPreference();
        QueuePaletteRefresh();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ObserveTopLevel(null);
        StopMotion();
    }

    /// <summary>宿主开关、挂载状态与窗口可呈现性任一变化都重算。</summary>
    private void ApplyMotionPreference()
    {
        if (!MotionEnabled || !this.IsAttachedToVisualTree())
        {
            StopMotion();
            return;
        }

        ObserveTopLevel(TopLevel.GetTopLevel(this));
        if (!IsHostPresentable())
        {
            SuspendMotion();
            return;
        }

        _audioPlayer?.SetAudioAnalysisEnabled(true);
        QueueMotionStart();
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
            || !MotionEnabled || !this.IsAttachedToVisualTree() || !IsHostPresentable()) return;

        var generation = ++_motionGeneration;
        _motionStartQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _motionStartQueued = false;
            if (generation != _motionGeneration || !MotionEnabled || !this.IsAttachedToVisualTree()
                || !IsHostPresentable()) return;
            if (TryStartMotion()) return;
            _motionStartAttempts++;

            if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
            topLevel.RequestAnimationFrame(_ =>
            {
                if (generation == _motionGeneration && MotionEnabled && this.IsAttachedToVisualTree()
                    && IsHostPresentable())
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
        => HaltMotion(preservePosition: false);

    /// <summary>窗口最小化/隐藏只暂停：保留轨道相位和当前 Translation，恢复后从原地继续。</summary>
    private void SuspendMotion()
        => HaltMotion(preservePosition: true);

    private void HaltMotion(bool preservePosition)
    {
        var hadMotion = _motionStarted || _motionVisuals is not null;
        _motionGeneration++;
        _motionStartQueued = false;
        _motionStartAttempts = 0;
        _audioPlayer?.SetAudioAnalysisEnabled(false);
        if (!preservePosition)
        {
            foreach (var ellipse in GetLayer(true).Concat(GetLayer(false)))
            {
                var visual = ElementComposition.GetElementVisual(ellipse);
                visual?.StopAnimation("Translation");
                visual?.StopAnimation("Scale");
            }
        }
        _motionVisuals = null;
        _motionTopLevel = null;
        _hasMotionTimestamp = false;
        _smoothedEnergy = 0;
        _previousRawEnergy = 0;
        _energyBurst = 0;
        _burstCooldown = 0;
        _smoothedSpeed = 0.52;
        _motionStarted = false;

        if (preservePosition)
        {
            _resumeFromPresentationPause |= hadMotion;
            // 音频分析恢复后的前几个样本会从 0 跳到当前能量，不应被误判成音乐突发。
            _burstSuppressionSeconds = 0.65;
        }
        else
        {
            _resumeFromPresentationPause = false;
            _burstSuppressionSeconds = 0;
            _motionPhase = 0;
        }
    }

    private bool TryStartMotion()
    {
        if (_motionStarted) return true;

        var all = GetLayer(true).Concat(GetLayer(false)).ToArray();
        var visuals = all.Select(ElementComposition.GetElementVisual).ToArray();
        if (visuals.Any(visual => visual is null)) return false;
        var preserveTranslation = _resumeFromPresentationPause;

        for (var i = 0; i < all.Length; i++)
        {
            var ellipse = all[i];
            var visual = visuals[i]!;

            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");
            if (!preserveTranslation)
                visual.Translation = default;
            visual.Scale = Vector3.One;
            visual.CenterPoint = new Vector3(
                (float)(ellipse.Bounds.Width / 2),
                (float)(ellipse.Bounds.Height / 2),
                0);
        }

        _motionVisuals = visuals.Select(visual => visual!).ToArray();
        _motionTopLevel = TopLevel.GetTopLevel(this);
        if (_motionTopLevel is null) return false;
        _hasMotionTimestamp = false;
        _motionStarted = true;
        _resumeFromPresentationPause = false;
        _motionTopLevel.RequestAnimationFrame(OnMotionFrame);
        return true;
    }

    /// <summary>
    /// 高精度帧时钟驱动的音频响应漂移。这里不是之前失败的 DispatcherTimer 降频方案：
    /// RequestAnimationFrame 跟随实际渲染帧并提供高分辨率时间戳，位移直接写合成视觉，
    /// 不触发布局。相位按 dt 积分，所以分析数据只有 20Hz 也不会让位置呈阶梯。
    /// </summary>
    private void OnMotionFrame(TimeSpan timestamp)
    {
        if (!_motionStarted || !MotionEnabled || !this.IsAttachedToVisualTree()
            || _motionVisuals is not { Length: 10 } visuals || _motionTopLevel is null)
            return;
        if (!IsHostPresentable())
        {
            // Avalonia 在最小化时仍可能交付 RAF；这里自断续订并关闭 20Hz 音频分析。
            SuspendMotion();
            return;
        }

        var dt = _hasMotionTimestamp
            ? Math.Clamp((timestamp - _lastMotionTimestamp).TotalSeconds, 1.0 / 240.0, 0.05)
            : 1.0 / 60.0;
        _lastMotionTimestamp = timestamp;
        _hasMotionTimestamp = true;

        var rawEnergy = Math.Clamp(_audioPlayer?.AudioEnergy ?? 0f, 0f, 1f);
        var rise = rawEnergy - _previousRawEnergy;
        _burstCooldown = Math.Max(0, _burstCooldown - dt);
        var suppressBurst = _burstSuppressionSeconds > 0;
        _burstSuppressionSeconds = Math.Max(0, _burstSuppressionSeconds - dt);
        if (!suppressBurst && rise > 0.13 && _burstCooldown <= 0)
        {
            // 只响应明显的强弱跃迁，并留出冷却时间；避免每个采样峰都触发一次加速。
            _energyBurst = Math.Max(_energyBurst, Math.Min(0.65, 0.18 + (rise - 0.13) * 2.8));
            _burstCooldown = 1.35;
        }
        _previousRawEnergy = rawEnergy;
        _energyBurst *= Math.Exp(-dt / 0.85);

        // 快起、慢落：强段很快建立动势，间歇处自然减速而不是跟着 20Hz 样本抖动。
        var tau = rawEnergy > _smoothedEnergy ? 0.14 : 1.05;
        var follow = 1 - Math.Exp(-dt / tau);
        _smoothedEnergy += (rawEnergy - _smoothedEnergy) * follow;

        // 用开方型曲线展开实际音乐常见的中低能量区间：暂停约 50s/圈，普通段约 18~24s，
        // 强段约 12~15s，偶发跃迁可短促进入约 10s/圈，强弱差异肉眼可见。
        var perceivedEnergy = Math.Pow(_smoothedEnergy, 0.58);
        var targetSpeed = 0.52 + 1.72 * perceivedEnergy + _energyBurst;
        // 最终速度再做一层连续过渡，播放/暂停时只改变速度，不会产生速度阶跃。
        var speedTau = targetSpeed > _smoothedSpeed ? 0.24 : 1.10;
        _smoothedSpeed += (targetSpeed - _smoothedSpeed) * (1 - Math.Exp(-dt / speedTau));
        _motionPhase = (_motionPhase + dt * Math.Tau / s_motionCycle.TotalSeconds * _smoothedSpeed) % Math.Tau;

        for (var i = 0; i < visuals.Length; i++)
        {
            var burst = s_motionPaths[i % s_motionPaths.Length];
            var dir = Vector3.Normalize(burst);
            var perp = new Vector3(-dir.Y, dir.X, 0);
            // 音乐只影响沿轨道的速度，不改变轨道半径；否则幅度变化会让当前坐标瞬移。
            var amplitude = burst.Length() * 0.75;
            var offset = new Vector3(
                (float)((Math.Sin(_motionPhase) * dir.X - (1 - Math.Cos(_motionPhase)) * perp.X) * amplitude),
                (float)((Math.Sin(_motionPhase) * dir.Y - (1 - Math.Cos(_motionPhase)) * perp.Y) * amplitude),
                0);
            visuals[i].Translation = offset;
        }

        _motionTopLevel.RequestAnimationFrame(OnMotionFrame);
    }

    /// <summary>最小化与 Hide 都不应继续逐帧计算；TopLevel 尚未解析时保持原启动重试行为。</summary>
    private bool IsHostPresentable()
    {
        if (_observedTopLevel is not { } topLevel) return true;
        if (!topLevel.IsVisible) return false;
        return topLevel is not Window window || window.WindowState != WindowState.Minimized;
    }

    private void ObserveTopLevel(TopLevel? topLevel)
    {
        if (ReferenceEquals(_observedTopLevel, topLevel)) return;
        if (_observedTopLevel is not null)
            _observedTopLevel.PropertyChanged -= OnHostPresentationChanged;
        _observedTopLevel = topLevel;
        if (_observedTopLevel is not null)
            _observedTopLevel.PropertyChanged += OnHostPresentationChanged;
    }

    private void OnHostPresentationChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty && e.Property != Window.WindowStateProperty) return;
        ApplyMotionPreference();
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
