using Avalonia.Media;

namespace ALyricEase.ViewModels;

/// <summary>左侧导航项:图标 + 文字。IsHeader 为分组标题(不可点选)。</summary>
public sealed class NavItemViewModel
{
    public NavItemViewModel(string key, string label, string? iconPath = null, bool isHeader = false)
    {
        Key = key;
        Label = label;
        IconData = iconPath is null ? null : StreamGeometry.Parse(iconPath);
        IsHeader = isHeader;
    }

    public string Key { get; }
    public string Label { get; }
    public Geometry? IconData { get; }
    public bool IsHeader { get; }

    public bool IsItem => !IsHeader;
}
