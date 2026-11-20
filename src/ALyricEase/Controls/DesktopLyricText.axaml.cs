using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ALyricEase.Models;

namespace ALyricEase.Controls;

/// <summary>
/// 桌面歌词的一行文字:几何描边层 + 逐字正文层(见 .axaml 里的说明)。
/// 字体字号字重不用在这里声明:<see cref="TextElement"/> 的这几项都是继承属性,
/// 写在控件上会自动传给内部两层。
/// </summary>
public partial class DesktopLyricText : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DesktopLyricText, string?>(nameof(Text));

    /// <summary>正文颜色(逐字时是"已唱"部分的颜色)。</summary>
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<DesktopLyricText, IBrush?>(nameof(TextBrush));

    /// <summary>逐字时"未唱"部分的颜色。null 时由正文控件给 35% 白。</summary>
    public static readonly StyledProperty<IBrush?> DimTextBrushProperty =
        AvaloniaProperty.Register<DesktopLyricText, IBrush?>(nameof(DimTextBrush));

    /// <summary>描边颜色。null 时描边层不画。</summary>
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<DesktopLyricText, IBrush?>(nameof(OutlineBrush));

    /// <summary>描边宽度(px,向外扩)。0 = 不显示描边层。</summary>
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

    /// <summary>当前行的逐字时间轴。null = 没有逐字,正文退回单色。</summary>
    public static readonly StyledProperty<IReadOnlyList<LyricWord>?> WordsProperty =
        AvaloniaProperty.Register<DesktopLyricText, IReadOnlyList<LyricWord>?>(nameof(Words));

    /// <summary>歌词时间轴位置(已叠加 offset,单位 ms)。</summary>
    public static readonly StyledProperty<long> PositionMsProperty =
        AvaloniaProperty.Register<DesktopLyricText, long>(nameof(PositionMs));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<DesktopLyricText, bool>(nameof(IsPlaying), defaultValue: true);

    /// <summary>逐字高亮开关(设置项)。关掉时正文始终单色,和改动前完全一致。</summary>
    public static readonly StyledProperty<bool> ShowKaraokeProperty =
        AvaloniaProperty.Register<DesktopLyricText, bool>(nameof(ShowKaraoke), defaultValue: true);

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

    public IBrush? DimTextBrush
    {
        get => GetValue(DimTextBrushProperty);
        set => SetValue(DimTextBrushProperty, value);
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

    public IReadOnlyList<LyricWord>? Words
    {
        get => GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public long PositionMs
    {
        get => GetValue(PositionMsProperty);
        set => SetValue(PositionMsProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool ShowKaraoke
    {
        get => GetValue(ShowKaraokeProperty);
        set => SetValue(ShowKaraokeProperty, value);
    }

    public DesktopLyricText()
    {
        InitializeComponent();
        ApplyOutline();
        ApplyTextLayout();
        ApplyKaraokeState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OutlineThicknessProperty || change.Property == OutlineBrushProperty) ApplyOutline();
        else if (change.Property == MaxTextHeightProperty || change.Property == MaxTextLinesProperty) ApplyTextLayout();
        else if (change.Property == TextFontFamilyProperty) ApplyFontFamily();
        else if (change.Property == WordsProperty || change.Property == ShowKaraokeProperty) ApplyKaraokeState();
    }

    /// <summary>
    /// 字体只改内部两层的局部值。回到"默认"时用 ClearValue 清掉局部值让属性继续继承 ——
    /// 给 FontFamily 塞 null 会让排版里拿不到字体家族。
    /// </summary>
    private void ApplyFontFamily()
    {
        var family = TextFontFamily;
        foreach (var layer in new Control[] { Outline, Body })
        {
            if (family is null) layer.ClearValue(TextElement.FontFamilyProperty);
            else layer.SetValue(TextElement.FontFamilyProperty, family);
        }
    }

    /// <summary>描边开关只改描边层的可见性。关掉时连 FormattedText 都不建。</summary>
    private void ApplyOutline()
    {
        var thickness = OutlineThickness;
        var visible = thickness > 0 && OutlineBrush is not null;
        Outline.IsVisible = visible;
        if (visible) Outline.StrokeThickness = thickness;
    }

    /// <summary>
    /// 高度上限两种模式互斥:给了行数就按行数 + 省略号(预览区,末行收干净),
    /// 否则按像素高度硬裁(歌词窗口,超长行裁到窗口放得下为止)。两层必须拿到同一套参数,
    /// 否则描边和正文的换行位置会错开。
    /// </summary>
    private void ApplyTextLayout()
    {
        var maxLines = MaxTextLines;
        var trimming = maxLines > 0 ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        var maxHeight = maxLines > 0 ? double.PositiveInfinity : MaxTextHeight;

        Outline.MaxLines = maxLines;
        Outline.TextTrimming = trimming;
        Outline.MaxTextHeight = maxHeight;

        Body.MaxLines = maxLines;
        Body.TextTrimming = trimming;
        Body.MaxTextHeight = maxHeight;
    }

    /// <summary>逐字只在"这一行有字级时间 + 开关打开"时启用;否则正文单色。</summary>
    private void ApplyKaraokeState()
    {
        Body.IsActive = ShowKaraoke && Words is { Count: > 0 };
    }
}
