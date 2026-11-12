using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ALyricEase.Controls;

/// <summary>色板选项:一条记录同时供按钮背景和 HEX 提示使用。</summary>
public sealed class ColorPaletteEntry
{
    private ColorPaletteEntry(string hex)
    {
        Hex = hex;
        Color = Color.Parse(hex);
        Brush = new SolidColorBrush(Color);
    }

    public string Hex { get; }
    public Color Color { get; }
    public IBrush Brush { get; }

    /// <summary>4 行 × 8 列:中性色一行 + 三行彩色,和 Fluent 2 色板一样按明度/色相排。</summary>
    public static IReadOnlyList<ColorPaletteEntry> CreatePalette()
    {
        var hexes = new[]
        {
            "#FFFFFFFF", "#FFF2F2F2", "#FFC9C9C9", "#FF8F8F8F", "#FF5A5A5A", "#FF333333", "#FF1A1A1A", "#FF000000",
            "#FFFF5A5A", "#FFFF8A5A", "#FFFFB13B", "#FFFFE066", "#FFB6E36A", "#FF6FDC8C", "#FF4ECDC4", "#FF72E5D3",
            "#FF5AC8FA", "#FF5B9BFF", "#FF7A7AFF", "#FFA98BFF", "#FFD08BFF", "#FFFF8AD0", "#FFFF6E8A", "#FFB0846A",
            "#FFFFC9C9", "#FFFFE0B2", "#FFFFF3B0", "#FFF0FFC2", "#FFC8F7D0", "#FFC2F1FF", "#FFD6D6FF", "#FFE8D6FF",
        };
        var entries = new ColorPaletteEntry[hexes.Length];
        for (var i = 0; i < hexes.Length; i++) entries[i] = new ColorPaletteEntry(hexes[i]);
        return entries;
    }
}

/// <summary>
/// 色块 + HEX 的颜色选择控件(见 .axaml)。<see cref="Color"/> 默认双向绑定,
/// 用法:<c>&lt;controls:ColorSwatchPicker Color="{CompiledBinding TextColor, Mode=TwoWay}" /&gt;</c>
/// </summary>
public partial class ColorSwatchPicker : UserControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorSwatchPicker, Color>(
            nameof(Color), Colors.White, defaultBindingMode: BindingMode.TwoWay);

    /// <summary>无障碍名称:控件本身不可聚焦,名字要落到里面那个按钮上。</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<ColorSwatchPicker, string?>(nameof(Label));

    private bool _syncingHex;

    public ColorSwatchPicker()
    {
        InitializeComponent();
        ApplyColor();
    }

    public IReadOnlyList<ColorPaletteEntry> Palette { get; } = ColorPaletteEntry.CreatePalette();

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorProperty) ApplyColor();
    }

    /// <summary>色块、色值文字、HEX 输入框三处保持同一个值;写回输入框时要挡住回环。</summary>
    private void ApplyColor()
    {
        var color = Color;
        Swatch.Background = new SolidColorBrush(color);
        var hex = color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        HexLabel.Text = hex;
        if (string.Equals(HexBox.Text, hex, StringComparison.OrdinalIgnoreCase)) return;
        _syncingHex = true;
        HexBox.Text = hex;
        _syncingHex = false;
    }

    private void OnPaletteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ColorPaletteEntry entry) Color = entry.Color;
    }

    private void OnHexChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingHex) return;
        var text = (sender as TextBox)?.Text;
        if (string.IsNullOrWhiteSpace(text) || !Color.TryParse(text.Trim(), out var parsed)) return;
        Color = parsed;
    }
}
