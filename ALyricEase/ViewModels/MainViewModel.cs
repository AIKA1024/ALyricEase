using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>聚合 VM:主窗口 DataContext。左导航(规格 5 项 + 底部账号/设置)、
/// 内容页(TransitioningContentControl)、正在播放全屏覆盖层、底部播放条。播放自动打开正在播放页。</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;

    public MainViewModel(SearchViewModel search, PlayerViewModel player, LyricViewModel lyric, PlaylistViewModel playlist, RecommendViewModel recommend, ArtistViewModel artist, AlbumViewModel album)
    {
        Search = search;
        Player = player;
        Lyric = lyric;
        Playlist = playlist;
        Recommend = recommend;
        Artist = artist;
        Album = album;
        _playlist = playlist;
        RebuildShellNavigation();
        Playlist.Playlists.CollectionChanged += OnPlaylistsChanged;
        _selectedNav = ShellNavItems.First(item => item.Key == _activePage);
        _ = Recommend.EnsureLoadedAsync(); // 启动即拉首页区块(幂等,失败静默)
    }

    public SearchViewModel Search { get; }
    public PlayerViewModel Player { get; }
    public LyricViewModel Lyric { get; }
    public PlaylistViewModel Playlist { get; }
    public RecommendViewModel Recommend { get; }
    public ArtistViewModel Artist { get; }
    public AlbumViewModel Album { get; }

    public PlaceholderViewModel Placeholder { get; } = new();

    /// <summary>原版导航结构；首阶段未实现的页面仍进入明确占位页。</summary>
    public IReadOnlyList<NavItemViewModel> NavItems { get; } =
    [
        new("DiscoverHeader", "发现", isHeader: true),
        new("Search", "搜索", ""),
        new("Recommend", "个性推荐", ""),
        new("Browse", "浏览", ""),
        new("PersonalStation", "私人FM", ""),
        new("MyMusicHeader", "我的音乐", isHeader: true),
        new("Library", "我的收藏", ""),
        new("CloudDrive", "音乐云盘", ""),
        new("Recents", "最近播放", ""),
        new("PlaylistsHeader", "我的歌单", isHeader: true),
    ];

    public ObservableCollection<NavItemViewModel> ShellNavItems { get; } = new();

    /// <summary>手机底部导航仅保留最常用入口，其他入口在抽屉中。</summary>
    public IReadOnlyList<NavItemViewModel> PrimaryNavItems =>
        ShellNavItems.Where(item => item.Key is "Search" or "Recommend" or "Library" or "Recents").ToArray();

    /// <summary>收起侧边栏(图标栏)显示的项:全部导航项(仿原版 NavigationView 紧凑态,不是只留常用 4 个)。</summary>
    public IReadOnlyList<NavItemViewModel> CompactNavItems =>
        ShellNavItems.Where(item => item.IsItem).ToArray();

    /// <summary>当前导航页键(Home/Recommend/Library/Recents/Favorites/Search/Account/Settings)。</summary>
    [ObservableProperty] private string _activePage = "Recommend";

    /// <summary>正在播放全屏覆盖层。</summary>
    [ObservableProperty] private bool _showNowPlaying;

    /// <summary>桌面/中屏侧栏是否完整展开；汉堡按钮在完整栏和图标栏之间切换。</summary>
    [ObservableProperty] private bool _isNavigationExpanded = true;

    public bool IsNavigationCompact => !IsNavigationExpanded;

    partial void OnIsNavigationExpandedChanged(bool value) => OnPropertyChanged(nameof(IsNavigationCompact));

    /// <summary>预留给后续 Android/触控抽屉的状态；当前小尺寸仍遵循原版图标栏。</summary>
    [ObservableProperty] private bool _isNavigationDrawerOpen;

    private readonly Stack<string> _navigationHistory = new();
    private string _lastPage = "Recommend";
    private bool _isGoingBack;

    /// <summary>页面切换动画方向:返回(true)时反向滑动(新页从左进),前进(false)从右进。
    /// 绑定 TransitioningContentControl.IsTransitionReversed。</summary>
    [ObservableProperty] private bool _isTransitionReversed;


    /// <summary>内容区当前页(TransitioningContentControl 按 VM 类型选模板)。</summary>
    public object? CurrentContent => ActivePage switch
    {
        "Recommend" => Recommend,
        "Search" => Search,
        "Favorites" => Playlist,
        "Artist" => Artist,
        "Album" => Album,
        _ => Placeholder,
    };

    partial void OnActivePageChanged(string value)
    {
        // 返回时反向滑动(GoBack 期间 _isGoingBack=true),前进正向
        IsTransitionReversed = _isGoingBack;
        if (!_isGoingBack && !string.Equals(_lastPage, value, StringComparison.Ordinal))
            _navigationHistory.Push(_lastPage);
        _lastPage = value;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CurrentContent));

        // 未完成页面使用明确占位，不伪装为可用功能。
        if (value is "Browse" or "PersonalStation" or "CloudDrive" or "Recents" or "Account" or "Settings")
        {
            (Placeholder.Title, Placeholder.Description) = value switch
            {
                "Browse" => ("浏览", "Banner、榜单与更多发现内容将在后续阶段接入"),
                "PersonalStation" => ("私人FM", "播放队列能力完成后接入私人FM"),
                "CloudDrive" => ("音乐云盘", "网易云盘接口将在后续阶段接入"),
                "Recents" => ("最近播放", "本地播放历史将在下一阶段接入"),
                "Favorites" => ("我喜欢的音乐", "喜欢列表与收藏操作将在队列阶段接入"),
                "Account" => ("账号", "登录与账户信息"),
                _ => ("设置", "应用设置将在下一阶段接入"),
            };
        }

        if (value == "Recommend")
            _ = Recommend.EnsureLoadedAsync();

        if (value == "Favorites")
            _ = _playlist.EnsureLoadedAsync(); // 已存 MUSIC_U 则恢复并打开“我喜欢的音乐”

        // 从搜索/占位页切回导航项时同步选中;非导航页(搜索/账号/设置)清除选中
        SelectedNav = IsNavItem(value) ? ShellNavItems.FirstOrDefault(n => n.Key == value) : null;
    }

    private static bool IsNavItem(string page) => page is "Search" or "Recommend" or "Browse" or "PersonalStation" or "Library" or "CloudDrive" or "Recents" or "Favorites";

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildShellNavigation();

    private void RebuildShellNavigation()
    {
        ShellNavItems.Clear();
        foreach (var item in NavItems)
            ShellNavItems.Add(item);

        foreach (var playlist in Playlist.Playlists)
            ShellNavItems.Add(new NavItemViewModel($"Playlist:{playlist.Id}", playlist.Name, "", playlist: playlist));
    }

    /// <summary>当前选中导航项(ListBox 双向)。</summary>
    [ObservableProperty] private NavItemViewModel? _selectedNav;

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (value is null || value.IsHeader) return;
        if (value.Playlist is { } playlist)
        {
            OpenShellPlaylistCommand.Execute(playlist);
            return;
        }

        ActivePage = value.Key;
        IsNavigationDrawerOpen = false;
    }

    public bool CanGoBack => _navigationHistory.Count > 0;

    [RelayCommand]
    private void GoBack()
    {
        if (_navigationHistory.Count == 0) return;
        _isGoingBack = true;
        try
        {
            ActivePage = _navigationHistory.Pop();
        }
        finally
        {
            _isGoingBack = false;
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    /// <summary>内容区右上搜索图标。</summary>
    [RelayCommand] private void GoSearch() => ActivePage = "Search";

    [RelayCommand]
    private void OpenShellPlaylist(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        ActivePage = "Favorites";
        Playlist.OpenPlaylistCommand.Execute(playlist);
    }

    /// <summary>歌单行/专辑行点击歌手 → 歌手页。</summary>
    [RelayCommand]
    private async Task OpenArtistAsync(long? artistId)
    {
        if (artistId is null or 0) return;
        ActivePage = "Artist";
        try { await Artist.LoadAsync(artistId.Value); }
        catch { /* 网络失败:停留在歌手页空内容 */ }
    }

    /// <summary>歌单行/歌手页点击专辑 → 专辑页。</summary>
    [RelayCommand]
    private async Task OpenAlbumAsync(long? albumId)
    {
        if (albumId is null or 0) return;
        ActivePage = "Album";
        try { await Album.LoadAsync(albumId.Value); }
        catch { /* 网络失败:停留在专辑页空内容 */ }
    }

    [RelayCommand] private void ToggleNavigationExpanded() => IsNavigationExpanded = !IsNavigationExpanded;

    [RelayCommand] private void ToggleNavigationDrawer() => IsNavigationDrawerOpen = !IsNavigationDrawerOpen;

    [RelayCommand] private void CloseNavigationDrawer() => IsNavigationDrawerOpen = false;

    [RelayCommand]
    private void NavigateCompact(NavItemViewModel? item)
    {
        if (item is { IsItem: true }) SelectedNav = item;
    }

    [RelayCommand] private void GoAccount()
    {
        ActivePage = "Account";
        Placeholder.Title = "账号";
        Placeholder.Description = "登录与账户信息";
    }

    [RelayCommand] private void GoSettings()
    {
        ActivePage = "Settings";
        Placeholder.Title = "设置";
        Placeholder.Description = "应用设置";
    }

    /// <summary>打开正在播放覆盖层(点底部播放条时)。</summary>
    [RelayCommand] private void OpenNowPlaying() => ShowNowPlaying = true;

    /// <summary>收起正在播放覆盖层(返回键/Escape)。</summary>
    [RelayCommand] private void CloseNowPlaying() => ShowNowPlaying = false;
}
