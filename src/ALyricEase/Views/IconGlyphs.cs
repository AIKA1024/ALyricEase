using Avalonia;
using Avalonia.Controls;

namespace ALyricEase.Views;

/// <summary>代码侧图标字形(菜单项、Tab、全屏切换等)。这些码位在
/// <c>Styles/Foundation/Icons.axaml</c> 里已有命名资源(XAML 直接 <c>DynamicResource</c> 引用),
/// 代码统一从这里取,改字形只动资源一处;字面量只作为"取不到资源"时的回落
/// (设计器 / 没有 Application 的纯单测宿主)。</summary>
internal static class IconGlyphs
{
    // ---- 右键/更多菜单 ----
    public static string Share => Resolve("IconShare", "\uE918");

    public static string CopyLink => Resolve("IconCopyLink", "\uE902");

    public static string OpenInBrowser => Resolve("IconOpenInBrowser", "\uE94F");

    public static string Play => Resolve("IconPlay", "\uE912");

    public static string PlayNext => Resolve("IconPlayNext", "\uE913");

    public static string Add => Resolve("IconAdd", "\uE92B");

    public static string Album => Resolve("IconAlbum", "\uE922");

    public static string SingleArtist => Resolve("IconSingleArtist", "\uE921");

    public static string MultiArtists => Resolve("IconMultiArtists", "\uE920");

    public static string Person => Resolve("IconPerson", "\uE910");

    public static string Remove => Resolve("IconRemove", "\uE932");

    public static string ShowInFolder => Resolve("IconShowInFolder", "\uF4DD");

    /// <summary>下载(arrow_download_24_regular)。注意:该码位在全量 Fluent 字体里,
    /// 原版子集字体(FluentUISystemIcons)只有 E900~E94A,渲染须配 LyricEaseIconsOutline。</summary>
    public static string Download => Resolve("IconDownload", "\uF151");

    // ---- 搜索结果类型 Tab ----
    public static string TabAll => Resolve("IconTabAll", "\uE933");

    public static string TabTrack => Resolve("IconTabTrack", "\uE90B");

    public static string TabPlaylist => Resolve("IconTabPlaylist", "\uE905");

    // ---- 正在播放页全屏切换 ----
    public static string EnterFullScreen => Resolve("IconEnterFullScreen", "\uE690");

    public static string ExitFullScreen => Resolve("IconExitFullScreen", "\uE693");

    private static string Resolve(string key, string fallback)
        => Application.Current is { } app && app.TryFindResource(key, out var value) && value is string glyph
            ? glyph
            : fallback;
}
