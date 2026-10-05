using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using ALyricEase.Infrastructure;
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

    public MainViewModel(SearchViewModel search, PlayerViewModel player, LyricViewModel lyric, PlaylistViewModel playlist, RecommendViewModel recommend, ArtistViewModel artist, AlbumViewModel album, ArtistSongsPageViewModel artistSongsPage, ArtistAlbumsPageViewModel artistAlbumsPage, UserProfileViewModel userProfile, CollectedPlaylistsViewModel collected, RecentPlaybackViewModel recentPlayback, SettingsViewModel settings, AccountViewModel account, Services.AppStateStore appState)
    {
        Search = search;
        Player = player;
        Lyric = lyric;
        Playlist = playlist;
        Recommend = recommend;
        Artist = artist;
        Album = album;
        ArtistSongsPage = artistSongsPage;
        ArtistAlbumsPage = artistAlbumsPage;
        UserProfile = userProfile;
        Collected = collected;
        RecentPlayback = recentPlayback;
        Settings = settings;
        Account = account;
        _playlist = playlist;
        AppState = appState;
        AddAggregateDialog = new AddAggregateDialogViewModel(playlist, appState, OnAggregateConfirmed,
            OnAggregateEditConfirmed);
        AggregateSettingsDialog = new AggregateSettingsDialogViewModel(OnAggregateSettingsSaved);
        CreatePlaylistDialog = new CreatePlaylistDialogViewModel(playlist, OnCreatePlaylistConfirmed);
        AddSongToPlaylistDialog = new AddSongToPlaylistDialogViewModel(playlist, OnSongAddedToPlaylist);
        LikeSourceDialog = new LikeSourceDialogViewModel(() => IsLikeSourceDialogOpen = false);
        RenamePlaylistDialog = new RenamePlaylistDialogViewModel(playlist, OnRenamePlaylistConfirmed,
            RenameAggregateAsync, OnAggregateRenamed);
        DeletePlaylistDialog = new DeletePlaylistDialogViewModel(playlist, OnDeletePlaylistConfirmed,
            DeleteAggregateAsync, OnAggregateDeleted);
        // 折叠状态必须先于首次 RebuildShellNavigation 恢复(静态分组头实例随即被导航渲染消费)
        NetEasePlaylistsHeader.IsExpanded = appState.IsNetEaseGroupExpanded;
        QqPlaylistsHeader.IsExpanded = appState.IsQqGroupExpanded;
        RebuildShellNavigation();
        Playlist.Playlists.CollectionChanged += OnPlaylistsChanged;
        Playlist.QqPlaylists.CollectionChanged += OnPlaylistsChanged;
        Playlist.PropertyChanged += OnPlaylistLoginChanged;
        // 设置里的渲染开关改了要立刻反映到正在显示的详情页背景上(见 NowPlayingMotionEnabled)
        appState.VisualEffectsChanged += () =>
        {
            OnPropertyChanged(nameof(NowPlayingMotionEnabled));
            OnPropertyChanged(nameof(LyricBlurEnabled));
        };
        // 启动页:本地存有任一音源登录凭证 → 自己的用户页(登录态异步恢复);
        // 完全没有 → 账号页 + 自动弹出登录弹层(与侧栏"账号"未登录的行为一致)。
        // CookieStore 构造函数同步读盘,此处判定可靠。
        _activePage = Playlist.HasStoredCredentials ? "User" : "Account";
        // 用户页非侧栏项 → SelectedNav 为 null,侧栏无高亮(同详情页打开时的行为);
        // 账号页也不是侧栏项(侧栏"账号"是按钮),同样无高亮。
        _selectedNav = ShellNavItems.FirstOrDefault(item => item.Key == _activePage);
        // 启动页数据:自己的用户页(uid=0 按登录态解析)。
        // 直接 LoadAsync 不走 OpenUser 命令 —— 不进返回历史,启动即"首页",无页可返回。
        // 未登录(账号页启动)不拉取:uid=0 无意义,登录成功后由用户自行进入用户页。
        if (_activePage == "User")
        {
            // 启动页数据:自己的用户页,按本地凭证音源解析(有网易云凭证走网易云;
            // 仅 QQ 凭证走 QQ 用户页 —— 旧逻辑恒走网易云,QQ 单独登录的用户启动只见空页)。
            // 直接 Load 不走 OpenUser 命令 —— 不进返回历史,启动即"首页",无页可返回。
            _ = Playlist.HasNetEaseCredential ? UserProfile.LoadAsync(0) : UserProfile.LoadQqAsync();
        }
        else
            IsLoginDialogOpen = true; // 无账号:启动即呈现登录页
        // 登录恢复完成后"自动打开我喜欢的音乐"会切走 ActivePage —— 仅当用户真的停在收藏页
        // 等待时才兜底打开,否则启动即被顶离用户页(实测)。
        Playlist.AutoOpenFavoritesGuard = () => ActivePage == "Favorites";
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
    public ArtistSongsPageViewModel ArtistSongsPage { get; }
    public ArtistAlbumsPageViewModel ArtistAlbumsPage { get; }
    public UserProfileViewModel UserProfile { get; }
    public CollectedPlaylistsViewModel Collected { get; }
    public RecentPlaybackViewModel RecentPlayback { get; }
    public SettingsViewModel Settings { get; }
    public AccountViewModel Account { get; }

    /// <summary>界面状态存储(折叠状态;主窗口大小/位置由 MainWindow 读写同一实例)。</summary>
    public Services.AppStateStore AppState { get; }

    /// <summary>添加聚合歌单对话框 VM(宿主绑定 AddAggregateDialogView;打开前 Refresh)。</summary>
    public AddAggregateDialogViewModel AddAggregateDialog { get; }

    /// <summary>聚合歌单设置对话框 VM(宿主绑定 AggregateSettingsDialogView;打开前 Refresh)。</summary>
    public AggregateSettingsDialogViewModel AggregateSettingsDialog { get; }

    /// <summary>创建歌单对话框 VM(宿主绑定 CreatePlaylistDialogView;打开前按音源 Refresh)。</summary>
    public CreatePlaylistDialogViewModel CreatePlaylistDialog { get; }

    /// <summary>添加当前歌曲到歌单的选择对话框 VM。</summary>
    public AddSongToPlaylistDialogViewModel AddSongToPlaylistDialog { get; }

    /// <summary>综合搜索合并歌曲的“我喜欢”平台选择对话框。</summary>
    public LikeSourceDialogViewModel LikeSourceDialog { get; }

    /// <summary>重命名歌单对话框 VM(宿主绑定 RenamePlaylistDialogView;打开前按目标歌单 Refresh)。</summary>
    public RenamePlaylistDialogViewModel RenamePlaylistDialog { get; }

    /// <summary>删除歌单确认对话框 VM(宿主绑定 DeletePlaylistDialogView;打开前按目标歌单 Refresh)。</summary>
    public DeletePlaylistDialogViewModel DeletePlaylistDialog { get; }

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

    /// <summary>网易云/QQ 歌单分组标题:在线登录或存在离线快照时显示，由 RebuildShellNavigation 插入。
    /// 头部是静态共享实例,展开/收起状态随实例保留,跨导航重建与登录变化不丢。
    /// 聚合歌单在两者之上:任一音源登录即出现(占位,展开空;"+"后续接入选择两源歌单加入聚合)。</summary>
    private static readonly NavItemViewModel NetEasePlaylistsHeader = new("PlaylistsHeader", "网易云音乐", isHeader: true, isToggleGroup: true, hasAddButton: true, addToolTip: "创建新歌单");
    private static readonly NavItemViewModel QqPlaylistsHeader = new("QqPlaylistsHeader", "QQ音乐", isHeader: true, isToggleGroup: true, hasAddButton: true, addToolTip: "创建新歌单");
    private static readonly NavItemViewModel AggregatePlaylistsHeader = new("AggregatePlaylistsHeader", "聚合歌单", isHeader: true, isToggleGroup: true, hasAddButton: true, addToolTip: "添加歌单到聚合");

    public ObservableCollection<NavItemViewModel> ShellNavItems { get; } = new();

    /// <summary>收起侧边栏(图标栏)显示的项:只留静态主导航(搜索/个性推荐/浏览/私人FM/我的收藏/音乐云盘/最近播放)。
    /// 歌单子项(含聚合歌单)与分组头都不进图标栏 —— 子项没有图标字形,进了只会渲染成一排"看不见却能点"的空行。</summary>
    public IReadOnlyList<NavItemViewModel> CompactNavItems =>
        ShellNavItems.Where(item => item.IsItem && !item.IsPlaylistChild).ToArray();

    /// <summary>当前导航页键(Home/Recommend/Library/Recents/Favorites/Search/Account/Settings)。
    /// 启动页 = 自己的用户页(uid=0,构造尾部加载);用户页无侧栏选中态(SelectedNav 为 null)。</summary>
    [ObservableProperty] private string _activePage = "User";

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
                "User" => "个人主页",
                "Artist" => "歌手",
                "Album" => "专辑",
                "Debug" => "Debug",
                _ => "ALyricEase",
            };
        }
    }

    /// <summary>正在播放全屏覆盖层。</summary>
    [ObservableProperty] private bool _showNowPlaying;

    /// <summary>
    /// 播放详情页的色团背景是否该漂移 —— 设置里的"启用播放详情界面的动态背景效果" × 详情页是否打开。
    ///
    /// <para>
    /// 必须两条件同时成立。覆盖层在窗口里是**常驻**的（只靠 RenderTransform 移出窗外），
    /// 所以这个背景控件从应用启动起就在视觉树上；不判"详情页是否打开"的话，
    /// 整幅窗口会以屏幕刷新率永远重画下去 ——
    /// 实测首页（详情页关闭）：强制让漂移继续跑 8.16% / CPU 22.5%，加上这道门后 0.95% / 1.81%。
    /// 代价与"看不看得见"无关，钱花在"一直动"上，不在"画出来"上（图层本身只值 2.20%）。
    /// </para>
    ///
    /// 这里同时订阅设置变更（见构造函数）：覆盖层虽然盖住整窗、用户改设置时它必然没打开，
    /// 但"改了就得马上对"这条不该依赖用户的操作顺序 —— 订阅是一行的事。
    /// </summary>
    public bool NowPlayingMotionEnabled => ShowNowPlaying && AppState.DynamicBackground;

    /// <summary>
    /// 歌词行要不要模糊 —— 由设置「性能与体验」决定(最佳质量 = 开,最佳性能 = 关)。
    /// 走 <see cref="AppStateStore.LyricBlurEnabled"/>,不在这里重复解析档位字符串。
    /// 关掉时整块歌词面板不挂 <c>Effect</c>:实测整档省 ≈0.95% GPU(1200×720,4 条带模糊行),
    /// 代价只是文字边缘少了软化,位置/字号/透明度全不变。
    /// </summary>
    public bool LyricBlurEnabled => AppState.LyricBlurEnabled;

    partial void OnShowNowPlayingChanged(bool value) => OnPropertyChanged(nameof(NowPlayingMotionEnabled));

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

    /// <summary>导航历史不能只记 ActivePage：所有歌单详情都复用 Favorites，
    /// 在歌单之间切换时页面键不会变化。这里只保存轻量领域对象,不让历史栈持有带封面的页面 VM。</summary>
    private sealed record NavigationEntry(
        string Page,
        string? SelectedNavKey,
        PlaylistNavigationSnapshot? PlaylistSnapshot,
        DetailNavigationSnapshot? DetailSnapshot);

    private const int MaxNavigationHistory = 50;
    private readonly List<NavigationEntry> _navigationHistory = new();
    private bool _isGoingBack;
    private bool _selectionNavigationInProgress;
    private bool _suppressSelectedNavNavigation;
    private bool _restoringPlaylistNavigation;

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
        "ArtistSongs" => ArtistSongsPage,
        "ArtistAlbums" => ArtistAlbumsPage,
        "User" => UserProfile,
        "Library" => Collected,
        "Recents" => RecentPlayback,
        "Settings" => Settings,
        "Account" => Account,
        "Debug" => Debug,
        _ => Placeholder,
    };

    partial void OnActivePageChanging(string? oldValue, string newValue)
    {
        // 导航到其他页面时自动收起正在播放覆盖层(所有导航路径都汇于 ActivePage 变化:
        // 侧栏点击/返回键/程序化开页/防御兜底)。此时详情页的退出滑动动画与页面切换并行。
        ShowNowPlaying = false;

        if (!_isGoingBack && !_selectionNavigationInProgress
            && !string.Equals(oldValue, newValue, StringComparison.Ordinal))
            PushCurrentNavigation();

        // 前进导航已在 PushCurrentNavigation 中留好内存快照并释放；后退没有前进栈，直接丢弃当前页重数据。
        if (oldValue is "Favorites" or "CloudDrive" && newValue != oldValue)
            _playlist.ReleaseCurrentPageData();
        if (newValue != oldValue)
            ReleaseDetailPageData(oldValue);
    }

    partial void OnActivePageChanged(string value)
    {
        // 返回时反向滑动(GoBack 期间 _isGoingBack=true),前进正向
        IsTransitionReversed = _isGoingBack;
        OnPropertyChanged(nameof(CanGoBack));
        // 解码图交给带租约的有界 LRU 管理。切页不整库清空，返回时可直接复用；
        // 超过容量且没有可见 Image 租用的条目会由缓存自动淘汰并释放。
        OnPropertyChanged(nameof(CurrentContent));

        // 未完成页面使用明确占位，不伪装为可用功能。
        if (value == "Browse")
        {
            (Placeholder.Title, Placeholder.Description) = value switch
            {
                "Browse" => ("浏览", "Banner、榜单与更多发现内容将在后续阶段接入"),
                "Favorites" => ("我喜欢的音乐", "喜欢列表与收藏操作将在队列阶段接入"),
                _ => ("浏览", "Banner、榜单与更多发现内容将在后续阶段接入"),
            };
        }

        if (value == "Recommend")
            _ = Recommend.EnsureLoadedAsync();

        if (value == "Account")
            _ = Account.RefreshAsync();

        // 经导航菜单/搜索图标重新进入搜索页时回到登录页(单例 VM 的结果状态不跨导航保留,
        // 对齐原版 Frame:菜单导航到 Search 落的是 SearchView 登录页);
        // 页面栈返回(_isGoingBack,如 结果页下钻歌手后 ←)则保留结果上下文
        if (value == "Search" && !_isGoingBack)
            Search.ResetToLanding();

        if (value == "Favorites" && !_restoringPlaylistNavigation)
            _ = _playlist.EnsureLoadedAsync(); // 已存 MUSIC_U 则恢复并打开“我喜欢的音乐”

        if (value == "CloudDrive" && !_restoringPlaylistNavigation)
            _ = _playlist.OpenCloudCommand.ExecuteAsync(null); // 云盘复用歌单页(未登录时由其内部跳过)

        // 从搜索/占位页切回导航项时同步选中;非导航页(搜索/账号/设置)清除选中。
        // 打开具体歌单时 ActivePage 也是 "Favorites",但 SelectedNav 当前是歌单子项,
        // 不能把它重置成“我的收藏”,否则歌单项选中样式会消失。
        if (value == "Favorites" && SelectedNav is { Playlist: not null } or { Aggregate: not null })
            return;

        SetSelectedNavWithoutNavigation(
            IsNavItem(value) ? ShellNavItems.FirstOrDefault(n => n.Key == value) : null);
        OnPropertyChanged(nameof(CurrentPageTitle));
    }

    private static bool IsNavItem(string page) => page is "Search" or "Recommend" or "Browse" or "PersonalStation" or "Library" or "CloudDrive" or "Recents" or "Favorites";

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildShellNavigation();

    /// <summary>网易云/QQ 登录态变化 → 重建导航；认证失败时已有离线快照仍保持可见。</summary>
    private void OnPlaylistLoginChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaylistViewModel.IsLoggedIn) or nameof(PlaylistViewModel.IsQqLoggedIn))
        {
            // 登录态变化后刷新各页歌曲行；明确的 VIP/购买权益提前判定，未知状态仍由播放地址兜底。
            Search.RefreshPlayability();
            Playlist.RefreshPlayability();
            Recommend.RefreshPlayability();
            RecentPlayback.RefreshPlayability();
            // 扫码/验证码登录可能先建立登录态、再异步取得会员状态；状态确认后再刷新一次，
            // 让已确认非会员的 VIP 行及时置灰，也让会员账号恢复可点。
            _ = RefreshPlayabilityAfterVipLoadedAsync(
                e.PropertyName == nameof(PlaylistViewModel.IsQqLoggedIn)
                    ? MusicSource.QQ
                    : MusicSource.NetEase);
            Account.SyncLoginState();
            if (ActivePage == "Account" &&
                ((e.PropertyName == nameof(PlaylistViewModel.IsLoggedIn) && Playlist.IsLoggedIn) ||
                 (e.PropertyName == nameof(PlaylistViewModel.IsQqLoggedIn) && Playlist.IsQqLoggedIn)))
                _ = Account.RefreshAsync();
            RebuildShellNavigation();
        }
    }

    private async Task RefreshPlayabilityAfterVipLoadedAsync(MusicSource source)
    {
        try
        {
            await ServiceLocator.Get<MusicApiProvider>().Resolve(source).EnsureVipStatusAsync();
        }
        catch
        {
            // 账号接口瞬时失败时保持“会员未知”可点，由实际播放地址继续兜底。
        }

        Search.RefreshPlayability();
        Playlist.RefreshPlayability();
        Recommend.RefreshPlayability();
        RecentPlayback.RefreshPlayability();
    }

    /// <summary>静态导航 + 在线账号或离线快照的歌单分组(QQ 键加前缀防与网易云 id 撞键)。
    /// 聚合歌单(任一音源登录即显示)置于两个分组之上。歌单子项经 OwnerKey 挂到所属分组头,
    /// 并继承其当前开合态。</summary>
    private void RebuildShellNavigation()
    {
        ShellNavItems.Clear();
        foreach (var item in NavItems)
            ShellNavItems.Add(item);

        var hasNetEaseLibrary = Playlist.IsLoggedIn || Playlist.Playlists.Count > 0;
        var hasQqLibrary = Playlist.IsQqLoggedIn || Playlist.QqPlaylists.Count > 0;

        // 聚合歌单:在线账号或任一来源的离线快照可用时出现。
        if (hasNetEaseLibrary || hasQqLibrary)
        {
            ShellNavItems.Add(AggregatePlaylistsHeader);
            foreach (var agg in AppState.AggregatePlaylists)
                ShellNavItems.Add(new NavItemViewModel($"Aggregate:{agg.Id}", agg.Name, "", aggregate: agg)
                {
                    OwnerKey = AggregatePlaylistsHeader.Key,
                    ShowAsChild = AggregatePlaylistsHeader.IsExpanded,
                });
        }

        if (hasNetEaseLibrary)
        {
            ShellNavItems.Add(NetEasePlaylistsHeader);
            foreach (var playlist in Playlist.Playlists)
                ShellNavItems.Add(new NavItemViewModel($"Playlist:{playlist.Id}", playlist.Name, "", playlist: playlist)
                {
                    OwnerKey = NetEasePlaylistsHeader.Key,
                    ShowAsChild = NetEasePlaylistsHeader.IsExpanded,
                });
        }

        if (hasQqLibrary)
        {
            ShellNavItems.Add(QqPlaylistsHeader);
            foreach (var playlist in Playlist.QqPlaylists)
                ShellNavItems.Add(new NavItemViewModel($"QQPlaylist:{playlist.Id}", playlist.Name, "", playlist: playlist)
                {
                    OwnerKey = QqPlaylistsHeader.Key,
                    ShowAsChild = QqPlaylistsHeader.IsExpanded,
                });
        }

        // CompactNavItems 是 ShellNavItems 的派生列表,不是它自身变化的通知源 —— 重建后要显式通知
        OnPropertyChanged(nameof(CompactNavItems));
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
    partial void OnSelectedNavChanging(NavItemViewModel? oldValue, NavItemViewModel? newValue)
    {
        if (_isGoingBack || _suppressSelectedNavNavigation
            || newValue is null || newValue.IsHeader || ReferenceEquals(oldValue, newValue))
            return;

        // 必须在属性真正换成新项之前截图，才能保留旧歌单详情。
        _selectionNavigationInProgress = true;
        PushCurrentNavigation();
    }

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (_suppressSelectedNavNavigation || value is null || value.IsHeader) return;
        try
        {
            OnPropertyChanged(nameof(CurrentPageTitle));
            // 中/小屏抽屉内点击导航项后自动收起(原版 NavigationView Compact/Minimal 语义:选中即收起抽屉)
            IsNavigationDrawerOpen = false;
            if (value.Aggregate is { } aggregate)
            {
                // 聚合歌单:合并各成员歌单曲目展示(复用歌单详情页)
                ActivePage = "Favorites";
                Playlist.OpenAggregateCommand.Execute(aggregate);
                return;
            }
            if (value.Playlist is { } playlist)
            {
                // 按 Playlist.Source 路由:QQ 歌单走一次拉全量,网易云维持 trackIds 增量加载
                if (playlist.Playlist.Source == MusicSource.QQ)
                    OpenShellQqPlaylistCommand.Execute(playlist);
                else
                    OpenShellPlaylistCommand.Execute(playlist);
                return;
            }

            ActivePage = value.Key;
        }
        finally
        {
            _selectionNavigationInProgress = false;
        }
    }

    public bool CanGoBack => _navigationHistory.Count > 0;

    [RelayCommand]
    private void GoBack()
    {
        // 搜索页内层级返回:结果页 ← 先回登录页(标题栏 ←),登录页 ← 才弹页面栈
        if (ActivePage == "Search" && Search.HasSearched)
        {
            Search.BackToLandingCommand.Execute(null);
            return;
        }
        if (_navigationHistory.Count == 0) return;
        IsNavigationDrawerOpen = false;
        _isGoingBack = true;
        try
        {
            var last = _navigationHistory.Count - 1;
            var entry = _navigationHistory[last];
            _navigationHistory.RemoveAt(last);
            OnPropertyChanged(nameof(CanGoBack));
            if (ActivePage is "Favorites" or "CloudDrive")
                Playlist.ReleaseCurrentPageData();
            ReleaseDetailPageData(ActivePage);
            RestoreNavigation(entry);
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
        // 红心平台选择声明在宿主弹层最后，系统返回/Esc 优先关闭它。
        if (IsLikeSourceDialogOpen)
        {
            CloseLikeSourceDialogCommand.Execute(null);
            return true;
        }

        // 更新弹窗声明在所有弹层最后(盖在最上) ⇒ 返回链最先消费
        if (IsUpdateDialogOpen)
        {
            CloseUpdateDialogCommand.Execute(null);
            return true;
        }

        // 清除缓存弹层声明在所有弹层最后(盖在最上) ⇒ 返回链最先消费
        if (IsClearCacheDialogOpen)
        {
            CloseClearCacheDialogCommand.Execute(null);
            return true;
        }

        // 设备选择弹层声明在所有弹层最后(盖在最上) ⇒ 返回链最先消费
        if (IsAudioDeviceDialogOpen)
        {
            CloseAudioDeviceDialogCommand.Execute(null);
            return true;
        }

        if (IsAddSongToPlaylistDialogOpen)
        {
            CloseAddSongToPlaylistDialogCommand.Execute(null);
            return true;
        }

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

        if (IsDeletePlaylistDialogOpen)
        {
            CloseDeletePlaylistDialogCommand.Execute(null);
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

        // 搜索页内层级返回:结果页先回登录页,登录页才真正弹页面栈(原版 Frame 中两页同属搜索)
        if (ActivePage == "Search" && Search.HasSearched)
        {
            Search.BackToLandingCommand.Execute(null);
            return true;
        }

        if (CanGoBack)
        {
            GoBackCommand.Execute(null);
            return true;
        }

        // 防御性兜底：只要视觉上不在首页(启动页 = 用户页),就绝不能因历史缺失直接退出界面。
        // 正常导航都会命中上面的历史；该分支覆盖恢复状态或后续新增页面漏记历史的情况。
        if (!string.Equals(ActivePage, "User", StringComparison.Ordinal))
        {
            _isGoingBack = true;
            try
            {
                ActivePage = "User";
                SetSelectedNavWithoutNavigation(ShellNavItems.FirstOrDefault(n => n.Key == "User"));
            }
            finally
            {
                _isGoingBack = false;
            }
            return true;
        }

        return false;
    }

    private void PushCurrentNavigation()
    {
        if (_navigationHistory.Count == MaxNavigationHistory)
        {
            Playlist.DiscardNavigationSnapshot(_navigationHistory[0].PlaylistSnapshot);
            DiscardDetailNavigationSnapshot(_navigationHistory[0].DetailSnapshot);
            _navigationHistory.RemoveAt(0);
        }

        var playlistSnapshot = ActivePage is "Favorites" or "CloudDrive"
            ? Playlist.CaptureAndReleaseNavigationSnapshot()
            : null;
        var detailSnapshot = CaptureDetailNavigationSnapshot(ActivePage);
        _navigationHistory.Add(new NavigationEntry(
            ActivePage,
            SelectedNav?.Key,
            playlistSnapshot,
            detailSnapshot));
        OnPropertyChanged(nameof(CanGoBack));
    }

    private void RestoreNavigation(NavigationEntry entry)
    {
        _restoringPlaylistNavigation = entry.PlaylistSnapshot is not null;
        try
        {
            ActivePage = entry.Page;
            SetSelectedNavWithoutNavigation(
                entry.SelectedNavKey is null
                    ? null
                    : ShellNavItems.FirstOrDefault(n => n.Key == entry.SelectedNavKey));
        }
        finally
        {
            _restoringPlaylistNavigation = false;
        }

        if (entry.PlaylistSnapshot is { } snapshot)
            _ = Playlist.RestoreNavigationSnapshotAsync(snapshot);
        if (entry.DetailSnapshot is { } detailSnapshot)
            RestoreDetailNavigationSnapshot(detailSnapshot);
    }

    private DetailNavigationSnapshot? CaptureDetailNavigationSnapshot(string page) => page switch
    {
        "Artist" => Artist.CaptureAndReleaseNavigationSnapshot(),
        "Album" => Album.CaptureAndReleaseNavigationSnapshot(),
        "ArtistSongs" => ArtistSongsPage.CaptureAndReleaseNavigationSnapshot(),
        "ArtistAlbums" => ArtistAlbumsPage.CaptureAndReleaseNavigationSnapshot(),
        "User" => UserProfile.CaptureAndReleaseNavigationSnapshot(),
        _ => null,
    };

    private void ReleaseDetailPageData(string? page)
    {
        switch (page)
        {
            case "Artist": Artist.ReleaseCurrentPageData(); break;
            case "Album": Album.ReleaseCurrentPageData(); break;
            case "ArtistSongs": ArtistSongsPage.ReleaseCurrentPageData(); break;
            case "ArtistAlbums": ArtistAlbumsPage.ReleaseCurrentPageData(); break;
            case "User": UserProfile.ReleaseCurrentPageData(); break;
        }
    }

    private void RestoreDetailNavigationSnapshot(DetailNavigationSnapshot snapshot)
    {
        _ = snapshot.Kind switch
        {
            DetailPageKind.Artist => Artist.RestoreNavigationSnapshotAsync(snapshot),
            DetailPageKind.Album => Album.RestoreNavigationSnapshotAsync(snapshot),
            DetailPageKind.ArtistSongs => ArtistSongsPage.RestoreNavigationSnapshotAsync(snapshot),
            DetailPageKind.ArtistAlbums => ArtistAlbumsPage.RestoreNavigationSnapshotAsync(snapshot),
            DetailPageKind.User => UserProfile.RestoreNavigationSnapshotAsync(snapshot),
            _ => Task.CompletedTask,
        };
    }

    private void DiscardDetailNavigationSnapshot(DetailNavigationSnapshot? snapshot)
    {
        if (snapshot is null) return;
        switch (snapshot.Kind)
        {
            case DetailPageKind.Artist: Artist.DiscardNavigationSnapshot(snapshot); break;
            case DetailPageKind.Album: Album.DiscardNavigationSnapshot(snapshot); break;
            case DetailPageKind.ArtistSongs: ArtistSongsPage.DiscardNavigationSnapshot(snapshot); break;
            case DetailPageKind.ArtistAlbums: ArtistAlbumsPage.DiscardNavigationSnapshot(snapshot); break;
            case DetailPageKind.User: UserProfile.DiscardNavigationSnapshot(snapshot); break;
        }
    }

    private void SetSelectedNavWithoutNavigation(NavItemViewModel? value)
    {
        _suppressSelectedNavNavigation = true;
        try
        {
            SelectedNav = value;
            OnPropertyChanged(nameof(CurrentPageTitle));
        }
        finally
        {
            _suppressSelectedNavNavigation = false;
        }
    }

    /// <summary>内容区右上搜索图标。</summary>
    [RelayCommand] private void GoSearch() => ActivePage = "Search";

    [RelayCommand]
    private void OpenShellPlaylist(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        PreserveOpenPlaylistBeforeReplacement();
        ActivePage = "Favorites";
        Playlist.OpenPlaylistCommand.Execute(playlist);
    }

    /// <summary>侧边栏打开 QQ 歌单:同样复用歌单详情页(Favorites),由 QQ 客户端一次拉全量曲目。</summary>
    [RelayCommand]
    private void OpenShellQqPlaylist(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        PreserveOpenPlaylistBeforeReplacement();
        ActivePage = "Favorites";
        Playlist.OpenQqPlaylistCommand.Execute(playlist);
    }

    /// <summary>非侧栏入口(用户页/收藏页/推荐页等)的统一打开口:按歌单音源自动路由。
    /// ⚠ 必须经这里,不能写死 OpenShellPlaylistCommand —— QQ 歌单 tid 丢给网易云的
    /// GetPlaylistTrackOverviewAsync 必然失败(2026-10-03 实测:用户页点"听歌吧"空白,侧栏正常)。</summary>
    public void OpenShellPlaylistAuto(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        if (playlist.Playlist.Source == MusicSource.QQ)
            OpenShellQqPlaylistCommand.Execute(playlist);
        else
            OpenShellPlaylistCommand.Execute(playlist);
    }

    private void PreserveOpenPlaylistBeforeReplacement()
    {
        if ((ActivePage is "Favorites" or "CloudDrive") && Playlist.HasRetainedPageData)
            PushCurrentNavigation();
    }

    /// <summary>歌单行/专辑行点击歌手 → 歌手页。</summary>
    [RelayCommand]
    private async Task OpenArtistAsync(long? artistId)
    {
        if (artistId is null or 0) return;
        PreserveDetailBeforeReplacement("Artist", Artist.HasRetainedPageData);
        ActivePage = "Artist";
        try { await Artist.LoadAsync(artistId.Value); }
        catch { /* 网络失败:停留在歌手页空内容 */ }
    }

    /// <summary>打开用户页:uid=0 视为"自己"(按登录态解析;未登录时停留在空内容页)。</summary>
    [RelayCommand]
    private async Task OpenUserAsync(long? uid)
    {
        PreserveDetailBeforeReplacement("User", UserProfile.HasRetainedPageData);
        ActivePage = "User";
        try { await UserProfile.LoadAsync(uid ?? 0); }
        catch { /* 网络失败:停留在用户页空内容 */ }
    }

    /// <summary>打开 QQ 自己的用户页(QQ 歌单创建者按钮):QQ 歌单创建者恒为登录账号,
    /// 协议无他人主页跳转入口,恒解析"自己"。与网易云用户页共用同一视图。</summary>
    [RelayCommand]
    private async Task OpenQqUserAsync()
    {
        PreserveDetailBeforeReplacement("User", UserProfile.HasRetainedPageData);
        ActivePage = "User";
        try { await UserProfile.LoadQqAsync(); }
        catch { /* 网络失败:停留在用户页空内容 */ }
    }

    /// <summary>QQ 音乐曲目点击歌手 → 歌手页(按 singer mid)。</summary>
    [RelayCommand]
    private async Task OpenQqArtistAsync(string? singerMid)
    {
        if (string.IsNullOrEmpty(singerMid)) return;
        PreserveDetailBeforeReplacement("Artist", Artist.HasRetainedPageData);
        ActivePage = "Artist";
        try { await Artist.LoadQqAsync(singerMid); }
        catch { /* 网络失败:停留在歌手页空内容 */ }
    }

    /// <summary>歌单行/歌手页点击专辑 → 专辑页。</summary>
    [RelayCommand]
    private async Task OpenAlbumAsync(long? albumId)
    {
        if (albumId is null or 0) return;
        PreserveDetailBeforeReplacement("Album", Album.HasRetainedPageData);
        ActivePage = "Album";
        try { await Album.LoadAsync(albumId.Value); }
        catch { /* 网络失败:停留在专辑页空内容 */ }
    }

    /// <summary>QQ 音乐曲目点击专辑 → 专辑页(按 album mid)。</summary>
    [RelayCommand]
    private async Task OpenQqAlbumAsync(string? albumMid)
    {
        if (string.IsNullOrEmpty(albumMid)) return;
        PreserveDetailBeforeReplacement("Album", Album.HasRetainedPageData);
        ActivePage = "Album";
        try { await Album.LoadQqAsync(albumMid); }
        catch { /* 网络失败:停留在专辑页空内容 */ }
    }

    /// <summary>歌手页"热门歌曲·查看更多" → 全部歌曲页(流式分页)。</summary>
    [RelayCommand]
    private async Task OpenArtistSongsPageAsync(ArtistPageRef? artistRef)
    {
        if (artistRef is null) return;
        PreserveDetailBeforeReplacement("ArtistSongs", ArtistSongsPage.HasRetainedPageData);
        ActivePage = "ArtistSongs";
        try { await ArtistSongsPage.LoadAsync(artistRef); }
        catch { /* 网络失败:停留在页面空内容 */ }
    }

    /// <summary>歌手页"专辑·查看更多" → 全部专辑页(流式分页)。</summary>
    [RelayCommand]
    private async Task OpenArtistAlbumsPageAsync(ArtistPageRef? artistRef)
    {
        if (artistRef is null) return;
        PreserveDetailBeforeReplacement("ArtistAlbums", ArtistAlbumsPage.HasRetainedPageData);
        ActivePage = "ArtistAlbums";
        try { await ArtistAlbumsPage.LoadAsync(artistRef); }
        catch { /* 网络失败:停留在页面空内容 */ }
    }

    private void PreserveDetailBeforeReplacement(string page, bool hasRetainedPageData)
    {
        if (ActivePage == page && hasRetainedPageData)
            PushCurrentNavigation();
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
        Playlist.CancelLoginActivities();
        Playlist.Message = null;
        IsLoginDialogOpen = true;
    }

    /// <summary>红心等账号操作发现未登录/凭证失效时打开登录弹层:定位到指定音源的标签
    /// (null=保持默认网易云标签);hint 非空(凭证失效)时在弹层底部显示提示 ——
    /// OpenLoginDialog 统一清空提示,此处随后补上。</summary>
    public void OpenLoginDialogFor(MusicSource? source, string? hint = null)
    {
        OpenLoginDialogCommand.Execute(null);
        if (source == MusicSource.QQ) Playlist.IsQQLoginTab = true;
        else if (source == MusicSource.NetEase) Playlist.IsQQLoginTab = false;
        if (hint is { Length: > 0 }) Playlist.Message = hint;
    }

    /// <summary>关闭登录对话框(取消/Esc;登录成功由 MainWindow 监听 IsLoggedIn 自动关闭)。</summary>
    [RelayCommand]
    private void CloseLoginDialog()
    {
        Playlist.CancelLoginActivities();
        IsLoginDialogOpen = false;
    }

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

    // ---- 添加歌曲到歌单对话框 ----

    /// <summary>播放条“添加到歌单”的窗口内模态层状态。</summary>
    [ObservableProperty] private bool _isAddSongToPlaylistDialogOpen;

    [RelayCommand]
    private void OpenAddSongToPlaylistDialog(Models.Song? song)
    {
        if (song is null) return;
        AddSongToPlaylistDialog.Refresh(song);
        IsAddSongToPlaylistDialogOpen = true;
    }

    [RelayCommand]
    private void CloseAddSongToPlaylistDialog() => IsAddSongToPlaylistDialogOpen = false;

    private void OnSongAddedToPlaylist() => IsAddSongToPlaylistDialogOpen = false;

    // ---- 综合搜索红心平台选择对话框 ----

    [ObservableProperty] private bool _isLikeSourceDialogOpen;

    public void OpenLikeSourceDialog(SongItemViewModel? song)
    {
        if (song is null || !song.HasMultipleLikeSources) return;
        LikeSourceDialog.Refresh(song);
        IsLikeSourceDialogOpen = true;
    }

    [RelayCommand]
    private void CloseLikeSourceDialog() => IsLikeSourceDialogOpen = false;

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

    /// <summary>打开重命名对话框(聚合歌单子项右键;同一弹窗,目标为本地聚合实体)。</summary>
    [RelayCommand]
    private void OpenRenameAggregateDialog(Models.AggregatePlaylist? aggregate)
    {
        if (aggregate is null) return;
        RenamePlaylistDialog.Refresh(aggregate);
        IsRenamePlaylistDialogOpen = true;
    }

    [RelayCommand] private void CloseRenamePlaylistDialog() => IsRenamePlaylistDialogOpen = false;

    /// <summary>重命名成功回调:关弹窗,后台刷新对应侧栏分组;打开中的详情页随刷新换新实例。</summary>
    private void OnRenamePlaylistConfirmed(PlaylistItemViewModel item)
    {
        IsRenamePlaylistDialogOpen = false;
        _ = _playlist.RefreshAfterRenameAsync(item);
    }

    /// <summary>聚合重命名回调:换实例/持久化/侧栏重建已在 RenameAggregateAsync 完成,这里只关弹窗。</summary>
    private void OnAggregateRenamed(Models.AggregatePlaylist aggregate) => IsRenamePlaylistDialogOpen = false;

    /// <summary>聚合歌单重命名(本地实体):Name 为 init-only,原位换新实例持久化;
    /// 打开中的详情页同步换引用并更新标题,侧栏经重建刷新标签。</summary>
    private Task RenameAggregateAsync(Models.AggregatePlaylist aggregate, string newName)
    {
        var idx = AppState.AggregatePlaylists.IndexOf(aggregate);
        if (idx >= 0)
        {
            var fresh = new Models.AggregatePlaylist
            {
                Id = aggregate.Id,
                Name = newName,
                SourceOrder = aggregate.SourceOrder,
                Members = aggregate.Members,
            };
            AppState.AggregatePlaylists[idx] = fresh;
            AppState.Save();
            _playlist.ApplyAggregateRename(aggregate, fresh);
            RebuildShellNavigation();
        }
        return Task.CompletedTask;
    }

    // ---- 删除歌单对话框 ----

    /// <summary>删除歌单确认弹窗状态(WinUI3 ContentDialog 式窗口内弹层,宿主 MainWindow 绑定)。</summary>
    [ObservableProperty] private bool _isDeletePlaylistDialogOpen;

    /// <summary>打开删除歌单确认对话框(侧栏歌单子项右键):红心集合不可删
    /// (菜单已隐藏,此处双保险),收藏的非本人歌单由服务端拒绝。</summary>
    [RelayCommand]
    private void OpenDeletePlaylistDialog(PlaylistItemViewModel? item)
    {
        if (item is null || _playlist.IsLikedPlaylist(item.Playlist)) return;
        DeletePlaylistDialog.Refresh(item);
        IsDeletePlaylistDialogOpen = true;
    }

    /// <summary>打开删除确认对话框(聚合歌单子项右键;同一弹窗,目标为本地聚合实体)。</summary>
    [RelayCommand]
    private void OpenDeleteAggregateDialog(Models.AggregatePlaylist? aggregate)
    {
        if (aggregate is null) return;
        DeletePlaylistDialog.Refresh(aggregate);
        IsDeletePlaylistDialogOpen = true;
    }

    [RelayCommand] private void CloseDeletePlaylistDialog() => IsDeletePlaylistDialogOpen = false;

    /// <summary>删除成功回调:关弹窗,后台刷新对应侧栏分组;被删歌单若正打开,详情页回占位态。</summary>
    private void OnDeletePlaylistConfirmed(PlaylistItemViewModel item)
    {
        IsDeletePlaylistDialogOpen = false;
        _ = _playlist.RefreshAfterDeleteAsync(item);
    }

    /// <summary>聚合删除回调:移除/持久化/详情清理已在 DeleteAggregateAsync 完成,这里只关弹窗。</summary>
    private void OnAggregateDeleted(Models.AggregatePlaylist aggregate) => IsDeletePlaylistDialogOpen = false;

    /// <summary>聚合歌单删除(本地实体):仅移除聚合与成员引用(持久化于 state.json),
    /// 成员歌单本身不受影响;打开中的聚合详情页清空回占位态。</summary>
    private Task DeleteAggregateAsync(Models.AggregatePlaylist aggregate)
    {
        if (AppState.AggregatePlaylists.Remove(aggregate))
        {
            AppState.Save();
            if (ReferenceEquals(Playlist.CurrentAggregate, aggregate))
                _playlist.CloseAggregateDetail();
            RebuildShellNavigation();
        }
        return Task.CompletedTask;
    }

    /// <summary>打开"选择成员歌单"对话框(聚合歌单子项右键;添加弹窗的编辑模式,预勾现有成员)。</summary>
    [RelayCommand]
    private void OpenEditAggregateDialog(Models.AggregatePlaylist? aggregate)
    {
        if (aggregate is null) return;
        AddAggregateDialog.Refresh(aggregate);
        IsAggregateDialogOpen = true;
    }

    /// <summary>成员选择保存回调:按 Id 原位换新实例持久化;若该聚合正打开,重开刷新合并曲目
    /// (成员/顺序可能变化),否则仅重建侧栏标签。</summary>
    private void OnAggregateEditConfirmed(Models.AggregatePlaylist updated)
    {
        IsAggregateDialogOpen = false;
        var original = AppState.AggregatePlaylists.FirstOrDefault(a => a.Id == updated.Id);
        if (original is null) return;
        var idx = AppState.AggregatePlaylists.IndexOf(original);
        AppState.AggregatePlaylists[idx] = updated;
        AppState.Save();
        if (ReferenceEquals(Playlist.CurrentAggregate, original))
            Playlist.OpenAggregateCommand.Execute(updated); // 重开:成员变了,合并曲目需重拉
        RebuildShellNavigation();
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

    // ---- 音频输出设备选择弹窗 ----

    /// <summary>音频输出设备选择弹窗(WinUI3 ContentDialog 式窗口内弹层):true=显示。
    /// 设备列表与选择逻辑都在 SettingsViewModel(弹层内层 DataContext 绑 Settings)。</summary>
    [ObservableProperty] private bool _isAudioDeviceDialogOpen;

    /// <summary>关闭输出设备选择弹窗(关闭按钮/Esc)。</summary>
    [RelayCommand] private void CloseAudioDeviceDialog() => IsAudioDeviceDialogOpen = false;

    // ---- 清除缓存确认弹窗 ----

    /// <summary>清除缓存确认弹窗(WinUI3 ContentDialog 式窗口内弹层):true=显示。
    /// 确认/清理状态都在 SettingsViewModel(弹层内层 DataContext 绑 Settings)。</summary>
    [ObservableProperty] private bool _isClearCacheDialogOpen;

    /// <summary>关闭清除缓存弹窗(取消按钮/Esc)。清理进行中不接受关闭 —— 等待动画阶段
    /// 弹窗必须留到清理结束(ConfirmClearCacheAsync 完成后自行关闭)。</summary>
    [RelayCommand]
    private void CloseClearCacheDialog()
    {
        if (Settings.IsClearingCache) return;
        IsClearCacheDialogOpen = false;
    }

    // ---- 应用更新弹窗 ----

    /// <summary>应用更新弹窗(WinUI3 ContentDialog 式窗口内弹层):true=显示。
    /// 版本/更新说明/下载状态都在 SettingsViewModel(弹层内层 DataContext 绑 Settings)。</summary>
    [ObservableProperty] private bool _isUpdateDialogOpen;

    /// <summary>关闭更新弹窗(取消/Esc/稍后)。下载进行中不接受关闭 —— 下载要么完成转
    /// "立即重启"态,要么失败由 ConfirmUpdateAsync 自行收尾关闭。</summary>
    [RelayCommand]
    private void CloseUpdateDialog()
    {
        if (Settings.IsDownloadingUpdate) return;
        IsUpdateDialogOpen = false;
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
