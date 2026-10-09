using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ALyricEase.Infrastructure;

/// <summary>封面 Image 转换器:封面已加载(Cover 非 null)返回封面,否则(未加载/失败)返回共享占位图。
/// 占位图静态懒加载一份(避免每容器 new 800x800 Bitmap),单 Image 即可,无需两层 Image 叠加。</summary>
public sealed class CoverImageConverter : IValueConverter
{
    public const string PlaceholderUri = "avares://ALyricEase/Assets/Placeholders/AlbumCoverPlaceholder.png";

    private static IImage? _placeholder;

    /// <summary>XAML 用 {x:Static infra:CoverImageConverter.Instance} 引用。</summary>
    public static CoverImageConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value as IImage ?? GetPlaceholder();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static IImage GetPlaceholder()
    {
        if (_placeholder is null)
        {
            using var stream = AssetLoader.Open(new Uri(PlaceholderUri));
            _placeholder = new Bitmap(stream);
        }
        return _placeholder;
    }
}
