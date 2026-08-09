using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>聚合 VM:主窗口 DataContext。左导航(发现/我的音乐/歌单分组,对齐 LyricEase)、
/// 内容页、正在播放全屏覆盖层、底部播放条。播放自动打开正在播放页。</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    public MainViewModel(SearchViewModel search, PlayerViewModel player, LyricViewModel lyric, PlaylistViewModel playlist)
    {
        Search = search;
        Player = player;
        Lyric = lyric;
        Playlist = playlist;
        Player.SongStarted += OnSongStarted;
    }

    public SearchViewModel Search { get; }
    public PlayerViewModel Player { get; }
    public LyricViewModel Lyric { get; }
    public PlaylistViewModel Playlist { get; }

    /// <summary>左侧导航项(分组头 + 导航项,对齐 LyricEase 的 NavigationView)。</summary>
    public IReadOnlyList<NavItemViewModel> NavItems { get; } =
    [
        new("", "发现", isHeader: true),
        new("Search", "搜索", "M 10,2 C 5.582,2 2,5.582 2,10 C 2,14.418 5.582,18 10,18 C 11.848,18 13.553,17.371 14.905,16.32 L 20.293,21.707 L 21.707,20.293 L 16.32,14.905 C 17.371,13.553 18,11.848 18,10 C 18,5.582 14.418,2 10,2 Z M 10,4 C 13.309,4 16,6.691 16,10 C 16,13.309 13.309,16 10,16 C 6.691,16 4,13.309 4,10 C 4,6.691 6.691,4 10,4 Z"),
        new("Recommend", "推荐", "M 12,2 L 14.4,8.2 L 21,9 L 16,13.4 L 17.8,20 L 12,16.4 L 6.2,20 L 8,13.4 L 3,9 L 9.6,8.2 Z"),
        new("Browse", "浏览", "M 12,2 C 6.5,2 2,6.5 2,12 C 2,17.5 6.5,22 12,22 C 17.5,22 22,17.5 22,12 C 22,6.5 17.5,2 12,2 Z M 9.5,9.5 L 15.5,8 L 13.5,13.5 L 7.5,15 Z"),
        new("PersonalStation", "私人FM", "M 12,2 C 8.5,2 5.5,3.5 3.5,6 L 5.5,8 C 7.2,6.1 9.5,5 12,5 C 14.5,5 16.8,6.1 18.5,8 L 20.5,6 C 18.5,3.5 15.5,2 12,2 Z M 12,8 C 10,8 8.2,8.8 7,10 L 9,12 C 9.8,11.4 10.8,11 12,11 C 13.2,11 14.2,11.4 15,12 L 17,10 C 15.8,8.8 14,8 12,8 Z"),
        new("", "我的音乐", isHeader: true),
        new("Library", "音乐库", "M 3,3 L 9,3 L 9,21 L 3,21 Z M 11,3 L 17,3 L 17,21 L 11,21 Z M 19,4.5 L 21,4.5 L 21,21 L 19,21 Z"),
        new("CloudDrive", "云盘", "M 7,18 C 4.239,18 2,15.761 2,13 C 2,10.239 4.239,8 7,8 C 8.042,5.659 10.327,4 13,4 C 16.314,4 19,6.686 19,10 C 20.434,10 22,11.566 22,13.5 C 22,15.434 20.434,17 18.5,17 Z M 12,8 L 8,12 L 10.5,12 L 10.5,16 L 13.5,16 L 13.5,12 L 16,12 Z"),
        new("Recents", "最近播放", "M 12,2 C 6.477,2 2,6.477 2,12 C 2,17.523 6.477,22 12,22 C 17.523,22 22,17.523 22,12 C 22,6.477 17.523,2 12,2 Z M 11,6 L 13,6 L 13,12.4 L 16.5,14.5 L 15.5,16.2 L 11,13.4 Z"),
        new("", "歌单", isHeader: true),
        new("Likelist", "我喜欢的音乐", "M 12,20.5 C 12,20.5 2.5,15 2.5,8.5 C 2.5,5.5 4.8,3.5 7.5,3.5 C 9.3,3.5 11,4.5 12,6 C 13,4.5 14.7,3.5 16.5,3.5 C 19.2,3.5 21.5,5.5 21.5,8.5 C 21.5,15 12,20.5 12,20.5 Z"),
    ];

    /// <summary>当前导航页键(Search/Recommend/Browse/PersonalStation/Library/CloudDrive/Recents/Likelist/Account/Settings)。</summary>
    [ObservableProperty] private string _activePage = "Search";

    /// <summary>正在播放全屏覆盖层。</summary>
    [ObservableProperty] private bool _showNowPlaying;

    public bool ShowSearch => ActivePage == "Search";
    public bool ShowPlaylist => ActivePage == "Likelist";
    public bool ShowPlaceholder => ActivePage is not ("Search" or "Likelist");

    /// <summary>占位页显示的标题(未实现页面的名字)。</summary>
    public string PlaceholderTitle => ActivePage switch
    {
        "Recommend" => "推荐",
        "Browse" => "浏览",
        "PersonalStation" => "私人FM",
        "Library" => "音乐库",
        "CloudDrive" => "云盘",
        "Recents" => "最近播放",
        "Likelist" => "我喜欢的音乐",
        "Account" => "账号",
        "Settings" => "设置",
        _ => ActivePage,
    };

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(ShowPlaylist));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(PlaceholderTitle));
    }

    /// <summary>当前选中导航项(ListBox 双向)。组头点击忽略。</summary>
    [ObservableProperty] private NavItemViewModel? _selectedNav;

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (value is null) return;
        if (value.IsHeader) { SelectedNav = null; return; }
        ActivePage = value.Key;
    }

    [RelayCommand] private void GoSearch() => ActivePage = "Search";
    [RelayCommand] private void GoRecommend() => ActivePage = "Recommend";
    [RelayCommand] private void GoBrowse() => ActivePage = "Browse";
    [RelayCommand] private void GoPersonalStation() => ActivePage = "PersonalStation";
    [RelayCommand] private void GoLibrary() => ActivePage = "Library";
    [RelayCommand] private void GoCloudDrive() => ActivePage = "CloudDrive";
    [RelayCommand] private void GoRecents() => ActivePage = "Recents";
    [RelayCommand]
    private void GoLikelist()
    {
        ActivePage = "Likelist";
        _ = Playlist.EnsureLoadedAsync(); // 已存 MUSIC_U 则恢复登录态
    }
    [RelayCommand] private void GoAccount() => ActivePage = "Account";
    [RelayCommand] private void GoSettings() => ActivePage = "Settings";

    /// <summary>打开正在播放覆盖层(点底部播放条或播放歌曲时)。</summary>
    [RelayCommand] private void OpenNowPlaying() => ShowNowPlaying = true;

    /// <summary>收起正在播放覆盖层(返回键/Escape)。</summary>
    [RelayCommand] private void CloseNowPlaying() => ShowNowPlaying = false;

    private void OnSongStarted() => ShowNowPlaying = true;
}
