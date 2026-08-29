using System;
using System.Globalization;
using ALyricEase.Models;
using Avalonia.Data.Converters;

namespace ALyricEase.Infrastructure;

/// <summary>SearchKind → 结果页 Tab 字形(与原版 FontIcons.xaml 相同码点,
/// 配 LyricEaseIconsOriginal 字体渲染,见 Icons.axaml 的 IconTab* 资源)。</summary>
public sealed class SearchTabIconConverter : IValueConverter
{
    public static readonly SearchTabIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SearchKind.Track => "\uE90B",      // MusicNote
        SearchKind.Album => "\uE922",      // Album
        SearchKind.Artist => "\uE921",     // SingleArtist
        SearchKind.Playlist => "\uE905",   // ListRegular
        SearchKind.User => "\uE910",       // Person
        _ => "\uE933",                     // ContentGallery(全部)
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
