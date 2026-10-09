using System.ComponentModel;

namespace ALyricEase.ViewModels;

/// <summary>左侧导航项:原版 Fluent 图标字形 + 文字。
/// IsHeader 为分组标题(点按展开/收起其下歌单子项);歌单子项经 OwnerKey 关联到所属分组头,
/// ShowAsChild 跟随分组开合(收起时隐藏行但保留在集合中,已打开的详情页与选中态不受影响)。
/// 两个可变属性走最小 INPC 实现:仅这两处需要绑定刷新,其余属性只读。</summary>
public sealed class NavItemViewModel : INotifyPropertyChanged
{
    public NavItemViewModel(string key, string label, string? iconGlyph = null, bool isHeader = false, bool isAccent = false, PlaylistItemViewModel? playlist = null, bool isToggleGroup = false, bool hasAddButton = false, string? addToolTip = null, Models.AggregatePlaylist? aggregate = null, Models.LocalPlaylist? localPlaylist = null)
    {
        Key = key;
        Label = label;
        IconGlyph = iconGlyph;
        IsHeader = isHeader;
        IsAccent = isAccent;
        Playlist = playlist;
        IsToggleGroup = isToggleGroup;
        HasAddButton = hasAddButton;
        AddToolTip = addToolTip;
        Aggregate = aggregate;
        LocalPlaylist = localPlaylist;
    }

    public string Key { get; }
    public string Label { get; }
    public string? IconGlyph { get; }
    public bool IsHeader { get; }
    public bool IsAccent { get; }
    public PlaylistItemViewModel? Playlist { get; }

    /// <summary>聚合歌单(聚合分组下的子项;打开时合并各成员歌单曲目)。</summary>
    public Models.AggregatePlaylist? Aggregate { get; }

    /// <summary>本地音乐歌单(本地音乐分组下的子项;曲目即时可得,无网络参与)。</summary>
    public Models.LocalPlaylist? LocalPlaylist { get; }

    /// <summary>可折叠歌单分组头(网易云音乐/QQ 音乐):带箭头、可点展开收起、有 hover 反馈;
    /// 与普通纯标题("发现/我的音乐")区分。</summary>
    public bool IsToggleGroup { get; }

    /// <summary>折叠分组头右侧是否带"+"添加入口(聚合歌单/网易云音乐/QQ 音乐分组头用,
    /// 点击经 MainViewModel.NavHeaderAddCommand 按 Key 分发)。</summary>
    public bool HasAddButton { get; }

    /// <summary>"+"按钮的 ToolTip 文案(配合 HasAddButton)。</summary>
    public string? AddToolTip { get; }

    /// <summary>容器行是否启用交互:普通导航项与折叠分组头启用,纯标题保持禁用(无 hover/选中)。</summary>
    public bool IsInteractive => !IsHeader || IsToggleGroup;

    /// <summary>所属分组头的 Key(仅歌单子项设置,如 "QqPlaylistsHeader")。</summary>
    public string? OwnerKey { get; init; }

    /// <summary>是否歌单子项(挂 OwnerKey 的行):参与分组开合的 Min/MaxHeight 收缩动画;
    /// 普通导航行不参与,保持基主题的 40px 行高。</summary>
    public bool IsPlaylistChild => OwnerKey is not null;

    private bool _isExpanded = true;

    /// <summary>分组头专用:分组是否展开(驱动箭头朝向)。头部为静态共享实例,状态跨导航重建保留。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, IsExpandedArgs);
        }
    }

    private bool _showAsChild = true;

    /// <summary>歌单子项专用:所在分组是否处于展开态(false 时隐藏该行)。</summary>
    public bool ShowAsChild
    {
        get => _showAsChild;
        set
        {
            if (_showAsChild == value) return;
            _showAsChild = value;
            PropertyChanged?.Invoke(this, ShowAsChildArgs);
        }
    }

    public bool IsItem => !IsHeader;

    private static readonly PropertyChangedEventArgs IsExpandedArgs = new(nameof(IsExpanded));
    private static readonly PropertyChangedEventArgs ShowAsChildArgs = new(nameof(ShowAsChild));

    public event PropertyChangedEventHandler? PropertyChanged;
}
