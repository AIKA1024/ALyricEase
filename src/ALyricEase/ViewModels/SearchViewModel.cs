using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>搜索 VM:登录页(大标题 + 搜索框 + 热门搜索 + 搜索历史)→ 结果页("kw"的搜索结果 +
/// 类型 Tab + 分区卡片,对齐原版 SearchResultView);歌曲行复用 SongItemViewModel(双击播放)。
/// 音源经 MusicApiProvider 选择,多类型走 IMusicApi.SearchAllAsync(QQ 仅歌曲+歌单,网易全量);
/// 搜索历史写入 AppStateStore(state.json)持久化,最新在前去重,上限 10 条。</summary>
public sealed partial class SearchViewModel : ViewModelBase
{
    /// <summary>搜索历史上限(原版行为:超出后丢最旧)。</summary>
    private const int HistoryLimit = 10;

    /// <summary>结果页各类型条数:All 页每区 5 条(原版),单类型 Tab 30 条。</summary>
    private const int SectionLimit = 5;

    private const int TabLimit = 30;

    private readonly PlayerViewModel _player;

    /// <summary>红心状态用网易云客户端(QQ 曲目在 SongItemViewModel 内跳过)。</summary>
    private readonly NetEaseApiClient? _neteaseApi;

    private readonly AppStateStore _appState;

    /// <summary>加载代次:新搜索/切 Tab 时自增,过期响应直接丢弃。</summary>
    private int _loadGeneration;

    public SearchViewModel(MusicApiProvider sources, PlayerViewModel player, AppStateStore appState)
    {
        Sources = sources.All;
        _neteaseApi = sources.All.FirstOrDefault(a => a.Source == MusicSource.NetEase) as NetEaseApiClient;
        _selectedSource = Sources.Count > 0 ? Sources[0] : sources.Default;
        _player = player;
        _appState = appState;
        foreach (var w in appState.SearchHistory)
            SearchHistory.Add(w);
        // 任何路径改历史(不止 RecordHistory/ClearHistory)都同步历史区显隐
        SearchHistory.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>全部已注册音源(UI 切换按钮列表)。</summary>
    public IReadOnlyList<IMusicApi> Sources { get; }

    private IMusicApi _selectedSource;

    /// <summary>当前选中的音源。</summary>
    public IMusicApi SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                OnPropertyChanged(nameof(IsNetEaseSource));
                OnPropertyChanged(nameof(IsQQSource));
                OnPropertyChanged(nameof(TrendingKeywords));
                OnPropertyChanged(nameof(Tabs));
                OnPropertyChanged(nameof(ShowTabBar));
            }
        }
    }

    /// <summary>音源切换按钮高亮态(Avalonia Classes.active 绑定)。</summary>
    public bool IsNetEaseSource => SelectedSource.Source == MusicSource.NetEase;

    public bool IsQQSource => SelectedSource.Source == MusicSource.QQ;

    [RelayCommand]
    private void SelectNetEaseSource()
        => SelectedSource = Sources.First(s => s.Source == MusicSource.NetEase);

    [RelayCommand]
    private void SelectQQSource()
        => SelectedSource = Sources.First(s => s.Source == MusicSource.QQ);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _keyword = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _isSearching;

    /// <summary>是否已发起过搜索(决定登录页/结果页)。</summary>
    [ObservableProperty] private bool _hasSearched;

    [ObservableProperty] private string? _message;

    /// <summary>结果页当前类型 Tab。</summary>
    [ObservableProperty] private SearchKind _selectedTab = SearchKind.All;

    /// <summary>Tab 栏选中索引(ListBox SelectedIndex 绑定)。</summary>
    public int SelectedTabIndex
    {
        get
        {
            var index = Tabs.ToList().FindIndex(t => t.Kind == SelectedTab);
            return index < 0 ? 0 : index;
        }
        set
        {
            if (value >= 0 && value < Tabs.Count)
                SelectedTab = Tabs[value].Kind;
        }
    }

    /// <summary>各 Tab 显隐(内容区按当前 Tab 只渲染对应分区)。</summary>
    public bool IsAllTab => SelectedTab == SearchKind.All;

    public bool IsTrackTab => SelectedTab == SearchKind.Track;

    public bool IsAlbumTab => SelectedTab == SearchKind.Album;

    public bool IsArtistTab => SelectedTab == SearchKind.Artist;

    public bool IsPlaylistTab => SelectedTab == SearchKind.Playlist;

    public bool IsUserTab => SelectedTab == SearchKind.User;

    /// <summary>查看更多:切到对应类型 Tab。</summary>
    [RelayCommand]
    private void ShowTab(SearchKind kind) => SelectedTab = kind;

    /// <summary>页脚"没有更多结果":加载完成且非空非错误。</summary>
    public bool ShowNoMore => HasSearched && !IsSearching && Message is null
        && (Songs.Count > 0 || Playlists.Count > 0 || Artists.Count > 0 || Albums.Count > 0 || Users.Count > 0);

    /// <summary>回顶按钮显隐(滚动超过一屏由视图回报)。</summary>
    [ObservableProperty] private bool _showBackToTop;

    /// <summary>类型 Tab(按音源能力:网易云全量;QQ 仅 歌曲+歌单)。</summary>
    public IReadOnlyList<SearchTabItem> Tabs
    {
        get
        {
            if (IsNetEaseSource)
                return
                [
                    new(SearchKind.All, "全部"),
                    new(SearchKind.Track, "歌曲"),
                    new(SearchKind.Album, "专辑"),
                    new(SearchKind.Artist, "表演者"),
                    new(SearchKind.Playlist, "歌单"),
                    new(SearchKind.User, "用户"),
                ];
            if (IsQQSource)
                return
                [
                    new(SearchKind.All, "全部"),
                    new(SearchKind.Track, "歌曲"),
                    new(SearchKind.Playlist, "歌单"),
                ];
            return [new(SearchKind.Track, "歌曲")];
        }
    }

    /// <summary>单 Tab 无切换意义时隐藏整条 Tab 栏。</summary>
    public bool ShowTabBar => Tabs.Count > 1;

    /// <summary>"kw"的搜索结果(结果页大标题,对齐原版)。</summary>
    public string ResultsTitle => $"\"{Keyword.Trim()}\"的搜索结果";

    // ---- 结果页分区(All 页只显示非空区;单类型 Tab 只有一个区) ----

    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    public ObservableCollection<SearchPlaylistItemViewModel> Playlists { get; } = new();

    public ObservableCollection<SearchArtistItemViewModel> Artists { get; } = new();

    public ObservableCollection<SearchAlbumItemViewModel> Albums { get; } = new();

    public ObservableCollection<SearchUserItemViewModel> Users { get; } = new();

    public bool HasSongs => Songs.Count > 0;

    public bool HasPlaylists => Playlists.Count > 0;

    public bool HasArtists => Artists.Count > 0;

    public bool HasAlbums => Albums.Count > 0;

    public bool HasUsers => Users.Count > 0;

    public bool ShowLanding => !HasSearched && !IsSearching;

    public bool ShowResults => HasSearched;

    /// <summary>结果页空态/错误:显示居中大号 Message(没有结果 20px)。</summary>
    public bool ShowEmpty => HasSearched && !IsSearching && Message is not null;

    /// <summary>登录态变化后重算各行可播性(登录成会员后 VIP 歌曲行恢复可点)。</summary>
    public void RefreshPlayability()
    {
        foreach (var r in Songs) r.RefreshPlayability();
    }

    partial void OnHasSearchedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLanding));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ResultsTitle));
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLanding));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowNoMore));
    }

    partial void OnMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowNoMore));
    }

    partial void OnSelectedTabChanged(SearchKind value)
    {
        OnPropertyChanged(nameof(SelectedTabIndex));
        OnPropertyChanged(nameof(IsAllTab));
        OnPropertyChanged(nameof(IsTrackTab));
        OnPropertyChanged(nameof(IsAlbumTab));
        OnPropertyChanged(nameof(IsArtistTab));
        OnPropertyChanged(nameof(IsPlaylistTab));
        OnPropertyChanged(nameof(IsUserTab));
        if (HasSearched && !_suppressTabLoad)
            _ = LoadTabAsync(value);
    }

    /// <summary>打开搜索结果里的歌单:按源路由侧边栏同款歌单详情页(复用 Favorites 页)。</summary>
    [RelayCommand]
    private void OpenSearchPlaylist(SearchPlaylistItemViewModel? item)
    {
        if (item is null) return;
        var main = ServiceLocator.Get<MainViewModel>();
        var playlist = new Playlist
        {
            Id = item.Item.Id,
            Name = item.Item.Name,
            CoverUrl = item.Item.CoverUrl,
            TrackCount = item.Item.TrackCount,
            Source = item.Item.Source,
        };
        if (item.IsQq)
            main.OpenShellQqPlaylistCommand.Execute(new PlaylistItemViewModel(playlist));
        else
            main.OpenShellPlaylistCommand.Execute(new PlaylistItemViewModel(playlist));
    }

    /// <summary>打开搜索结果里的歌手:按源路由歌手页(网易云按 id,QQ 按 mid)。</summary>
    [RelayCommand]
    private async Task OpenSearchArtist(SearchArtistItemViewModel? item)
    {
        if (item is null) return;
        var main = ServiceLocator.Get<MainViewModel>();
        try
        {
            if (item.IsQq)
                await main.OpenQqArtistCommand.ExecuteAsync(item.Item.Mid).ConfigureAwait(true);
            else
                await main.OpenArtistCommand.ExecuteAsync(item.Item.Id).ConfigureAwait(true);
        }
        catch { /* 网络失败:停留在歌手页空内容,与其它入口一致 */ }
    }

    /// <summary>打开搜索结果里的专辑:按源路由专辑页。</summary>
    [RelayCommand]
    private async Task OpenSearchAlbum(SearchAlbumItemViewModel? item)
    {
        if (item is null) return;
        var main = ServiceLocator.Get<MainViewModel>();
        try
        {
            if (item.IsQq)
                await main.OpenQqAlbumCommand.ExecuteAsync(item.Item.Mid).ConfigureAwait(true);
            else
                await main.OpenAlbumCommand.ExecuteAsync(item.Item.Id).ConfigureAwait(true);
        }
        catch { /* 网络失败:停留在专辑页空内容 */ }
    }

    [RelayCommand]
    private void SearchKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        Keyword = keyword.Trim();
        SearchCommand.Execute(null);
    }

    /// <summary>回到搜索登录页(清空结果)。</summary>
    [RelayCommand]
    private void BackToLanding() => ResetToLanding();

    /// <summary>回到搜索登录页并复位结果页状态(外壳返回与菜单重进共用;
    /// 在途请求全部作废,Tab 回"全部",回顶钮复位)。</summary>
    public void ResetToLanding()
    {
        _loadGeneration++; // 在途请求全部作废
        HasSearched = false;
        Keyword = "";
        Message = null;
        ShowBackToTop = false;
        // HasSearched 已为 false,OnSelectedTabChanged 不会触发加载
        SelectedTab = SearchKind.All;
        ClearResultCollections();
    }

    /// <summary>结果页滚回顶部(视图滚动监听调 IsScrolledFarFromTop;按钮调本命令)。</summary>
    [RelayCommand]
    private void BackToTop() => BackToTopRequested?.Invoke();

    /// <summary>视图订阅:回顶按钮点击后由视图执行实际滚动。</summary>
    public event Action? BackToTopRequested;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var keyword = Keyword.Trim();
        if (keyword.Length == 0) return;

        _loadGeneration++; // 作废在途加载
        HasSearched = true;
        IsSearching = true;
        Message = null;
        OnPropertyChanged(nameof(ResultsTitle));

        // 新搜索一律回"全部"页;抑制 OnSelectedTabChanged 的重复加载,统一由下方执行
        if (SelectedTab != SearchKind.All)
        {
            _suppressTabLoad = true;
            SelectedTab = SearchKind.All;
            _suppressTabLoad = false;
        }
        try
        {
            await LoadTabCoreAsync(SearchKind.All, keyword).ConfigureAwait(true);
            if (Message is null)
                RecordHistory(keyword); // 仅成功返回时记入历史(网络失败不算)
        }
        finally
        {
            IsSearching = false;
        }
    }

    private bool CanSearch() => !IsSearching && Keyword.Trim().Length > 0;

    private bool _suppressTabLoad;

    partial void OnKeywordChanged(string value) => OnPropertyChanged(nameof(ResultsTitle));

    /// <summary>切 Tab:重新拉该类型(30 条;All 5 条/区)。</summary>
    private async Task LoadTabAsync(SearchKind tab)
    {
        if (_suppressTabLoad || !HasSearched) return;
        var keyword = Keyword.Trim();
        if (keyword.Length == 0) return;

        IsSearching = true;
        Message = null;
        try
        {
            await LoadTabCoreAsync(tab, keyword).ConfigureAwait(true);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>实际加载:按 kind 调 SearchAllAsync 并填充分区;过期代次直接丢弃。</summary>
    private async Task LoadTabCoreAsync(SearchKind kind, string keyword)
    {
        var generation = ++_loadGeneration;
        try
        {
            var limit = kind == SearchKind.All ? SectionLimit : TabLimit;
            var result = await SelectedSource.SearchAllAsync(keyword, kind, limit).ConfigureAwait(true);
            if (generation != _loadGeneration) return; // 期间新搜索/切页,丢弃过期结果

            Songs.Clear(); Playlists.Clear(); Artists.Clear(); Albums.Clear(); Users.Clear();
            NotifySectionFlags();

            if (result is null)
            {
                Message = "没有搜索结果";
                return;
            }
            foreach (var song in result.Songs)
                Songs.Add(new SongItemViewModel(song, _player.PlayFromList, queue: result.Songs,
                    api: _neteaseApi, source: $"{SelectedSource.DisplayName}·搜索"));
            foreach (var p in result.Playlists) Playlists.Add(new SearchPlaylistItemViewModel(p));
            foreach (var a in result.Artists) Artists.Add(new SearchArtistItemViewModel(a));
            foreach (var a in result.Albums) Albums.Add(new SearchAlbumItemViewModel(a));
            foreach (var u in result.Users) Users.Add(new SearchUserItemViewModel(u));
            NotifySectionFlags();

            if (Songs.Count == 0 && Playlists.Count == 0 && Artists.Count == 0 &&
                Albums.Count == 0 && Users.Count == 0)
                Message = "没有搜索结果";
        }
        catch (ApiException ex)
        {
            if (generation != _loadGeneration) return;
            Message = $"搜索失败:{ex.Message}";
        }
    }

    private void ClearResultCollections()
    {
        Songs.Clear(); Playlists.Clear(); Artists.Clear(); Albums.Clear(); Users.Clear();
        NotifySectionFlags();
    }

    private void NotifySectionFlags()
    {
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(HasArtists));
        OnPropertyChanged(nameof(HasAlbums));
        OnPropertyChanged(nameof(HasUsers));
        OnPropertyChanged(nameof(ShowNoMore));
    }

    /// <summary>热门搜索关键词(登录页 chips,点按直接搜索);随音源切换刷新。</summary>
    public IReadOnlyList<string> TrendingKeywords => IsQQSource
        ? ["周杰伦", "晴天", "林俊杰", "陈奕迅", "稻香", "孤勇者", "起风了", "光年之外", "消愁",
           "平凡之路", "漠河舞厅", "云与海", "白月光与朱砂痣", "岁月神偷", "飞鸟和蝉", "芒种",
           "少年", "后来", "突然的自我", "加减乘除"]
        : ["甲乙丙丁", "海屿你", "梨花雨", "谚歌", "失眠", "四口", "皇家蓝(ft. Rapeter)", "碎碎念",
           "夏日尽头的我们 N3", "我不难过", "我好想你", "周旋", "浴室", "颜如玉", "知了",
           "同花顺", "樱花草", "认真的雪", "你", "雨爱"];

    /// <summary>搜索历史(最新在前;点按直接搜索,历史区随条目有无整段显隐)。</summary>
    public ObservableCollection<string> SearchHistory { get; } = new();

    public bool HasHistory => SearchHistory.Count > 0;

    /// <summary>清除全部搜索历史(同步落盘)。</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        SearchHistory.Clear();
        _appState.SearchHistory.Clear();
        _appState.Save();
        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>记录搜索词:去重(忽略大小写)后插到最前,超限丢最旧,落盘。</summary>
    private void RecordHistory(string keyword)
    {
        var existing = SearchHistory.FirstOrDefault(w =>
            string.Equals(w, keyword, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SearchHistory.Remove(existing);
            _appState.SearchHistory.Remove(existing);
        }
        while (SearchHistory.Count >= HistoryLimit)
        {
            var oldest = SearchHistory[^1];
            SearchHistory.Remove(oldest);
            _appState.SearchHistory.Remove(oldest);
        }
        SearchHistory.Insert(0, keyword);
        _appState.SearchHistory.Insert(0, keyword);
        _appState.Save();
        OnPropertyChanged(nameof(HasHistory));
    }
}

/// <summary>结果页类型 Tab 项(标签 + 对应 SearchKind;图标由视图按 Kind 映射)。</summary>
public sealed record SearchTabItem(SearchKind Kind, string Label);
