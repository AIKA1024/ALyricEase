using System.Globalization;
using Avalonia.Data.Converters;

namespace ALyricEase.Infrastructure;

/// <summary>字号缩放转换器:value(缩放系数)× ConverterParameter(基准字号)→ 实际字号。
/// 详情页歌词字号档位(60%~150%)用,基准字号写在 XAML 里。</summary>
public sealed class FontScaleConverter : IValueConverter
{
    public static FontScaleConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var scale = value is double d ? d : 1.0;
        var baseSize = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 16.0;
        return scale * baseSize;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
