using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>聚合 VM:主窗口 DataContext。左导航(规格 5 项 + 底部账号/设置)、
/// 内容页(TransitioningContentControl)、正在播放全屏覆盖层、底部播放条。播放自动打开正在播放页。</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;

    public MainViewModel(SearchViewModel search, PlayerViewModel player, LyricViewModel lyric, PlaylistViewModel playlist, RecommendViewModel recommend)
    {
        Search = search;
        Player = player;
        Lyric = lyric;
        Playlist = playlist;
        Recommend = recommend;
        _playlist = playlist;
        Player.SongStarted += OnSongStarted;
        _ = Recommend.EnsureLoadedAsync(); // 启动即拉首页区块(幂等,失败静默)
    }

    public SearchViewModel Search { get; }
    public PlayerViewModel Player { get; }
    public LyricViewModel Lyric { get; }
    public PlaylistViewModel Playlist { get; }
    public RecommendViewModel Recommend { get; }

    public PlaceholderViewModel Placeholder { get; } = new();

    /// <summary>左侧导航(规格 Prompt 1 五项)。</summary>
    public IReadOnlyList<NavItemViewModel> NavItems { get; } =
    [
        new("Home", "首页", "M 12,3 L 22,12 L 19.5,12 L 19.5,21 L 14,21 L 14,15 L 10,15 L 10,21 L 4.5,21 L 4.5,12 L 2,12 Z"),
        new("Recommend", "推荐", "M 12,2 L 14.4,8.2 L 21,9 L 16,13.4 L 17.8,20 L 12,16.4 L 6.2,20 L 8,13.4 L 3,9 L 9.6,8.2 Z"),
        new("Library", "我的音乐", "M 12,3 L 12,12.5 C 11.4,12.2 10.7,12 10,12 C 8.3,12 7,13.3 7,15 C 7,16.7 8.3,18 10,18 C 11.7,18 13,16.7 13,15 L 13,5 L 19,4 L 19,12.5 C 18.4,12.2 17.7,12 17,12 C 15.3,12 14,13.3 14,15 C 14,16.7 15.3,18 17,18 C 18.7,18 20,16.7 20,15 L 20,3 Z"),
        new("Recents", "播放记录", "M 12,2 C 6.477,2 2,6.477 2,12 C 2,17.523 6.477,22 12,22 C 17.523,22 22,17.523 22,12 C 22,6.477 17.523,2 12,2 Z M 11,6 L 13,6 L 13,12.4 L 16.5,14.5 L 15.5,16.2 L 11,13.4 Z"),
        new("Favorites", "收藏歌曲", "M 12,20.5 C 12,20.5 2.5,15 2.5,8.5 C 2.5,5.5 4.8,3.5 7.5,3.5 C 9.3,3.5 11,4.5 12,6 C 13,4.5 14.7,3.5 16.5,3.5 C 19.2,3.5 21.5,5.5 21.5,8.5 C 21.5,15 12,20.5 12,20.5 Z"),
    ];

    /// <summary>当前导航页键(Home/Recommend/Library/Recents/Favorites/Search/Account/Settings)。</summary>
    [ObservableProperty] private string _activePage = "Home";

    /// <summary>正在播放全屏覆盖层。</summary>
    [ObservableProperty] private bool _showNowPlaying;

    /// <summary>内容区当前页(TransitioningContentControl 按 VM 类型选模板)。</summary>
    public object? CurrentContent => ActivePage switch
    {
        "Home" => Recommend,
        "Search" => Search,
        "Library" => Playlist,
        _ => Placeholder,
    };

    private static readonly object s_nowPlayingToken = new();

    /// <summary>正在播放覆盖层的哨兵对象(非 null 才渲染模板)。</summary>
    public object? NowPlayingContent => ShowNowPlaying ? s_nowPlayingToken : null;

    partial void OnShowNowPlayingChanged(bool value) => OnPropertyChanged(nameof(NowPlayingContent));

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentContent));

        // 占位页标题(推荐/播放记录/收藏歌曲/账号/设置)
        if (value is "Recommend" or "Recents" or "Favorites" or "Account" or "Settings")
        {
            (Placeholder.Title, Placeholder.Description) = value switch
            {
                "Recommend" => ("推荐", "为你推荐的新歌与热门内容"),
                "Recents" => ("播放记录", "近期播放过的歌曲"),
                "Favorites" => ("收藏歌曲", "你喜欢的歌曲"),
                "Account" => ("账号", "登录与账户信息"),
                _ => ("设置", "应用设置"),
            };
        }

        if (value == "Home")
            _ = Recommend.EnsureLoadedAsync(); // 回到首页时刷新(登录态变化也会触发)

        if (value == "Library")
            _ = _playlist.EnsureLoadedAsync(); // 已存 MUSIC_U 则恢复登录态

        // 从搜索/占位页切回导航项时同步选中;非导航页(搜索/账号/设置)清除选中
        SelectedNav = IsNavItem(value) ? NavItems.FirstOrDefault(n => n.Key == value) : null;
    }

    private static bool IsNavItem(string page) => page is "Home" or "Recommend" or "Library" or "Recents" or "Favorites";

    /// <summary>当前选中导航项(ListBox 双向)。</summary>
    [ObservableProperty] private NavItemViewModel? _selectedNav;

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (value is null || value.IsHeader) return;
        ActivePage = value.Key;
    }

    /// <summary>内容区右上搜索图标。</summary>
    [RelayCommand] private void GoSearch() => ActivePage = "Search";

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

    /// <summary>打开正在播放覆盖层(点底部播放条或播放歌曲时)。</summary>
    [RelayCommand] private void OpenNowPlaying() => ShowNowPlaying = true;

    /// <summary>收起正在播放覆盖层(返回键/Escape)。</summary>
    [RelayCommand] private void CloseNowPlaying() => ShowNowPlaying = false;

    private void OnSongStarted() => ShowNowPlaying = true;
}
