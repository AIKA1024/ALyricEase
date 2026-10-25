using System;
using System.Globalization;
using ALyricEase.Models;
using ALyricEase.Views;
using Avalonia.Data.Converters;

namespace ALyricEase.Infrastructure;

/// <summary>SearchKind → 结果页 Tab 字形;字形统一由 <see cref="IconGlyphs"/> 从
/// Icons.axaml 的 IconTab*/IconAlbum 等资源取(配 LyricEaseIconsOriginal 字体渲染)。</summary>
public sealed class SearchTabIconConverter : IValueConverter
{
    public static readonly SearchTabIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SearchKind.Track => IconGlyphs.TabTrack,
        SearchKind.Album => IconGlyphs.Album,
        SearchKind.Artist => IconGlyphs.SingleArtist,
        SearchKind.Playlist => IconGlyphs.TabPlaylist,
        SearchKind.User => IconGlyphs.Person,
        _ => IconGlyphs.TabAll,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
