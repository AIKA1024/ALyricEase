using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ALyricEase.Controls;

/// <summary>文字描边层:用 <see cref="FormattedText.BuildGeometry"/> 把整段文字转成矢量轮廓,
/// 再用 <see cref="Pen"/> 描一圈。相比"8 份偏移副本",这里只有一份排版、一次绘制,
/// 粗细均匀、斜角不会变厚(8 方向单位向量在角上是 1.41 倍)。
///
/// <para>
/// **为什么可以折行**:Avalonia 12 的 <see cref="FormattedText"/> 内部封装了 TextLayout,
/// 设了 <see cref="FormattedText.MaxTextWidth"/> 之后 <c>BuildGeometry</c> 返回的是**多行**
/// 轮廓(实测 4 行文本的 GeometryGroup 有 4 个子项,末行还带着居中偏移)。
/// 早先"它不支持换行"的判断是查 XML 文档得出的,文档没写,实测有 —— 见 <c>--text-geometry</c>。
/// </para>
///
/// <para>
/// 与正文层(<see cref="KaraokeText"/> / TextBlock)共用同一个格子和同一个宽度,两边都按
/// <see cref="TextAlignment"/> 与 <see cref="MaxTextHeight"/> 排版,所以行高与换行位置一致
/// (实测两者 height 完全相同)。描边画在正文下面,内半圈被正文盖住,外扩正好等于
/// <see cref="StrokeThickness"/> —— 和原来的 8 层副本等效。
/// </para>
/// </summary>
public sealed class TextOutlineLayer : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TextOutlineLayer, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TextOutlineLayer, IBrush?>(nameof(Stroke));

    /// <summary>描边向外扩的宽度(px)。实际 Pen 宽度是它的 2 倍(描边沿轮廓居中)。</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<TextOutlineLayer, double>(nameof(StrokeThickness));

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<TextOutlineLayer, TextAlignment>(nameof(TextAlignment));

    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        AvaloniaProperty.Register<TextOutlineLayer, TextTrimming>(nameof(TextTrimming));

    public static readonly StyledProperty<int> MaxLinesProperty =
        AvaloniaProperty.Register<TextOutlineLayer, int>(nameof(MaxLines));

    public static readonly StyledProperty<double> MaxTextHeightProperty =
        AvaloniaProperty.Register<TextOutlineLayer, double>(nameof(MaxTextHeight), double.PositiveInfinity);

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<TextOutlineLayer>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<TextOutlineLayer>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<TextOutlineLayer>();

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    public TextTrimming TextTrimming { get => GetValue(TextTrimmingProperty); set => SetValue(TextTrimmingProperty, value); }
    public int MaxLines { get => GetValue(MaxLinesProperty); set => SetValue(MaxLinesProperty, value); }
    public double MaxTextHeight { get => GetValue(MaxTextHeightProperty); set => SetValue(MaxTextHeightProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    private FormattedText? _formatted;
    private Geometry? _geometry;
    private Pen? _pen;
    private IBrush? _penBrush;
    private double _penThickness;

    private string? _builtText;
    private double _builtWidth = -1;
    private double _builtFontSize = -1;
    private FontWeight _builtFontWeight;
    private FontFamily? _builtFontFamily;
    private TextAlignment _builtAlignment;
    private TextTrimming _builtTrimming;
    private int _builtMaxLines;
    private double _builtMaxHeight;

    static TextOutlineLayer()
    {
        AffectsMeasure<TextOutlineLayer>(TextProperty, FontSizeProperty, FontWeightProperty, FontFamilyProperty,
            TextAlignmentProperty, TextTrimmingProperty, MaxLinesProperty, MaxTextHeightProperty);
        AffectsRender<TextOutlineLayer>(TextProperty, StrokeProperty, StrokeThicknessProperty,
            FontSizeProperty, FontWeightProperty, FontFamilyProperty, TextAlignmentProperty,
            TextTrimmingProperty, MaxLinesProperty, MaxTextHeightProperty);
    }

    public TextOutlineLayer()
    {
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(1, availableSize.Width) : 800;
        EnsureBuilt(width);
        if (_formatted is null) return default;
        return new Size(_formatted.Width, Math.Min(_formatted.Height, MaxTextHeight));
    }

    public override void Render(DrawingContext context)
    {
        if (_geometry is null || Stroke is null || StrokeThickness <= 0) return;

        // Pen 宽度取 2 倍:描边沿轮廓居中,外半边才是用户看到的"描边宽度"。
        var thickness = StrokeThickness * 2;
        if (_pen is null || !ReferenceEquals(_penBrush, Stroke) || Math.Abs(_penThickness - thickness) > 0.001)
        {
            _pen = new Pen(Stroke, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            _penBrush = Stroke;
            _penThickness = thickness;
        }

        using var clip = context.PushClip(new Rect(0, 0, Bounds.Width, Bounds.Height));
        context.DrawGeometry(null, _pen, _geometry);
    }

    private void EnsureBuilt(double maxWidth)
    {
        var text = Text ?? "";
        if (_geometry is not null && text == _builtText
            && Math.Abs(maxWidth - _builtWidth) < 0.5
            && Math.Abs(FontSize - _builtFontSize) < 0.01
            && FontWeight == _builtFontWeight
            && Equals(FontFamily, _builtFontFamily)
            && TextAlignment == _builtAlignment
            && TextTrimming == _builtTrimming
            && MaxLines == _builtMaxLines
            && Math.Abs(MaxTextHeight - _builtMaxHeight) < 0.5)
            return;

        _builtText = text;
        _builtWidth = maxWidth;
        _builtFontSize = FontSize;
        _builtFontWeight = FontWeight;
        _builtFontFamily = FontFamily;
        _builtAlignment = TextAlignment;
        _builtTrimming = TextTrimming;
        _builtMaxLines = MaxLines;
        _builtMaxHeight = MaxTextHeight;

        if (string.IsNullOrEmpty(text))
        {
            _formatted = null;
            _geometry = null;
            return;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight),
            FontSize,
            Brushes.White)
        {
            MaxTextWidth = maxWidth,
            TextAlignment = TextAlignment,
            Trimming = TextTrimming,
        };
        if (double.IsFinite(MaxTextHeight)) formatted.MaxTextHeight = MaxTextHeight;
        if (MaxLines > 0) formatted.MaxLineCount = MaxLines;

        _formatted = formatted;
        _geometry = formatted.BuildGeometry(new Point(0, 0));
    }
}
