namespace ALyricEase.ViewModels;

/// <summary>左侧导航项:原版 Fluent 图标字形 + 文字。IsHeader 为分组标题(不可点选)。</summary>
public sealed class NavItemViewModel
{
    public NavItemViewModel(string key, string label, string? iconGlyph = null, bool isHeader = false, bool isAccent = false, PlaylistItemViewModel? playlist = null)
    {
        Key = key;
        Label = label;
        IconGlyph = iconGlyph;
        IsHeader = isHeader;
        IsAccent = isAccent;
        Playlist = playlist;
    }

    public string Key { get; }
    public string Label { get; }
    public string? IconGlyph { get; }
    public bool IsHeader { get; }
    public bool IsAccent { get; }
    public PlaylistItemViewModel? Playlist { get; }

    public bool IsItem => !IsHeader;
}
