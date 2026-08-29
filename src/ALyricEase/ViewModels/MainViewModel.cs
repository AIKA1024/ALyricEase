using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using ALyricEase.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>正在播放页右侧面板:无 / 歌词 / 播放列表(原版 PlaybackDetailView 的两个切换按钮互斥)。</summary>
public enum NowPlayingPanel
{
    None,
    Lyrics,
    Queue,
}

/// <summary>聚合 VM:主窗口 DataContext。左导航(规格 5 项 + 底部账号/设置)、
/// 内容页(TransitioningContentControl)、正在播放全屏覆盖层、底部播放条。播放自动打开正在播放页。</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;

    public MainViewModel(SearchViewModel search, PlayerViewModel player, LyricViewModel lyric, PlaylistViewModel playlist, RecommendViewModel recommend, ArtistViewModel artist, AlbumViewModel album, SettingsViewModel settings, Services.AppStateStore appState)
    {
        Search = search;
        Player = player;
        Lyric = lyric;
        Playlist = playlist;
        Recommend = recommend;
        Artist = artist;
        Album = album;
        Settings = settings;
        _playlist = playlist;
        AppState = appState;
        AddAggregateDialog = new AddAggregateDialogViewModel(playlist, appState, OnAggregateConfirmed);
        AggregateSettingsDialog = new AggregateSettingsDialogViewModel(OnAggregateSettingsSaved);
        CreatePlaylistDialog = new CreatePlaylistDialogViewModel(playlist, OnCreatePlaylistConfirmed);
        RenamePlaylistDialog = new RenamePlaylistDialogViewModel(playlist, OnRenamePlaylistConfirmed);
        // 折叠状态必须先于首次 RebuildShellNavigation 恢复(静态分组头实例随即被导航渲染消费)
        NetEasePlaylistsHeader.IsExpanded = appState.IsNetEaseGroupExpanded;
        QqPlaylistsHeader.IsExpanded = appState.IsQqGroupExpanded;
        RebuildShellNavigation();
        Playlist.Playlists.CollectionChanged += OnPlaylistsChanged;
        Playlist.QqPlaylists.CollectionChanged += OnPlaylistsChanged;
        Playlist.PropertyChanged += OnPlaylistLoginChanged;
        _selectedNav = ShellNavItems.First(item => item.Key == _activePage);
        _ = Recommend.EnsureLoadedAsync(); // 启动即拉首页区块(幂等,失败静默)
        _ = Playlist.EnsureQqLoadedAsync(); // 启动恢复 QQ 登录态并拉侧边栏"QQ音乐"分组(失败静默)
    }

    public SearchViewModel Search { get; }
    public PlayerViewModel Player { get; }
    public LyricViewModel Lyric { get; }
    public PlaylistViewModel Playlist { get; }
    public RecommendViewModel Recommend { get; }
    public ArtistViewModel Artist { get; }
    public AlbumViewModel Album { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>界面状态存储(折叠状态;主窗口大小/位置由 MainWindow 读写同一实例)。</summary>
    public Services.AppStateStore AppState { get; }

    /// <summary>添加聚合歌单对话框 VM(宿主绑定 AddAggregateDialogView;打开前 Refresh)。</summary>
    public AddAggregateDialogViewModel AddAggregateDialog { get; }

    /// <summary>聚合歌单设置对话框 VM(宿主绑定 AggregateSettingsDialogView;打开前 Refresh)。</summary>
    public AggregateSettingsDialogViewModel AggregateSettingsDialog { get; }

    /// <summary>创建歌单对话框 VM(宿主绑定 CreatePlaylistDialogView;打开前按音源 Refresh)。</summary>
    public CreatePlaylistDialogViewModel CreatePlaylistDialog { get; }

    /// <summary>重命名歌单对话框 VM(宿主绑定 RenamePlaylistDialogView;打开前按目标歌单 Refresh)。</summary>
    public RenamePlaylistDialogViewModel RenamePlaylistDialog { get; }

    public PlaceholderViewModel Placeholder { get; } = new();

    /// <summary>Debug 页 VM(仅 DEBUG 构建有左下角入口可看,Release 无入口不可达)。</summary>
    public DebugViewModel Debug { get; } = new();

    /// <summary>是否 Debug 构建(DEBUG 条件编译):控制侧边栏底部 Debug 入口显隐。</summary>
#if DEBUG
    public bool IsDebug => true;
#else
    public bool IsDebug => false;
#endif

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
    ];

    /// <summary>网易云/QQ 歌单分组标题:均登录后才显示,故不进静态 NavItems,由 RebuildShellNavigation 按登录态插入。
    /// 头部是静态共享实例,展开/收起状态随实例保留,跨导航重建与登录变化不丢。
    /// 聚合歌单在两者之上:任一音源登录即出现(占位,展开空;"+"后续接入选择两源歌单加入聚合)。</summary>
    private static readonly NavItemViewModel NetEasePlaylistsHeader = new("PlaylistsHeader", "网易云音乐", isHeader: true, isToggleGroup: true, hasAddButton: true, addToolTip: "创建新歌单");
    private static readonly NavItemViewModel QqPlaylistsHeader = new("QqPlaylistsHeader", "QQ音乐", isHeader: true, isToggleGroup: true, hasAddButton: true, addToolTip: "创建新歌单");
    private static readonly NavItemViewModel AggregatePlaylistsHeader = new("AggregatePlaylistsHeader", "聚合歌单", isHeader: true, isToggleGroup: true, hasAddButton: true, addToolTip: "添加歌单到聚合");

    public ObservableCollection<NavItemViewModel> ShellNavItems { get; } = new();

    /// <summary>手机底部导航仅保留最常用入口，其他入口在抽屉中。</summary>
    public IReadOnlyList<NavItemViewModel> PrimaryNavItems =>
        ShellNavItems.Where(item => item.Key is "Search" or "Recommend" or "Library" or "Recents").ToArray();

    /// <summary>收起侧边栏(图标栏)显示的项:全部导航项(仿原版 NavigationView 紧凑态,不是只留常用 4 个)。</summary>
    public IReadOnlyList<NavItemViewModel> CompactNavItems =>
        ShellNavItems.Where(item => item.IsItem).ToArray();

    /// <summary>当前导航页键(Home/Recommend/Library/Recents/Favorites/Search/Account/Settings)。</summary>
    [ObservableProperty] private string _activePage = "Recommend";

    /// <summary>Android 顶部横幅显示当前页名称:优先用导航项文本,歌单子项显示歌单名。</summary>
    public string CurrentPageTitle
    {
        get
        {
            if (SelectedNav is { IsItem: true } nav)
                return nav.Label;

            return ActivePage switch
            {
                "Search" => "搜索",
                "Recommend" => "个性推荐",
                "Browse" => "浏览",
                "PersonalStation" => "私人FM",
                "Library" => "我的收藏",
                "CloudDrive" => "音乐云盘",
                "Recents" => "最近播放",
                "Favorites" => "我喜欢的音乐",
                "Account" => "账号",
                "Settings" => "设置",
                "Artist" => "歌手",
                "Album" => "专辑",
                "Debug" => "Debug",
                _ => "ALyricEase",
            };
        }
    }

    /// <summary>正在播放全屏覆盖层。</summary>
    [ObservableProperty] private bool _showNowPlaying;

    /// <summary>正在播放页右侧面板(歌词/播放列表互斥,再点一次收起)。</summary>
    [ObservableProperty] private NowPlayingPanel _nowPlayingPanel;

    public bool ShowLyricsPanel => NowPlayingPanel == NowPlayingPanel.Lyrics;

    public bool ShowQueuePanel => NowPlayingPanel == NowPlayingPanel.Queue;

    partial void OnNowPlayingPanelChanged(NowPlayingPanel value)
    {
        OnPropertyChanged(nameof(ShowLyricsPanel));
        OnPropertyChanged(nameof(ShowQueuePanel));
    }

    /// <summary>切换歌词面板(开着播放列表时先收起到歌词,互斥)。</summary>
    [RelayCommand]
    private void ToggleLyricsPanel()
        => NowPlayingPanel = NowPlayingPanel == NowPlayingPanel.Lyrics ? NowPlayingPanel.None : NowPlayingPanel.Lyrics;

    /// <summary>切换播放列表面板(与歌词面板互斥)。</summary>
    [RelayCommand]
    private void ToggleQueuePanel()
        => NowPlayingPanel = NowPlayingPanel == NowPlayingPanel.Queue ? NowPlayingPanel.None : NowPlayingPanel.Queue;

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


    /// <summary>内容区当前页(TransitioningContentControl 按 VM 类型选模板)。
    /// 音乐云盘复用歌单页(PlaylistViewModel 云盘模式,见 OpenCloudAsync);
    /// 私人FM 复用播放器 VM(播放器内 FM 无限流,页面绑定 PlayerViewModel)。</summary>
    public object? CurrentContent => ActivePage switch
    {
        "Recommend" => Recommend,
        "Search" => Search,
        "Favorites" => Playlist,
        "CloudDrive" => Playlist,
        "PersonalStation" => Player,
        "Artist" => Artist,
        "Album" => Album,
        "Settings" => Settings,
        "Debug" => Debug,
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
        if (value is "Browse" or "Recents" or "Account")
        {
            Placeholder.ShowLogout = value == "Account";
            (Placeholder.Title, Placeholder.Description) = value switch
            {
                "Browse" => ("浏览", "Banner、榜单与更多发现内容将在后续阶段接入"),
                "Recents" => ("最近播放", "本地播放历史将在下一阶段接入"),
                "Favorites" => ("我喜欢的音乐", "喜欢列表与收藏操作将在队列阶段接入"),
                _ => ("账号", "登录与账户信息"),
            };
        }

        if (value == "Recommend")
            _ = Recommend.EnsureLoadedAsync();

        if (value == "Favorites")
            _ = _playlist.EnsureLoadedAsync(); // 已存 MUSIC_U 则恢复并打开“我喜欢的音乐”

        if (value == "CloudDrive")
            _ = _playlist.OpenCloudCommand.ExecuteAsync(null); // 云盘复用歌单页(未登录时由其内部跳过)

        // 从搜索/占位页切回导航项时同步选中;非导航页(搜索/账号/设置)清除选中。
        // 打开具体歌单时 ActivePage 也是 "Favorites",但 SelectedNav 当前是歌单子项,
        // 不能把它重置成“我的收藏”,否则歌单项选中样式会消失。
        if (value == "Favorites" && SelectedNav is { Playlist: not null })
            return;

        SelectedNav = IsNavItem(value) ? ShellNavItems.FirstOrDefault(n => n.Key == value) : null;
        OnPropertyChanged(nameof(CurrentPageTitle));
    }

    private static bool IsNavItem(string page) => page is "Search" or "Recommend" or "Browse" or "PersonalStation" or "Library" or "CloudDrive" or "Recents" or "Favorites";

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildShellNavigation();

    /// <summary>网易云/QQ 登录态变化 → 重建导航:登录后出现对应歌单分组,退出后消失。</summary>
    private void OnPlaylistLoginChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaylistViewModel.IsLoggedIn) or nameof(PlaylistViewModel.IsQqLoggedIn))
        {
            // 登录态变化:重算各页歌曲行可播性(登录成会员后 VIP 行恢复可点;登出则禁用)
            Search.RefreshPlayability();
            Playlist.RefreshPlayability();
            Recommend.RefreshPlayability();
            RebuildShellNavigation();
        }
    }

    /// <summary>静态导航 + 登录音源各自的歌单分组(网易云/QQ 均登录后才显示;QQ 键加前缀防与网易云 id 撞键)。
    /// 聚合歌单(任一音源登录即显示)置于两个分组之上。歌单子项经 OwnerKey 挂到所属分组头,
    /// 并继承其当前开合态。</summary>
    private void RebuildShellNavigation()
    {
        ShellNavItems.Clear();
        foreach (var item in NavItems)
            ShellNavItems.Add(item);

        // 聚合歌单:两源任一登录即出现(子项 = 用户创建的聚合歌单,占位期可空)
        if (Playlist.IsLoggedIn || Playlist.IsQqLoggedIn)
        {
            ShellNavItems.Add(AggregatePlaylistsHeader);
            foreach (var agg in AppState.AggregatePlaylists)
                ShellNavItems.Add(new NavItemViewModel($"Aggregate:{agg.Id}", agg.Name, "", aggregate: agg)
                {
                    OwnerKey = AggregatePlaylistsHeader.Key,
                    ShowAsChild = AggregatePlaylistsHeader.IsExpanded,
                });
        }

        if (Playlist.IsLoggedIn)
        {
            ShellNavItems.Add(NetEasePlaylistsHeader);
            foreach (var playlist in Playlist.Playlists)
                ShellNavItems.Add(new NavItemViewModel($"Playlist:{playlist.Id}", playlist.Name, "", playlist: playlist)
                {
                    OwnerKey = NetEasePlaylistsHeader.Key,
                    ShowAsChild = NetEasePlaylistsHeader.IsExpanded,
                });
        }

        if (Playlist.IsQqLoggedIn)
        {
            ShellNavItems.Add(QqPlaylistsHeader);
            foreach (var playlist in Playlist.QqPlaylists)
                ShellNavItems.Add(new NavItemViewModel($"QQPlaylist:{playlist.Id}", playlist.Name, "", playlist: playlist)
                {
                    OwnerKey = QqPlaylistsHeader.Key,
                    ShowAsChild = QqPlaylistsHeader.IsExpanded,
                });
        }
    }

    /// <summary>展开/收起一个可折叠分组(聚合歌单/网易云音乐/QQ 音乐)。子项只隐藏不清除:
    /// 已打开的歌单详情页与选中态保持不变;分组头是静态实例,状态跨导航重建保留。</summary>
    [RelayCommand]
    private void ToggleNavGroup(string? key)
    {
        var header = ShellNavItems.FirstOrDefault(i => i.IsHeader && i.Key == key);
        if (header is null) return;
        header.IsExpanded = !header.IsExpanded;
        foreach (var child in ShellNavItems.Where(i => i.OwnerKey == header.Key))
            child.ShowAsChild = header.IsExpanded;

        // 记住折叠状态(state.json 原子小文件,同步写无感)
        switch (header.Key)
        {
            case "PlaylistsHeader": AppState.IsNetEaseGroupExpanded = header.IsExpanded; break;
            case "QqPlaylistsHeader": AppState.IsQqGroupExpanded = header.IsExpanded; break;
        }
        AppState.Save();
    }

    /// <summary>当前选中导航项(ListBox 双向)。</summary>
    [ObservableProperty] private NavItemViewModel? _selectedNav;

    /// <summary>选中导航项即导航。原版 UWP NavigationView 在点击(抬起)时选中;Avalonia ListBox 默认
    /// 按下选中,侧栏由 NavListTapBehavior 延迟到点击(Tapped)再驱动选中(此前试过
    /// InputElement.IsHoldWithMouseEnabled,因"特定导航顺序下歌单子项选中样式不刷新"被关闭),
    /// 触发时机对齐原版:按下不导航、拖走松开不选中、点击在项上才导航。</summary>
    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (value is null || value.IsHeader) return;
        OnPropertyChanged(nameof(CurrentPageTitle));
        // 中/小屏抽屉内点击导航项后自动收起(原版 NavigationView Compact/Minimal 语义:选中即收起抽屉)
        IsNavigationDrawerOpen = false;
        if (value.Aggregate is { } aggregate)
        {
            // 聚合歌单:合并各成员歌单曲目展示(复用歌单详情页)
            ActivePage = "Favorites";
            Playlist.OpenAggregateCommand.Execute(aggregate);
            SelectedNav = value;
            return;
        }
        if (value.Playlist is { } playlist)
        {
            // 按 Playlist.Source 路由:QQ 歌单走一次拉全量,网易云维持 trackIds 增量加载
            if (playlist.Playlist.Source == MusicSource.QQ)
                OpenShellQqPlaylistCommand.Execute(playlist);
            else
                OpenShellPlaylistCommand.Execute(playlist);
            // OpenShellPlaylist 会把 ActivePage 设为 “Favorites”,
            // 这里再强制把选中项设回歌单子项,防止被”我的收藏”同步逻辑覆盖。
            SelectedNav = value;
            return;
        }

        ActivePage = value.Key;
    }

    public bool CanGoBack => _navigationHistory.Count > 0;

    [RelayCommand]
    private void GoBack()
    {
        if (_navigationHistory.Count == 0) return;
        IsNavigationDrawerOpen = false;
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

    /// <summary>系统返回的统一入口(Android 返回手势/三大金刚键返回键;桌面端 Esc、鼠标返回键也可复用)。
    /// 一次调用只消费层级最上的一项,顺序与宿主 XAML 的声明顺序相反(声明靠后者盖在上层):
    /// 对话框 → 正在播放页右侧子面板 → 正在播放覆盖层 → 导航抽屉 → 页面栈。
    /// </summary>
    /// <returns>true 表示本次返回已被应用消费,宿主应阻止系统默认行为(结束 Activity / 关闭窗口)。</returns>
    public bool TryHandleBack()
    {
        if (IsAggregateSettingsDialogOpen)
        {
            CloseAggregateSettingsDialogCommand.Execute(null);
            return true;
        }

        if (IsAggregateDialogOpen)
        {
            CloseAggregateDialogCommand.Execute(null);
            return true;
        }

        if (IsLoginDialogOpen)
        {
            CloseLoginDialogCommand.Execute(null);
            return true;
        }

        if (IsCreatePlaylistDialogOpen)
        {
            CloseCreatePlaylistDialogCommand.Execute(null);
            return true;
        }

        if (IsRenamePlaylistDialogOpen)
        {
            CloseRenamePlaylistDialogCommand.Execute(null);
            return true;
        }

        // 正在播放页的歌词/播放列表面板先于覆盖层本身收起,与桌面端 Esc 的语义一致
        if (NowPlayingPanel != NowPlayingPanel.None)
        {
            NowPlayingPanel = NowPlayingPanel.None;
            return true;
        }

        if (ShowNowPlaying)
        {
            CloseNowPlayingCommand.Execute(null);
            return true;
        }

        if (IsNavigationDrawerOpen)
        {
            CloseNavigationDrawerCommand.Execute(null);
            return true;
        }

        if (CanGoBack)
        {
            GoBackCommand.Execute(null);
            return true;
        }

        return false;
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

    /// <summary>侧边栏打开 QQ 歌单:同样复用歌单详情页(Favorites),由 QQ 客户端一次拉全量曲目。</summary>
    [RelayCommand]
    private void OpenShellQqPlaylist(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        ActivePage = "Favorites";
        Playlist.OpenQqPlaylistCommand.Execute(playlist);
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

    /// <summary>QQ 音乐曲目点击歌手 → 歌手页(按 singer mid)。</summary>
    [RelayCommand]
    private async Task OpenQqArtistAsync(string? singerMid)
    {
        if (string.IsNullOrEmpty(singerMid)) return;
        ActivePage = "Artist";
        try { await Artist.LoadQqAsync(singerMid); }
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

    /// <summary>QQ 音乐曲目点击专辑 → 专辑页(按 album mid)。</summary>
    [RelayCommand]
    private async Task OpenQqAlbumAsync(string? albumMid)
    {
        if (string.IsNullOrEmpty(albumMid)) return;
        ActivePage = "Album";
        try { await Album.LoadQqAsync(albumMid); }
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
        IsNavigationDrawerOpen = false;
        ActivePage = "Account";
        Placeholder.Title = "账号";
        Placeholder.Description = "登录与账户信息";
    }

    [RelayCommand] private void GoSettings()
    {
        IsNavigationDrawerOpen = false;
        ActivePage = "Settings";
    }

    /// <summary>登录对话框(WinUI3 ContentDialog 式窗口内弹层):true=显示。代替原独立 LoginWindow。</summary>
    [ObservableProperty] private bool _isLoginDialogOpen;

    /// <summary>打开登录对话框:清掉上一次失败信息,总是以干净态出现。</summary>
    [RelayCommand]
    private void OpenLoginDialog()
    {
        Playlist.Message = null;
        IsLoginDialogOpen = true;
    }

    /// <summary>关闭登录对话框(取消/Esc;登录成功由 MainWindow 监听 IsLoggedIn 自动关闭)。</summary>
    [RelayCommand] private void CloseLoginDialog() => IsLoginDialogOpen = false;

    // ---- 添加聚合歌单对话框 ----

    /// <summary>添加聚合歌单对话框(WinUI3 ContentDialog 式窗口内弹层):true=显示。</summary>
    [ObservableProperty] private bool _isAggregateDialogOpen;

    /// <summary>分组头"+"统一入口:聚合歌单=添加成员歌单;网易云/QQ 音乐=创建该源歌单(占位,API 待接入)。</summary>
    [RelayCommand]
    private void NavHeaderAdd(string? key)
    {
        if (key == AggregatePlaylistsHeader.Key)
            OpenAggregateDialog();
        else if (key == NetEasePlaylistsHeader.Key)
            OpenCreatePlaylistDialog(MusicSource.NetEase);
        else if (key == QqPlaylistsHeader.Key)
            OpenCreatePlaylistDialog(MusicSource.QQ);
    }

    /// <summary>创建歌单弹窗状态(WinUI3 ContentDialog 式窗口内弹层,宿主 MainWindow 绑定)。</summary>
    [ObservableProperty] private bool _isCreatePlaylistDialogOpen;

    /// <summary>打开创建歌单对话框:按音源重置输入;未登录该音源时改弹登录弹窗引导。</summary>
    [RelayCommand]
    private void OpenCreatePlaylistDialog(MusicSource source)
    {
        var loggedIn = source == MusicSource.QQ ? Playlist.IsQqLoggedIn : Playlist.IsLoggedIn;
        if (!loggedIn)
        {
            OpenLoginDialogCommand.Execute(null);
            return;
        }
        CreatePlaylistDialog.Refresh(source);
        IsCreatePlaylistDialogOpen = true;
    }

    [RelayCommand] private void CloseCreatePlaylistDialog() => IsCreatePlaylistDialogOpen = false;

    // ---- 重命名歌单对话框 ----

    /// <summary>重命名歌单弹窗状态(WinUI3 ContentDialog 式窗口内弹层,宿主 MainWindow 绑定)。</summary>
    [ObservableProperty] private bool _isRenamePlaylistDialogOpen;

    /// <summary>打开重命名歌单对话框(侧栏歌单子项右键):预填当前名;
    /// 红心集合不可改名(菜单已隐藏,此处双保险),非本人歌单由服务端拒绝。</summary>
    [RelayCommand]
    private void OpenRenamePlaylistDialog(PlaylistItemViewModel? item)
    {
        if (item is null || _playlist.IsLikedPlaylist(item.Playlist)) return;
        RenamePlaylistDialog.Refresh(item);
        IsRenamePlaylistDialogOpen = true;
    }

    [RelayCommand] private void CloseRenamePlaylistDialog() => IsRenamePlaylistDialogOpen = false;

    /// <summary>重命名成功回调:关弹窗,后台刷新对应侧栏分组;打开中的详情页随刷新换新实例。</summary>
    private void OnRenamePlaylistConfirmed(PlaylistItemViewModel item)
    {
        IsRenamePlaylistDialogOpen = false;
        _ = _playlist.RefreshAfterRenameAsync(item);
    }

    /// <summary>创建成功回调:关弹窗,后台刷新侧栏歌单分组并打开新歌单(复用歌单详情页)。</summary>
    private void OnCreatePlaylistConfirmed(Models.Playlist created)
    {
        IsCreatePlaylistDialogOpen = false;
        _ = OpenCreatedPlaylistAsync(created);
    }

    private async Task OpenCreatedPlaylistAsync(Models.Playlist created)
    {
        try
        {
            if (created.Source == MusicSource.QQ)
            {
                await Playlist.ReloadQqPlaylistsAsync();
                var item = Playlist.QqPlaylists.FirstOrDefault(p => p.Playlist.Id == created.Id);
                if (item is not null) OpenShellQqPlaylistCommand.Execute(item);
            }
            else
            {
                await Playlist.ReloadNetEasePlaylistsAsync();
                var item = Playlist.Playlists.FirstOrDefault(p => p.Playlist.Id == created.Id);
                if (item is not null) OpenShellPlaylistCommand.Execute(item);
            }
        }
        catch
        {
            // 刷新/打开失败不打断(新歌单下次进侧栏仍可见)
        }
    }

    /// <summary>打开聚合歌单对话框:先按两源已加载的歌单重建候选,再显示。</summary>
    [RelayCommand]
    private void OpenAggregateDialog()
    {
        AddAggregateDialog.Refresh();
        IsAggregateDialogOpen = true;
    }

    /// <summary>关闭聚合歌单对话框(取消/Esc)。</summary>
    [RelayCommand] private void CloseAggregateDialog() => IsAggregateDialogOpen = false;

    /// <summary>对话框确认回调:聚合歌单入库(state.json 持久化)并重建侧栏聚合分组,然后关弹窗。</summary>
    private void OnAggregateConfirmed(Models.AggregatePlaylist aggregate)
    {
        AppState.AggregatePlaylists.Add(aggregate);
        AppState.Save();
        RebuildShellNavigation(); // 聚合分组下新增子项
        IsAggregateDialogOpen = false;
    }

    // ---- 聚合歌单设置对话框 ----

    /// <summary>聚合歌单设置对话框(WinUI3 ContentDialog 式窗口内弹层):true=显示。</summary>
    [ObservableProperty] private bool _isAggregateSettingsDialogOpen;

    /// <summary>打开聚合歌单设置对话框(齿轮按钮;按当前聚合歌单初始化单选)。</summary>
    [RelayCommand]
    private void OpenAggregateSettings(Models.AggregatePlaylist? aggregate)
    {
        if (aggregate is null) return;
        AggregateSettingsDialog.Refresh(aggregate);
        IsAggregateSettingsDialogOpen = true;
    }

    /// <summary>关闭聚合歌单设置对话框(取消/Esc)。</summary>
    [RelayCommand] private void CloseAggregateSettingsDialog() => IsAggregateSettingsDialogOpen = false;

    /// <summary>设置保存回调:持久化;若当前页正展示该聚合歌单,按新顺序重新打开;然后关弹窗。</summary>
    private void OnAggregateSettingsSaved(Models.AggregatePlaylist aggregate)
    {
        AppState.Save();
        if (ReferenceEquals(Playlist.CurrentAggregate, aggregate))
            Playlist.OpenAggregateCommand.Execute(aggregate);
        IsAggregateSettingsDialogOpen = false;
    }

    /// <summary>左下角 Debug 入口(仅 DEBUG 构建可见)。</summary>
    [RelayCommand] private void GoDebug() => ActivePage = "Debug";

    /// <summary>打开正在播放覆盖层(点底部播放条时)。</summary>
    [RelayCommand] private void OpenNowPlaying() => ShowNowPlaying = true;

    /// <summary>收起正在播放覆盖层(返回键/Escape)。</summary>
    [RelayCommand] private void CloseNowPlaying() => ShowNowPlaying = false;
}
