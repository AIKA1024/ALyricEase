using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ALyricEase.Controls;

/// <summary>
/// 桌面歌词的一行文字,支持描边(见 .axaml 里的说明)。
/// 字体字号字重不用在这里声明:<see cref="TextBlock"/> 的这几项都是
/// <c>TextElement</c> 的继承属性,写在控件上就会传给内部所有副本。
/// </summary>
public partial class DesktopLyricText : UserControl
{
    /// <summary>描边副本相对正文的 8 个方向(单位向量;对角线也取 1,角上稍厚一点反而更自然)。</summary>
    private static readonly (double X, double Y)[] OutlineDirections =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DesktopLyricText, string?>(nameof(Text));

    /// <summary>正文颜色。</summary>
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<DesktopLyricText, IBrush?>(nameof(TextBrush));

    /// <summary>描边颜色。null 时描边层保持隐藏。</summary>
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<DesktopLyricText, IBrush?>(nameof(OutlineBrush));

    /// <summary>描边宽度(px)。0 = 不显示描边层。</summary>
    public static readonly StyledProperty<double> OutlineThicknessProperty =
        AvaloniaProperty.Register<DesktopLyricText, double>(nameof(OutlineThickness));

    /// <summary>字体家族;null = 跟随应用默认字体(显式 ClearValue 回继承,不写局部空值)。</summary>
    public static readonly StyledProperty<FontFamily?> TextFontFamilyProperty =
        AvaloniaProperty.Register<DesktopLyricText, FontFamily?>(nameof(TextFontFamily));

    /// <summary>文本块的最大高度(超出后裁掉),避免超长歌词把窗口撑到屏幕上沿。</summary>
    public static readonly StyledProperty<double> MaxTextHeightProperty =
        AvaloniaProperty.Register<DesktopLyricText, double>(nameof(MaxTextHeight), double.PositiveInfinity);

    /// <summary>
    /// 最多显示几行,0 = 不限。设了行数就改用省略号收尾(末行截断),不再按像素高度硬裁 ——
    /// 弹窗预览用它,免得大字号时长歌词被裁在半行上。
    /// </summary>
    public static readonly StyledProperty<int> MaxTextLinesProperty =
        AvaloniaProperty.Register<DesktopLyricText, int>(nameof(MaxTextLines));

    private readonly TextBlock[] _outlineCopies;
    private readonly TextBlock[] _allCopies;

    public DesktopLyricText()
    {
        InitializeComponent();
        _outlineCopies = new[]
        {
            Outline0, Outline1, Outline2, Outline3, Outline4, Outline5, Outline6, Outline7,
        };
        _allCopies = _outlineCopies.Append(Body).ToArray();
        ApplyOutline();
        ApplyTextLayout();
    }
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public FontFamily? TextFontFamily
    {
        get => GetValue(TextFontFamilyProperty);
        set => SetValue(TextFontFamilyProperty, value);
    }

    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public double OutlineThickness
    {
        get => GetValue(OutlineThicknessProperty);
        set => SetValue(OutlineThicknessProperty, value);
    }

    public double MaxTextHeight
    {
        get => GetValue(MaxTextHeightProperty);
        set => SetValue(MaxTextHeightProperty, value);
    }

    public int MaxTextLines
    {
        get => GetValue(MaxTextLinesProperty);
        set => SetValue(MaxTextLinesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OutlineThicknessProperty) ApplyOutline();
        else if (change.Property == MaxTextHeightProperty || change.Property == MaxTextLinesProperty) ApplyTextLayout();
        else if (change.Property == TextFontFamilyProperty) ApplyFontFamily();
    }

    /// <summary>
    /// 字体只改内部副本的局部值。回到"默认"时用 ClearValue 清掉局部值让属性继续继承 ——
    /// 给 TextBlock.FontFamily 塞 null 会让排版里拿不到字体家族。
    /// </summary>
    private void ApplyFontFamily()
    {
        var family = TextFontFamily;
        foreach (var copy in _allCopies)
        {
            if (family is null) copy.ClearValue(TextBlock.FontFamilyProperty);
            else copy.FontFamily = family;
        }
    }

    /// <summary>
    /// 描边开关只改副本的可见性和偏移。关掉时 IsVisible=false,连测量都省掉 ——
    /// 不给它们设"透明色"是因为那样 8 份文字照样参与排版。
    /// </summary>
    private void ApplyOutline()
    {
        var thickness = OutlineThickness;
        var visible = thickness > 0;
        for (var i = 0; i < _outlineCopies.Length; i++)
        {
            var copy = _outlineCopies[i];
            copy.IsVisible = visible;
            if (!visible) continue;
            var (x, y) = OutlineDirections[i];
            copy.RenderTransform = new TranslateTransform(x * thickness, y * thickness);
        }
    }

    /// <summary>
    /// 高度上限两种模式互斥:给了行数就按行数 + 省略号(预览区,末行收干净),
    /// 否则按像素高度硬裁(歌词窗口,超长行裁到窗口放得下为止)。
    /// </summary>
    private void ApplyTextLayout()
    {
        var maxLines = MaxTextLines;
        var maxHeight = MaxTextHeight;
        foreach (var copy in _allCopies)
        {
            if (maxLines > 0)
            {
                copy.MaxLines = maxLines;
                copy.TextTrimming = TextTrimming.CharacterEllipsis;
                copy.MaxHeight = double.PositiveInfinity;
            }
            else
            {
                copy.MaxLines = 0;
                copy.TextTrimming = TextTrimming.None;
                copy.MaxHeight = maxHeight;
            }
        }
    }
}
