using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using ALyricEase.Infrastructure;
using ALyricEase.Models;

namespace ALyricEase.Controls;

/// <summary>
/// 逐字歌词渲染:底层画一遍暗色全文,上层用 <see cref="DrawingContext.PushClip"/> 按"已唱进度"
/// 裁剪后再画一遍亮色全文。
///
/// <para>
/// **为什么自绘**:一个 <see cref="TextBlock"/> 只有一个 Foreground,做不出"一句里前半亮后半暗"。
/// 另一条路(双 TextBlock + <c>OpacityMask</c>)会走离屏合成,与歌词行挂 <c>Effect</c> 是同一条贵管线;
/// 这里用几何裁剪,成本只是这一行文字画两遍。
/// </para>
///
/// <para>
/// **只对当前行启用**:<see cref="IsActive"/> 负责开关。非当前行只画一遍单色,视觉与原来的 TextBlock 一致
/// (外层 <c>ListBoxItem</c> 的 Opacity 仍然生效)。
/// </para>
///
/// <para>
/// **位置靠插值**:播放器的 PositionChanged 约 5Hz,直接喂给逐字会肉眼跳字。控件记住最近一次
/// <see cref="PositionMs"/> 样本与到达时刻,用 <see cref="Stopwatch"/> 外推,按帧重绘。
/// 暂停(<see cref="IsPlaying"/>=false)立即停泵,不继续外推。
/// </para>
/// </summary>
public sealed class KaraokeText : Control
{
    private static readonly IBrush DefaultDim = new ImmutableSolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush DefaultAccent = Brushes.White;

    /// <summary>偏差超过这个值就当成 seek/切歌,直接对齐而不是平滑追。</summary>
    private const double SeekResyncThresholdMs = 800;

    /// <summary>单帧最多推进多少 ms。窗口被遮挡/系统挂起后 Stopwatch 的间隔可能是几秒,不能一次跳完。</summary>
    private const double MaxFrameStepMs = 200;

    /// <summary>落后时每帧吸收的偏差比例(≈0.15s 时间常数,肉眼看不出加速)。</summary>
    private const double CatchUpRate = 0.12;

    /// <summary>超前时每帧吸收的偏差比例(≈3s 时间常数)。慢到不可察觉,只为不让领先量长期累积。</summary>
    private const double EaseBackRate = 0.005;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<KaraokeText, string?>(nameof(Text));

    /// <summary>逐字时间轴。null/空 = 这一行没有逐字数据,控件退化为单色文本。</summary>
    public static readonly StyledProperty<IReadOnlyList<LyricWord>?> WordsProperty =
        AvaloniaProperty.Register<KaraokeText, IReadOnlyList<LyricWord>?>(nameof(Words));

    /// <summary>歌词时间轴上的位置(已叠加 offset,单位 ms)。</summary>
    public static readonly StyledProperty<long> PositionMsProperty =
        AvaloniaProperty.Register<KaraokeText, long>(nameof(PositionMs));

    /// <summary>这一行是不是"正在唱"的那行。只有它为 true 才会按帧推进与双色渲染。</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<KaraokeText, bool>(nameof(IsActive));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<KaraokeText, bool>(nameof(IsPlaying), defaultValue: true);

    /// <summary>未唱部分的颜色(当前行默认 35% 白;40% 会和下一行的整体亮度撞上)。</summary>
    public static readonly StyledProperty<IBrush?> DimBrushProperty =
        AvaloniaProperty.Register<KaraokeText, IBrush?>(nameof(DimBrush));

    /// <summary>已唱部分的颜色。</summary>
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<KaraokeText, IBrush?>(nameof(AccentBrush));

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<KaraokeText>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<KaraokeText>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<KaraokeText>();

    /// <summary>对齐方式。详情页左对齐,桌面歌词居中 —— 必须和同格里的其它文字层一致,
    /// 否则裁剪用的 x 坐标会和实际字形错位。</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<KaraokeText, TextAlignment>(nameof(TextAlignment));

    /// <summary>超出时的收尾方式(桌面歌词预览用省略号,歌词窗口用硬裁)。</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        AvaloniaProperty.Register<KaraokeText, TextTrimming>(nameof(TextTrimming));

    /// <summary>最多几行,0 = 不限。与 <see cref="TextTrimming"/> 配合使用。</summary>
    public static readonly StyledProperty<int> MaxLinesProperty =
        AvaloniaProperty.Register<KaraokeText, int>(nameof(MaxLines));

    /// <summary>高度上限(px)。超出部分不参与布局,按 <see cref="ClipToBounds"/> 裁掉。</summary>
    public static readonly StyledProperty<double> MaxTextHeightProperty =
        AvaloniaProperty.Register<KaraokeText, double>(nameof(MaxTextHeight), double.PositiveInfinity);

    /// <summary>所在页面是否处于可呈现状态。详情页是靠 RenderTransform 平移出窗的,
    /// `IsVisible` 全程为 true ⇒ 逐字帧循环必须靠这个开关停泵,否则收起后仍在跑。</summary>
    public static readonly StyledProperty<bool> IsPageActiveProperty =
        AvaloniaProperty.Register<KaraokeText, bool>(nameof(IsPageActive), defaultValue: true);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IReadOnlyList<LyricWord>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public long PositionMs { get => GetValue(PositionMsProperty); set => SetValue(PositionMsProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public IBrush? DimBrush { get => GetValue(DimBrushProperty); set => SetValue(DimBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    public TextTrimming TextTrimming { get => GetValue(TextTrimmingProperty); set => SetValue(TextTrimmingProperty, value); }
    public int MaxLines { get => GetValue(MaxLinesProperty); set => SetValue(MaxLinesProperty, value); }
    public double MaxTextHeight { get => GetValue(MaxTextHeightProperty); set => SetValue(MaxTextHeightProperty, value); }
    public bool IsPageActive { get => GetValue(IsPageActiveProperty); set => SetValue(IsPageActiveProperty, value); }

    /// <summary>只给探针读:当前实际画出来的歌词位置(ms)。见 <c>--karaoke-smooth</c>。</summary>
    internal double RenderedPositionMsForProbe => _renderedMs;

    /// <summary>只给探针读:是否有一帧在排队(链是否活着)。</summary>
    internal bool IsFramePendingForProbe => _framePending;

    private TextLayout? _dimLayout;
    private TextLayout? _litLayout;
    private string? _layoutText;
    private double _layoutWidth = -1;
    private double _layoutFontSize = -1;
    private FontWeight _layoutFontWeight;
    private FontFamily? _layoutFontFamily;
    private TextAlignment _layoutAlignment;
    private IBrush? _layoutDimBrush;
    private IBrush? _layoutAccentBrush;
    private TextTrimming _layoutTrimming;
    private int _layoutMaxLines;
    private double _layoutMaxHeight;

    /// <summary>
    /// 采样与渲染位置分离:采样(<see cref="_sampleMs"/> + <see cref="_sampleAt"/>)只用来给出
    /// "此刻应该在哪"的目标值,真正画出来的 <see cref="_renderedMs"/> 由 <see cref="Stopwatch"/>
    /// 按帧推进,再按比例吸收两者的偏差。
    ///
    /// <para>
    /// **为什么不能直接拿采样当位置**:播放器的 PositionChanged 是 200ms 一次的轮询,本身带台阶,
    /// 而且会**回退**(<c>PlayerViewModel</c> 为此专门留了 1500ms 的噪声容差)。早先的写法是
    /// "每次采样到达就把渲染位置重置成采样值",于是每 200ms 亮区就往回退一截 ——
    /// 桌面歌词逐字看得很清楚,就是用户说的"每句都卡一下 / 随机卡住"。
    /// 详情页只按整行变亮,同样的回退看不出来,所以这个问题只在逐字上暴露。
    /// </para>
    /// </summary>
    private long _sampleMs;

    private long _sampleAt;

    /// <summary>实际绘制的歌词位置(ms),连续量,只前进不后退(见 <see cref="AdvancePosition"/>)。</summary>
    private double _renderedMs;

    /// <summary>上一帧的时间戳;0 = 刚起泵/刚暂停,下一帧不推进(只校正偏差)。</summary>
    private long _frameAt;

    /// <summary>是否已经拿到过采样(首帧用它定位,不能从 0 开始推)。</summary>
    private bool _hasSample;

    /// <summary>Pacer 是否被本控件持有。与"有没有帧在排队"分开:前者管 UI 线程唤醒,后者管渲染链。</summary>
    private bool _pacerHeld;

    /// <summary>
    /// 是否已有一帧在排队。<see cref="RequestFrame"/> 用它去重 —— 每次采样到达都会尝试补帧
    /// (看门狗),但只有链真的断了才会多排一次。
    /// </summary>
    private bool _framePending;

    static KaraokeText()
    {
        AffectsMeasure<KaraokeText>(TextProperty, WordsProperty, IsActiveProperty, FontSizeProperty, FontWeightProperty,
            FontFamilyProperty, TextAlignmentProperty, TextTrimmingProperty, MaxLinesProperty, MaxTextHeightProperty);
        AffectsRender<KaraokeText>(TextProperty, WordsProperty, IsActiveProperty, DimBrushProperty, AccentBrushProperty,
            FontSizeProperty, FontWeightProperty, FontFamilyProperty, TextAlignmentProperty, TextTrimmingProperty,
            MaxLinesProperty, MaxTextHeightProperty);
    }

    public KaraokeText()
    {
        _sampleAt = Stopwatch.GetTimestamp();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PositionMsProperty)
        {
            // 只登记采样,**不**重置 _renderedMs:采样会回退(轮询台阶 + 播放器噪声),
            // 一次硬重置就是一次肉眼可见的倒退。对齐交给 AdvancePosition 按阈值判断。
            _sampleMs = PositionMs;
            _sampleAt = Stopwatch.GetTimestamp();

            // 例外:没在跑帧的时候没有任何插值可言(暂停、非当前行),必须直接跟随采样,
            // 否则暂停时拖进度条 / 切行后画面不跟着更新。播放中的采样则一律交给平滑推进。
            var animate = CanRun;
            if (!_hasSample || !animate)
            {
                _hasSample = true;
                _renderedMs = _sampleMs;
                _frameAt = 0;
            }

            if (animate)
                RequestFrame();
            else if (CanShowHighlight)
                InvalidateVisual(); // 暂停拖动也要显示新位置；单色行只需保留采样。
        }
        else if (change.Property == DimBrushProperty || change.Property == AccentBrushProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == WordsProperty)
        {
            // 容器复用或逐字数据晚到时，选中态可能没有变化。
            Kick();
        }
        else if (change.Property == IsPageActiveProperty || change.Property == IsPlayingProperty)
        {
            Kick();
            if (CanShowHighlight) InvalidateVisual();
        }
        else if (change.Property == IsActiveProperty)
        {
            // 刚变成当前行时这一行没有"最近的位置"可用:偏差大就对齐(首次变当前行通常是 0 → 30s),
            // 偏差小(相邻行切换)则保持连续推进,不要因为换行把亮区拽一下。
            if (IsActive && _hasSample && Math.Abs(_sampleMs - _renderedMs) > SeekResyncThresholdMs)
                _renderedMs = _sampleMs;
            _frameAt = 0;

            Kick();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 新树里不可能有上一次 attach 排下的帧:清掉残留标记,否则 RequestFrame 会一直跳过。
        _framePending = false;
        // 窗口从隐藏恢复(暂停隐藏 / 桌面歌词开关)时控件是重新 attach 的:
        // 之前只有 Detach 停泵、没有 Attach 起泵,恢复后要等下一次采样才动 —— 那 200ms 就是"卡一下"。
        Kick();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopLoop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 800;
        EnsureLayouts(width);
        if (_litLayout is null) return default;
        return new Size(_litLayout.Width, Math.Min(_litLayout.Height, MaxTextHeight));
    }

    public override void Render(DrawingContext context)
    {
        if (_litLayout is null || _dimLayout is null) return;

        // MaxTextHeight 只限制布局高度,超出部分要靠这里裁掉(与 TextBlock.MaxHeight 的行为对齐)。
        using var frame = context.PushClip(new Rect(0, 0, Bounds.Width, Bounds.Height));

        // 没有逐字 / 不是当前行:单色全文,和原来的 TextBlock 一致
        if (!HasHighlight)
        {
            _litLayout.Draw(context, default);
            return;
        }

        _dimLayout.Draw(context, default);

        var lit = (long)_renderedMs;
        var y = 0.0;
        foreach (var line in _litLayout.TextLines)
        {
            var height = line.Height;
            var charCount = Math.Max(0, line.Length - line.NewLineLength);
            // ⚠ TextLine.Start 是"行起点距段落左边的距离"(居中对齐时 ≈250),不是字符索引;
            // 早先拿它当索引用,左对齐单行永远为 0 所以没暴露,居中的桌面歌词直接整行不亮。
            // 字符索引用 FirstTextSourceIndex,行内左边界用 Start。
            if (charCount > 0 && line.FirstTextSourceIndex < (_layoutText?.Length ?? 0))
            {
                var right = ResolveRightEdge(line.FirstTextSourceIndex, charCount, lit, line.Start, line.Width);
                if (right > 0)
                {
                    using (context.PushClip(new Rect(0, y, right, height)))
                        _litLayout.Draw(context, default);
                }
            }

            y += height;
        }
    }

    /// <summary>算这一行已经亮到哪个 x。0 = 整行还没开始。</summary>
    /// <param name="lineStart">这一行第一个字符在整段文本里的索引(FirstTextSourceIndex)。</param>
    /// <param name="lineCharCount">这一行的字符数(不含换行符)。</param>
    /// <param name="lineX">行起点在布局里的 x(居中对齐时不为 0)。</param>
    /// <param name="lineWidth">行宽(不含行尾空白)。</param>
    private double ResolveRightEdge(int lineStart, int lineCharCount, long positionMs, double lineX, double lineWidth)
    {
        var words = Words!;
        var lineEnd = lineStart + lineCharCount;
        var lineRight = lineX + lineWidth;

        var charCursor = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var wordLength = word.Text.Length;
            var spanStart = charCursor;
            var spanEnd = charCursor + wordLength;
            charCursor = spanEnd;

            // 这一行之前就唱完的字:整行覆盖到行尾
            if (spanEnd <= lineStart) continue;

            // 这一行之后才唱的字:到此为止
            if (spanStart >= lineEnd) break;

            if (positionMs >= word.EndMs)
            {
                // 当前行内已经完全唱完:继续看下一个字,直到越过行尾
                continue;
            }

            if (positionMs <= word.StartMs)
            {
                // 这个字还没开始:亮到它的起点(行首用行的左边界,不是 0)
                var startX = spanStart <= lineStart ? lineX : CaretX(spanStart);
                return Math.Clamp(startX, lineX, lineRight);
            }

            // 字唱到一半:按字宽插值
            var duration = word.EndMs - word.StartMs;
            var fraction = duration > 0 ? (positionMs - word.StartMs) / (double)duration : 1;
            var from = spanStart <= lineStart ? lineX : CaretX(spanStart);
            var to = Math.Min(spanEnd, lineEnd) >= lineEnd ? lineRight : CaretX(spanEnd);
            // CaretX 和裁剪边界都包含居中偏移，须在该行的实际横坐标内限幅。
            return Math.Clamp(from + (to - from) * fraction, lineX, lineRight);
        }

        // 这一行全部唱完
        return lineRight;
    }

    private double CaretX(int charIndex)
    {
        var clamped = Math.Clamp(charIndex, 0, _layoutText?.Length ?? 0);
        return _litLayout!.HitTestTextPosition(clamped).X;
    }

    private void EnsureLayouts(double maxWidth)
    {
        var text = ResolveText();
        if (string.IsNullOrEmpty(text))
        {
            _dimLayout = _litLayout = null;
            _layoutText = null;
            return;
        }

        if (_litLayout is not null && text == _layoutText
            && Math.Abs(maxWidth - _layoutWidth) < 0.5
            && Math.Abs(FontSize - _layoutFontSize) < 0.01
            && FontWeight == _layoutFontWeight
            && Equals(FontFamily, _layoutFontFamily)
            && TextAlignment == _layoutAlignment
            && TextTrimming == _layoutTrimming
            && MaxLines == _layoutMaxLines
            && MaxTextHeight == _layoutMaxHeight
            && ReferenceEquals(DimBrush, _layoutDimBrush)
            && ReferenceEquals(AccentBrush, _layoutAccentBrush))
            return;

        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        var dim = DimBrush ?? DefaultDim;
        var accent = AccentBrush ?? DefaultAccent;

        _dimLayout = CreateLayout(text, typeface, dim, maxWidth);
        _litLayout = CreateLayout(text, typeface, accent, maxWidth);
        _layoutText = text;
        _layoutWidth = maxWidth;
        _layoutFontSize = FontSize;
        _layoutFontWeight = FontWeight;
        _layoutFontFamily = FontFamily;
        _layoutAlignment = TextAlignment;
        _layoutTrimming = TextTrimming;
        _layoutMaxLines = MaxLines;
        _layoutMaxHeight = MaxTextHeight;
        _layoutDimBrush = DimBrush;
        _layoutAccentBrush = AccentBrush;
    }

    /// <summary>渲染文本:当前行用 yrc 的字拼接(与字级时间严格对应),其余行用 LRC 原文
    /// (与改动前的显示完全一致)。两者只可能在括号上有差异,由 LrcParser 的对齐规则兜底。</summary>
    private string ResolveText() =>
        IsActive && Words is { Count: > 0 } words
            ? string.Concat(words.Select(w => w.Text))
            : Text ?? "";

    private TextLayout CreateLayout(string text, Typeface typeface, IBrush brush, double maxWidth) =>
        new(text,
            typeface,
            FontSize,
            brush,
            TextAlignment,
            TextWrapping.Wrap,
            TextTrimming,
            null,
            FlowDirection.LeftToRight,
            maxWidth,
            MaxTextHeight,
            double.NaN,
            0,
            MaxLines);

    private bool HasHighlight => IsActive && Words is { Count: > 0 };

    private bool CanShowHighlight => HasHighlight && IsPageActive && VisualRoot is not null;
    private bool CanRun => IsPlaying && CanShowHighlight;

    /// <summary>播放状态、当前行、逐字数据或视图挂载状态改变时，同步帧泵。</summary>
    private void Kick()
    {
        if (!CanRun)
        {
            StopLoop();
            return;
        }

        if (!_pacerHeld)
        {
            _pacerHeld = true;
            UiFramePacer.Acquire();
        }

        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_framePending) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        _framePending = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    /// <summary>停泵并清掉"下一帧"的计时基准:恢复后的第一帧只做偏差校正,不按暂停时长推进。</summary>
    private void StopLoop()
    {
        // 故意不清 _framePending:已经排进 render timer 的那次回调还会执行,
        // 它会看到 !CanRun 自行退出。提前清掉会让"暂停后立刻恢复"多排一帧(双倍重绘)。
        _frameAt = 0;

        if (!_pacerHeld) return;
        _pacerHeld = false;
        UiFramePacer.Release();
    }

    private void OnFrame(TimeSpan _)
    {
        _framePending = false;

        if (!CanRun)
        {
            StopLoop();
            return;
        }

        AdvancePosition();
        InvalidateVisual();
        RequestFrame();
    }

    /// <summary>
    /// 把渲染位置往前推一帧,并吸收与采样的偏差。
    ///
    /// <para>
    /// 推进量取 <see cref="Stopwatch"/> 的真实间隔(不是"采样差"),所以采样的台阶和回退
    /// 都不会传成视觉上的抖动;采样只用来算目标值 <c>target</c>:
    /// </para>
    /// <list type="bullet">
    /// <item>偏差 &gt; <see cref="SeekResyncThresholdMs"/> ⇒ 当成 seek/切歌,直接对齐;</item>
    /// <item>落后 ⇒ 每帧吸收 <see cref="CatchUpRate"/>,0.15s 左右追上,看不出加速;</item>
    /// <item>超前 ⇒ 只用 <see cref="EaseBackRate"/> 极慢吸收,防止"卡顿一次就永久领先";</item>
    /// <item>**任何情况下都不后退** —— 逐字亮区往回走比短暂停顿更刺眼。</item>
    /// </list>
    /// </summary>
    private void AdvancePosition()
    {
        if (!_hasSample) return;

        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _sampleAt) * 1000.0 / Stopwatch.Frequency;
        var target = _sampleMs + elapsedMs;

        var deltaMs = _frameAt == 0 ? 0 : (now - _frameAt) * 1000.0 / Stopwatch.Frequency;
        _frameAt = now;
        if (deltaMs > MaxFrameStepMs) deltaMs = MaxFrameStepMs;

        var error = target - _renderedMs;
        if (Math.Abs(error) > SeekResyncThresholdMs)
        {
            _renderedMs = target;
            return;
        }

        var step = deltaMs + error * (error > 0 ? CatchUpRate : EaseBackRate);
        if (step > MaxFrameStepMs) step = MaxFrameStepMs;
        if (step > 0) _renderedMs += step;
    }
}
