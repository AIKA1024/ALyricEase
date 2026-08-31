using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌单 VM:MUSIC_U 粘贴登录 → 用户歌单 → 点开歌单看曲目(双击播放)。
/// 未登录显示登录卡片;已登录显示用户信息 + 歌单列表 + 选中歌单的曲目。
/// 登录对话框支持双音源切换:网易云(MUSIC_U)与 QQ 音乐(uin+qqmusic_key cookie,解锁 VIP 音质)。</summary>
public sealed partial class PlaylistViewModel : ViewModelBase
{
    internal const int AggregateNetEaseBatchSize = 100;
    internal const int AggregateQqPageSize = 300;
    private const int AggregateUiBatchSize = 50;

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly CookieStore _cookie;
    private readonly PlayerViewModel _player;

    public PlaylistViewModel(NetEaseApiClient api, QQMusicApiClient qqApi, CookieStore cookie, PlayerViewModel player)
    {
        _api = api;
        _qqApi = qqApi;
        _cookie = cookie;
        _player = player;
    }

    [ObservableProperty] private string _musicUInput = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _avatarUrl = "";
    [ObservableProperty] private string _playlistTitle = "";
    [ObservableProperty] private PlaylistItemViewModel? _selectedPlaylist;
    [ObservableProperty] private SongItemViewModel? _selectedTrack;
    [ObservableProperty] private IImage? _avatarImage;

    // ---- 登录对话框双音源 ----

    /// <summary>登录弹窗当前选中的音源页签:false=网易云(默认),true=QQ音乐。</summary>
    [ObservableProperty] private bool _isQQLoginTab;

    /// <summary>QQ音乐登录输入:y.qq.com 的整段完整 Cookie(客户端解析 uin/qqmusic_key 并原文保存;
    /// 账号接口依赖完整字段,精简两项过不了服务端校验)。</summary>
    [ObservableProperty] private string _qqCookieInput = "";

    /// <summary>QQ音乐已登录(本地 cookie 有效;解锁 VIP/320k 播放)。</summary>
    [ObservableProperty] private bool _isQqLoggedIn;

    /// <summary>QQ 登录用户昵称(歌单详情页创建者显示用;拉歌单时顺带缓存)。</summary>
    [ObservableProperty] private string _qqUserName = "";

    /// <summary>歌单详情页 hero 显示的创建者:网易云歌单 = 网易云昵称,QQ 歌单 = QQ 昵称。</summary>
    [ObservableProperty] private string _creatorName = "";

    public bool IsNetEaseLoginTab => !IsQQLoginTab;

    partial void OnIsQQLoginTabChanged(bool value) => OnPropertyChanged(nameof(IsNetEaseLoginTab));

    [RelayCommand] private void SelectNetEaseLoginTab() => IsQQLoginTab = false;

    [RelayCommand] private void SelectQQLoginTab() => IsQQLoginTab = true;

    public bool ShowLogin => !IsLoggedIn;

    /// <summary>歌单详情页内容可见性:任一音源登录即可(只登 QQ 时网易云未登录也要能看 QQ 歌单)。</summary>
    public bool HasAnyLogin => IsLoggedIn || IsQqLoggedIn;

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    /// <summary>QQ 登录用户的歌单(侧边栏"QQ音乐"分组;一次全量拉取,失败静默可重试)。</summary>
    public ObservableCollection<PlaylistItemViewModel> QqPlaylists { get; } = new();

    private bool _qqPlaylistsLoaded;

    public RangeObservableCollection<SongItemViewModel> Tracks { get; } = new();

    /// <summary>登录态变化后重算各曲目行可播性(登录成会员后 VIP 歌曲行恢复可点)。</summary>
    public void RefreshPlayability()
    {
        foreach (var t in Tracks) t.RefreshPlayability();
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLogin));
        OnPropertyChanged(nameof(HasAnyLogin));
    }

    partial void OnIsQqLoggedInChanged(bool value) => OnPropertyChanged(nameof(HasAnyLogin));

    /// <summary>进入页面时调用:恢复本地登录态(网易云拉资料,不阻塞 UI,失败静默),QQ 侧由 EnsureQqLoadedAsync 处理。</summary>
    public async Task EnsureLoadedAsync()
    {
        await EnsureQqLoadedAsync();
        // QQ 单独登录且未选中歌单时,自动打开"我喜欢"——"我的收藏"页直出喜欢列表
        // (与网易云登录后自动打开"我喜欢的音乐"行为对齐;双登录时网易云分支优先)
        if (IsQqLoggedIn && !IsLoggedIn && SelectedPlaylist is null)
        {
            var liked = QqPlaylists.FirstOrDefault(p => p.Playlist.DirId == QQMusicApiClient.LikedDirId)
                        ?? QqPlaylists.FirstOrDefault(p => p.Playlist.Name == "我喜欢")
                        ?? QqPlaylists.FirstOrDefault();
            if (liked is not null)
                await OpenQqPlaylistAsync(liked);
        }
        if (!IsLoggedIn && _cookie.MusicU is not null)
            await LoadProfileAndPlaylistsAsync();
    }

    /// <summary>恢复 QQ 登录态(cookie 纯本地解析)并按需拉取 QQ 用户歌单(失败静默,下次进入重试)。</summary>
    public async Task EnsureQqLoadedAsync()
    {
        if (!IsQqLoggedIn && _cookie.QQCookieRaw is { Length: > 0 })
            IsQqLoggedIn = _qqApi.IsLoggedIn;
        if (!IsQqLoggedIn || _qqPlaylistsLoaded) return;
        await LoadQqPlaylistsAsync();
    }

    /// <summary>拉取 QQ 用户歌单填入 QqPlaylists(一次全量),昵称一并缓存。失败静默且不标记已加载。</summary>
    private async Task LoadQqPlaylistsAsync()
    {
        try
        {
            var profile = await _qqApi.GetUserProfileAsync();
            QqUserName = profile.Nickname;
            var playlists = await _qqApi.GetUserPlaylistsAsync();
            QqPlaylists.Clear();
            foreach (var p in playlists)
                QqPlaylists.Add(new PlaylistItemViewModel(p));
            _qqPlaylistsLoaded = true;
        }
        catch (ApiException)
        {
            // Cookie 失效/网络失败:分组保持空,下次进入或重启重试
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsQQLoginTab)
        {
            await LoginQQAsync();
            return;
        }
        var raw = MusicUInput.Trim();
        if (raw.Length == 0) { Message = "请粘贴 MUSIC_U cookie"; return; }

        IsBusy = true;
        Message = null;
        try
        {
            _api.SetMusicUCookie(raw); // 解析并持久化(容错:容忍贴整段 cookie)
            await LoadProfileAndPlaylistsAsync();
            MusicUInput = "";
        }
        catch (ApiException ex)
        {
            Message = $"登录失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>QQ音乐 Cookie 登录:粘贴 y.qq.com 的整段完整 Cookie(含 uin/qqmusic_key/p_skey 等)。
    /// 本地解析通过后立即调一次服务端资料接口验证 —— 现网对不完整/失效的 Cookie 会"静默保存成登录态",
    /// 不验证会出现 UI 已登录而账号接口全 401 型假象;验证失败回滚本地与持久化 Cookie。
    /// AppShell 监听 IsQqLoggedIn 自动关弹窗。</summary>
    private async Task LoginQQAsync()
    {
        var raw = QqCookieInput.Trim();
        if (raw.Length == 0) { Message = "请粘贴 y.qq.com 的整段 Cookie"; return; }

        IsBusy = true;
        Message = null;
        try
        {
            _qqApi.SetCookie(raw);
            var profile = await _qqApi.GetUserProfileAsync();
            IsQqLoggedIn = true;
            QqUserName = profile.Nickname;
            _ = LoadQqPlaylistsAsync(); // 侧边栏"QQ音乐"分组随后出现(MainViewModel 监听集合变化)
            QqCookieInput = "";
        }
        catch (ApiException ex)
        {
            _qqApi.ClearCookie(); // 服务端不认:不留半残登录态(内存 + 存档一并清)
            IsQqLoggedIn = false;
            Message = $"登录失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    /// <summary>清除本地 Cookie 并重置登录态(账号页"删除本地Cookie"按钮调用);网易云与 QQ 一并清除。</summary>
    public void Logout()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        IsLoadingMore = false;
        _cookie.MusicU = null;
        _cookie.Save();
        _qqApi.ClearCookie();
        IsQqLoggedIn = false;
        IsLoggedIn = false;
        UserName = "";
        AvatarUrl = "";
        PlaylistTitle = "";
        Playlists.Clear();
        QqPlaylists.Clear();
        QqUserName = "";
        CreatorName = "";
        _qqPlaylistsLoaded = false;
        Tracks.Clear();
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        _isCloud = false;
        _currentAggregate = null;
        IsAggregate = false;
    }

    // 增量加载状态:trackIds 是全量权威顺序;仅已解析的歌曲会物化进 Tracks。
    private List<long> _trackIds = new();
    private readonly Dictionary<long, Song> _known = new();
    // 当前页已物化歌曲窗口：供“接下来播放”直接展示；完整的随机/顺序范围由 _playbackQueue
    // 保存轻量 trackId/成员分页描述，命中未显示下标时再解析 Song。
    private readonly List<Song> _queueSongs = new();
    private ILazySongQueue? _playbackQueue;
    private AggregateSongQueue? _aggregatePlaybackQueue;
    private int _materialized;          // 已物化进 Tracks 的曲目数(按 trackIds 顺序)
    [ObservableProperty] private bool _isLoadingMore;
    private int _loadGeneration;        // 打开新歌单时自增,使旧歌单的加载失效
    private CancellationTokenSource? _loadCancellation;
    private bool _isCloud;              // 当前展示的是音乐云盘(而非用户歌单)

    private sealed class AggregateLoadState
    {
        public required IReadOnlyList<AggregatePlaylistMember> Members { get; init; }
        public int MemberIndex { get; set; }
        public IReadOnlyList<long>? NetEaseTrackIds { get; set; }
        public Dictionary<long, Song> NetEaseKnown { get; } = new();
        public int NetEaseCursor { get; set; }
        public int QqBegin { get; set; }
        public int FailedCount { get; set; }
        public bool CoverSet { get; set; }
        public bool HasMore => MemberIndex < Members.Count;

        public void AdvanceMember()
        {
            MemberIndex++;
            NetEaseTrackIds = null;
            NetEaseKnown.Clear();
            NetEaseCursor = 0;
            QqBegin = 0;
        }
    }

    private sealed record AggregateSongBatch(MusicSource Source, IReadOnlyList<Song> Songs);
    private AggregateLoadState? _aggregateLoad;

    /// <summary>当前展示的聚合歌单(null = 非聚合页)。齿轮设置按钮按它显隐/定位。</summary>
    private Models.AggregatePlaylist? _currentAggregate;

    /// <summary>当前页是否为聚合歌单(驱动右上角齿轮设置按钮显隐)。</summary>
    [ObservableProperty] private bool _isAggregate;

    /// <summary>当前展示的聚合歌单(供设置弹窗引用;null = 非聚合页)。</summary>
    public Models.AggregatePlaylist? CurrentAggregate => _currentAggregate;

    /// <summary>聚合歌单页右上角齿轮:打开排列顺序设置弹窗(经宿主 MainViewModel 弹窗状态)。</summary>
    [RelayCommand]
    private void OpenAggregateSettings()
    {
        if (_currentAggregate is null) return;
        try { ServiceLocator.Get<MainViewModel>().OpenAggregateSettingsCommand.Execute(_currentAggregate); }
        catch { /* SelfTest/Headless 等无宿主环境 */ }
    }

    /// <summary>开始新的详情页加载并取消旧页面仍在进行的 HTTP 请求。</summary>
    private (int Generation, CancellationToken Token) BeginLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        // 旧代次的 finally 不会再改新页面状态，必须在这里解除它持有的单飞标记。
        IsLoadingMore = false;
        return (++_loadGeneration, _loadCancellation.Token);
    }

    /// <summary>页面离开时停止当前网络与增量任务；导航恢复对应入口时会建立新的加载代次。</summary>
    public void CancelCurrentLoad()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _isCloud = false;
        IsBusy = false;
        IsLoadingMore = false;
    }

    private bool IsCurrentLoad(int generation, CancellationToken token)
        => generation == _loadGeneration && !token.IsCancellationRequested;

    /// <summary>音乐云盘:复用歌单页展示。分页拉全量云盘曲目(500/页,跟随 hasMore),
    /// 行队列共享 → 播放全部/上一曲/下一曲都在云盘列表内。已在云盘页时跳过(保留现有内容)。</summary>
    [RelayCommand]
    private async Task OpenCloudAsync()
    {
        if (!IsLoggedIn || _isCloud) return;
        var (generation, ct) = BeginLoad();
        _isCloud = true;
        _currentAggregate = null;
        _aggregateLoad = null;
        IsAggregate = false;
        SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = "音乐云盘" });
        Tracks.Clear();
        PlaylistTitle = "音乐云盘";
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = true;
        Message = null;
        try
        {
            var offset = 0;
            var hasMore = true;
            while (hasMore)
            {
                var (songs, totalCount, more) = await _api.GetCloudListAsync(500, offset, ct);
                if (!IsCurrentLoad(generation, ct)) return;
                if (offset == 0)
                {
                    // 首页拿到总数后重建头部(TrackCount/CoverUrl init-only);封面用第一首有封面的歌(仿歌单页)
                    var cover = songs.FirstOrDefault(s => !string.IsNullOrEmpty(s.CoverUrl))?.CoverUrl ?? "";
                    SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = "音乐云盘", TrackCount = totalCount, CoverUrl = cover });
                    SelectedPlaylist.EnsureCoverLoaded();
                    _ = SelectedPlaylist.EnsureLargeCoverLoadedAsync(); // 头部 260px 大图
                }
                foreach (var s in songs)
                {
                    Tracks.Add(new SongItemViewModel(s, _player.PlayFromList, Tracks.Count + 1, _queueSongs, _api, "音乐云盘"));
                    _queueSongs.Add(s);
                }
                offset += songs.Count;
                hasMore = more && songs.Count > 0 && offset < 3000; // 3000 首兜底,防接口异常时死循环
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException ex)
        {
            if (generation == _loadGeneration) Message = $"加载云盘失败:{ex.Message}";
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) IsBusy = false;
        }
    }

    /// <summary>点开歌单：只取 v6 一次 → 立即把前段曲目上屏；后续按滚动增量补齐。
    /// 首屏不清零、不阻塞；大歌单(如 1000+ 首“我喜欢的音乐”)不会因全量请求而假死。</summary>
    [RelayCommand]
    private async Task OpenPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        var (generation, ct) = BeginLoad();
        _isCloud = false;
        _currentAggregate = null;
        _aggregateLoad = null;
        IsAggregate = false;
        SelectedPlaylist = playlist;
        playlist.EnsureCoverLoaded(); // 头部大封面
        _ = playlist.EnsureLargeCoverLoadedAsync(); // 600px 大图,保证头部 200px 显示清晰
        Tracks.Clear();
        PlaylistTitle = playlist.Name;
        CreatorName = UserName;
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = true;
        Message = null;
        try
        {
            var overview = await _api.GetPlaylistTrackOverviewAsync(playlist.Id, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            playlist.RefreshCover(overview.CoverUrl); // 封面随曲目变化(如"我喜欢的音乐"),URL 变了才重载
            _trackIds = overview.TrackIds.ToList();
            foreach (var s in overview.PrefixTracks)
                if (s.Id != 0) _known[s.Id] = s;
            _playbackQueue = new IndexedSongQueue(
                _trackIds, overview.PrefixTracks, _api.GetSongsByIdsAsync);

            AppendKnownTracks();
            IsBusy = false;

            // 后台静默补充到 ~200 首,让首屏滚动不断档(至多一次 song/detail 请求,不阻塞 UI)
            await LoadMoreAsync(fillTo: 200, generation: generation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException ex)
        {
            if (generation == _loadGeneration) Message = $"加载歌单失败:{ex.Message}";
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) IsBusy = false;
        }
    }

    /// <summary>点开 QQ 歌单:一次拉全量曲目(QQ 无 trackIds 增量协议,fcg 按歌单 id 整页返回)。
    /// 复用网易云歌单页 UI:hero/曲目列表/共享队列;_trackIds 留空即无增量补充,播放全部直接用已物化列表。</summary>
    [RelayCommand]
    private async Task OpenQqPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        var (generation, ct) = BeginLoad();
        _isCloud = false;
        _currentAggregate = null;
        _aggregateLoad = null;
        IsAggregate = false;
        SelectedPlaylist = playlist;
        playlist.EnsureCoverLoaded();
        _ = playlist.EnsureLargeCoverLoadedAsync();
        Tracks.Clear();
        PlaylistTitle = playlist.Name;
        CreatorName = QqUserName;
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = true;
        Message = null;
        try
        {
            var songs = await _qqApi.GetPlaylistTracksAsync(playlist.Id, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            foreach (var s in songs)
            {
                Tracks.Add(new SongItemViewModel(s, _player.PlayFromList, Tracks.Count + 1, _queueSongs, null, playlist.Name));
                _queueSongs.Add(s);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException ex)
        {
            if (generation == _loadGeneration) Message = $"加载歌单失败:{ex.Message}";
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) IsBusy = false;
        }
    }

    /// <summary>点开聚合歌单:按 SourceOrder 稳定排序成员并建立流式游标。
    /// 首批立即上屏；网易云按 100 个 trackId 补详情，QQ 按服务端 300 首分页；
    /// 当前成员耗尽后才推进下一成员，从而在动态加载时仍严格保持聚合顺序。</summary>
    [RelayCommand]
    private async Task OpenAggregateAsync(Models.AggregatePlaylist? aggregate)
    {
        if (aggregate is null) return;
        var (generation, ct) = BeginLoad();
        _isCloud = false;
        _currentAggregate = aggregate;
        IsAggregate = true;
        var members = OrderAggregateMembers(aggregate);
        _aggregateLoad = new AggregateLoadState { Members = members };
        SelectedPlaylist = new PlaylistItemViewModel(new Playlist
        {
            Name = aggregate.Name,
            Description = string.Join(" · ", members.Select(m => m.PlaylistName)),
        });
        Tracks.Clear();
        PlaylistTitle = aggregate.Name;
        CreatorName = $"聚合歌单 · {aggregate.Members.Count} 个歌单";
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _aggregatePlaybackQueue = CreateAggregatePlaybackQueue(members);
        _playbackQueue = _aggregatePlaybackQueue;
        _materialized = 0;
        IsBusy = true;
        Message = null;

        try
        {
            // 第一批优先完成并解除页面忙碌态；随后静默补到约 200 首，避免首屏刚出现就断档。
            await LoadMoreAggregateAsync(generation, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            IsBusy = false;
            while (Tracks.Count < 200 && _aggregateLoad is { HasMore: true })
            {
                var before = Tracks.Count;
                await LoadMoreAggregateAsync(generation, ct);
                if (!IsCurrentLoad(generation, ct) || Tracks.Count == before) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) IsBusy = false;
        }
    }

    private async Task LoadMoreAggregateAsync(int generation, CancellationToken ct)
    {
        if (IsLoadingMore || _aggregateLoad is not { HasMore: true } state) return;
        IsLoadingMore = true;
        try
        {
            var batch = await ReadNextAggregateBatchAsync(state, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            if (batch is null)
            {
                if (state.FailedCount > 0)
                    Message = $"{state.FailedCount} 个歌单拉取失败,已展示其余成员";
                return;
            }
            await AppendAggregateBatchAsync(batch, state, generation, ct);

            if (!state.HasMore && state.FailedCount > 0)
                Message = $"{state.FailedCount} 个歌单拉取失败,已展示其余成员";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == _loadGeneration) IsLoadingMore = false;
        }
    }

    /// <summary>读取当前成员的下一网络批次；空歌单/失败成员会在同一次请求中跳过，直到拿到数据或全部结束。</summary>
    private async Task<AggregateSongBatch?> ReadNextAggregateBatchAsync(AggregateLoadState state, CancellationToken ct)
    {
        while (state.HasMore)
        {
            ct.ThrowIfCancellationRequested();
            var member = state.Members[state.MemberIndex];
            try
            {
                var memberIndex = state.MemberIndex;
                if (member.Source == MusicSource.QQ)
                {
                    var begin = state.QqBegin;
                    var page = await _qqApi.GetPlaylistTrackPageAsync(
                        member.PlaylistId, begin, AggregateQqPageSize, ct);
                    _aggregatePlaybackQueue?.ConfigureQqMember(memberIndex, page.TotalCount);
                    state.QqBegin += AggregateQqPageSize;
                    if (!page.HasMore) state.AdvanceMember();
                    if (page.Songs.Count > 0)
                        return new AggregateSongBatch(MusicSource.QQ, page.Songs);
                    continue;
                }

                if (state.NetEaseTrackIds is null)
                {
                    var overview = await _api.GetPlaylistTrackOverviewAsync(member.PlaylistId, ct);
                    state.NetEaseTrackIds = overview.TrackIds;
                    _aggregatePlaybackQueue?.ConfigureNetEaseMember(memberIndex, overview);
                    foreach (var song in overview.PrefixTracks)
                        if (song.Id != 0) state.NetEaseKnown[song.Id] = song;
                }

                if (state.NetEaseCursor >= state.NetEaseTrackIds.Count)
                {
                    state.AdvanceMember();
                    continue;
                }

                var memberOffset = state.NetEaseCursor;
                var ids = state.NetEaseTrackIds
                    .Skip(memberOffset)
                    .Take(AggregateNetEaseBatchSize)
                    .ToList();
                var missing = ids.Where(id => !state.NetEaseKnown.ContainsKey(id)).ToList();
                if (missing.Count > 0)
                {
                    var details = await _api.GetSongsByIdsAsync(missing, ct);
                    foreach (var song in details)
                        if (song.Id != 0) state.NetEaseKnown[song.Id] = song;
                }

                var songs = ids
                    .Where(state.NetEaseKnown.ContainsKey)
                    .Select(id => state.NetEaseKnown[id])
                    .ToList();
                // 已物化歌曲由 Tracks/_queueSongs 持有；游标字典只保留后续批次，避免再重复保活整份成员歌单。
                foreach (var id in ids)
                    state.NetEaseKnown.Remove(id);
                state.NetEaseCursor += ids.Count;
                if (state.NetEaseCursor >= state.NetEaseTrackIds.Count)
                    state.AdvanceMember();
                if (songs.Count > 0)
                    return new AggregateSongBatch(MusicSource.NetEase, songs);
            }
            catch (ApiException ex)
            {
                _aggregatePlaybackQueue?.MarkMemberUnavailable(state.MemberIndex);
                state.FailedCount++;
                Message = $"歌单[{member.PlaylistName}]拉取失败:{ex.Message}";
                state.AdvanceMember();
            }
        }

        return null;
    }

    /// <summary>网络批次按最多 50 行一次的集合通知应用到 UI，给输入/渲染队列留下调度机会。</summary>
    private async Task AppendAggregateBatchAsync(
        AggregateSongBatch batch, AggregateLoadState state, int generation, CancellationToken ct)
    {
        for (var offset = 0; offset < batch.Songs.Count; offset += AggregateUiBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var songs = batch.Songs.Skip(offset).Take(AggregateUiBatchSize).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentLoad(generation, ct)) return;

                var rowStart = Tracks.Count;
                _queueSongs.AddRange(songs);
                var rows = songs.Select((song, index) =>
                    CreateTrackRow(song, rowStart + index,
                        batch.Source == MusicSource.QQ ? null : _api)).ToList();
                Tracks.AddRange(rows);
                SelectedPlaylist?.UpdateTrackCount(Tracks.Count);

                if (!state.CoverSet)
                {
                    var cover = songs.FirstOrDefault(song => !string.IsNullOrEmpty(song.CoverUrl))?.CoverUrl ?? "";
                    if (cover.Length > 0)
                    {
                        state.CoverSet = true;
                        SelectedPlaylist?.RefreshCover(cover);
                    }
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>按来源分组稳定排序，同源成员保持用户配置的原始顺序。</summary>
    internal static List<AggregatePlaylistMember> OrderAggregateMembers(AggregatePlaylist aggregate)
        => aggregate.SourceOrder == AggregateSourceOrder.QqFirst
            ? aggregate.Members.OrderBy(member => member.Source == MusicSource.QQ ? 0 : 1).ToList()
            : aggregate.Members.OrderBy(member => member.Source == MusicSource.NetEase ? 0 : 1).ToList();

    private AggregateSongQueue CreateAggregatePlaybackQueue(IReadOnlyList<AggregatePlaylistMember> members)
    {
        var descriptors = members.Select(member =>
        {
            var item = member.Source == MusicSource.QQ
                ? QqPlaylists.FirstOrDefault(candidate => candidate.Id == member.PlaylistId)
                : Playlists.FirstOrDefault(candidate => candidate.Id == member.PlaylistId);
            // 0 既可能是真空歌单，也可能是列表接口未给计数；交给播放源做一次轻量元数据确认。
            int? knownCount = item?.Playlist.TrackCount is > 0 ? item.Playlist.TrackCount : null;
            return new AggregateSongQueue.Member(member, knownCount);
        }).ToList();
        return new AggregateSongQueue(_api, _qqApi, descriptors);
    }

    /// <summary>滚动接近底部时调用：补齐下一批(≤100)曲目元数据并物化。</summary>
    public Task LoadMoreAsync()
    {
        var cancellation = _loadCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested) return Task.CompletedTask;
        return IsAggregate
            ? LoadMoreAggregateAsync(_loadGeneration, cancellation.Token)
            : LoadMoreAsync(fillTo: null, generation: _loadGeneration, cancellation.Token);
    }

    private async Task LoadMoreAsync(int? fillTo, int generation, CancellationToken ct)
    {
        if (IsLoadingMore) return;
        IsLoadingMore = true;
        try
        {
            var target = fillTo ?? Math.Min(_trackIds.Count, _materialized + 100);
            while (_materialized < target && _materialized < _trackIds.Count)
            {
                ct.ThrowIfCancellationRequested();
                // 从当前未解析位置取下一段(≤100 个缺失 id)
                var slice = new List<long>();
                for (var i = _materialized; i < _trackIds.Count && slice.Count < 100; i++)
                    if (!_known.ContainsKey(_trackIds[i])) slice.Add(_trackIds[i]);
                if (slice.Count > 0)
                {
                    var songs = await _api.GetSongsByIdsAsync(slice, ct);
                    if (!IsCurrentLoad(generation, ct)) return;
                    foreach (var s in songs)
                        if (s.Id != 0) _known[s.Id] = s;
                }

                var before = _materialized;
                AppendKnownTracks();
                if (!IsCurrentLoad(generation, ct)) return;
                // 本批未推进(缺失 id 全部无法解析)则停止,避免死循环
                if (_materialized == before) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException)
        {
            // 单批失败不拖垮歌单;滚动时重试
        }
        finally
        {
            if (generation == _loadGeneration) IsLoadingMore = false;
        }
    }

    /// <summary>把 trackIds 里连续已解析的曲目物化成列表项。
    /// UI 行与 _queueSongs 仍同步增长，但播放使用 _playbackQueue 的完整逻辑范围。</summary>
    private void AppendKnownTracks()
    {
        while (_materialized < _trackIds.Count)
        {
            var id = _trackIds[_materialized];
            if (!_known.TryGetValue(id, out var song)) break;
            Tracks.Add(CreateTrackRow(song, _materialized, _api));
            _queueSongs.Add(song);
            _materialized++;
        }
    }

    private SongItemViewModel CreateTrackRow(Song song, int zeroBasedIndex, NetEaseApiClient? api)
    {
        var lazyQueue = _playbackQueue;
        if (lazyQueue is null)
            return new SongItemViewModel(
                song, _player.PlayFromList, zeroBasedIndex + 1, _queueSongs, api, PlaylistTitle);

        lazyQueue.Remember(zeroBasedIndex, song);
        var source = PlaylistTitle;
        return new SongItemViewModel(song,
            candidate => _player.PlayFromLazyList(
                candidate, lazyQueue, zeroBasedIndex, _queueSongs, source),
            zeroBasedIndex + 1, api);
    }

    /// <summary>头部「播放全部」:从第一首开始播放；懒歌单只物化首屏，播放器按需解析完整逻辑队列。</summary>
    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Tracks.Count == 0) return;
        // 非懒队列保留原行为；懒队列无需为了播放器提前创建更多 Song/UI 行。
        var cancellation = _loadCancellation;
        if (_playbackQueue is null && cancellation is { IsCancellationRequested: false })
            _ = LoadMoreAsync(fillTo: Math.Min(_trackIds.Count, _materialized + 300),
                generation: _loadGeneration, cancellation.Token);
        await Tracks[0].PlayCommand.ExecuteAsync(null);
    }

    /// <summary>创建歌单(侧栏分组头"+"按钮):按音源路由到对应客户端。
    /// 成功返回新歌单;失败抛 ApiException(对话框内呈现)。</summary>
    public Task<Playlist> CreatePlaylistAsync(MusicSource source, string name, bool isPrivate)
        => source == MusicSource.QQ
            ? _qqApi.CreatePlaylistAsync(name, isPrivate)
            : _api.CreatePlaylistAsync(name, isPrivate);

    /// <summary>重命名歌单(侧栏右键):按歌单音源路由到对应客户端。
    /// 成功后由宿主刷新侧栏分组(RefreshAfterRenameAsync);失败抛 ApiException(对话框内呈现)。</summary>
    public Task RenamePlaylistAsync(PlaylistItemViewModel item, string newName)
        => item.Playlist.Source == MusicSource.QQ
            ? _qqApi.RenamePlaylistAsync(item.Playlist, newName)
            : _api.RenamePlaylistAsync(item.Playlist, newName);

    /// <summary>删除自己创建的歌单(侧栏右键,确认弹窗后调用):按歌单音源路由到对应客户端。
    /// 成功后由宿主刷新侧栏分组(RefreshAfterDeleteAsync);失败抛 ApiException(对话框内呈现)。</summary>
    public Task DeletePlaylistAsync(PlaylistItemViewModel item)
        => item.Playlist.Source == MusicSource.QQ
            ? _qqApi.DeletePlaylistAsync(item.Playlist)
            : _api.DeletePlaylistAsync(item.Playlist);

    /// <summary>该歌单是否为红心集合("我喜欢"):红心集合不允许重命名/删除,右键菜单据此隐藏。
    /// QQ 按资产目录 id(201)识别,网易云按拉列表时定位到的喜欢集合歌单 id。</summary>
    public bool IsLikedPlaylist(Playlist playlist)
        => playlist.Source == MusicSource.QQ
            ? playlist.DirId == QQMusicApiClient.LikedDirId || playlist.Name == "我喜欢"
            : playlist.Id != 0 && playlist.Id == _api.LikedPlaylistId;

    /// <summary>重命名成功后刷新对应侧栏分组(换新实例,侧栏名随列表更新);
    /// 若该歌单正作为详情页打开,把详情页切到新实例并同步标题(曲目不动)。</summary>
    public async Task RefreshAfterRenameAsync(PlaylistItemViewModel renamed)
    {
        var source = renamed.Playlist.Source;
        if (source == MusicSource.QQ)
            await ReloadQqPlaylistsAsync();
        else
            await ReloadNetEasePlaylistsAsync();

        var fresh = source == MusicSource.QQ
            ? QqPlaylists.FirstOrDefault(p => p.Id == renamed.Id)
            : Playlists.FirstOrDefault(p => p.Id == renamed.Id);
        if (fresh is null) return;
        if (ReferenceEquals(SelectedPlaylist, renamed))
        {
            SelectedPlaylist = fresh;
            PlaylistTitle = fresh.Name;
        }
    }

    /// <summary>删除成功后刷新对应侧栏分组;若被删歌单正作为详情页打开,清空详情回到
    /// "请选择歌单"占位态(自增加载代次使在途曲目加载作废)。</summary>
    public async Task RefreshAfterDeleteAsync(PlaylistItemViewModel deleted)
    {
        var wasOpen = ReferenceEquals(SelectedPlaylist, deleted);
        if (deleted.Playlist.Source == MusicSource.QQ)
            await ReloadQqPlaylistsAsync();
        else
            await ReloadNetEasePlaylistsAsync();

        if (wasOpen)
            ResetDetailPage();
    }

    /// <summary>聚合歌单在侧栏被重命名(集合中已换新实例)后,若打开中的详情页正是它,
    /// 换引用保持后续聚合设置弹窗的比较一致,并同步合成歌单名与标题(曲目不动)。</summary>
    public void ApplyAggregateRename(Models.AggregatePlaylist old, Models.AggregatePlaylist fresh)
    {
        if (!ReferenceEquals(_currentAggregate, old)) return;
        _currentAggregate = fresh;
        SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = fresh.Name });
        PlaylistTitle = fresh.Name;
    }

    /// <summary>聚合歌单被删除后清空详情页回占位态(自增加载代次使在途合并拉取作废)。</summary>
    public void CloseAggregateDetail()
    {
        if (_currentAggregate is null && !IsAggregate) return;
        ResetDetailPage();
    }

    /// <summary>详情页整体复位:清曲目与增量加载状态、解除打开的聚合引用,回到"请选择歌单"占位态。</summary>
    private void ResetDetailPage()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _isCloud = false;
        _currentAggregate = null;
        _aggregateLoad = null;
        IsAggregate = false;
        SelectedPlaylist = null;
        PlaylistTitle = "";
        CreatorName = "";
        Tracks.Clear();
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = false;
        IsLoadingMore = false;
        Message = null;
    }

    /// <summary>重新拉取网易云用户歌单(创建/删除歌单后刷新侧栏;静默失败,保底不清空现列表)。</summary>
    public async Task ReloadNetEasePlaylistsAsync()
    {
        if (!IsLoggedIn) return;
        try
        {
            var profile = await _api.GetUserProfileAsync();
            UserName = profile.Nickname;
            var playlists = await _api.GetUserPlaylistsAsync(profile.UserId);
            Playlists.Clear();
            foreach (var p in playlists)
                Playlists.Add(new PlaylistItemViewModel(p));
        }
        catch (ApiException)
        {
            // 刷新失败保持现列表(侧栏旧数据仍可用)
        }
    }

    /// <summary>重新拉取 QQ 用户歌单(创建/删除歌单后刷新侧栏;失败静默保持现列表)。</summary>
    public async Task ReloadQqPlaylistsAsync()
    {
        if (!IsQqLoggedIn) return;
        await LoadQqPlaylistsAsync();
    }

    private async Task LoadProfileAndPlaylistsAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var profile = await _api.GetUserProfileAsync();
            IsLoggedIn = true;
            UserName = profile.Nickname;
            CreatorName = profile.Nickname;
            AvatarUrl = profile.AvatarUrl;
            _ = LoadAvatarAsync();

            var playlists = await _api.GetUserPlaylistsAsync(profile.UserId);
            Playlists.Clear();
            foreach (var p in playlists)
                Playlists.Add(new PlaylistItemViewModel(p));

            // 网易云通常把“我喜欢的音乐”放在首位；自动打开它，使该入口直接呈现可用的歌单详情。
            if (Playlists.Count > 0)
                await OpenPlaylistAsync(Playlists[0]);
        }
        catch (ApiException ex)
        {
            Message = $"登录失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 头像缩到 128px:profile avatarUrl 是 1000px 原图,直接加载解码 ~4MB 纯浪费(当前头像尚未在 UI 显示)
    private async Task LoadAvatarAsync() => AvatarImage = await CoverLoader.LoadAsync(AvatarUrl, 128);
}
