using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌单 VM:MUSIC_U 粘贴登录 → 用户歌单 → 点开歌单看曲目(双击播放)。
/// 未登录显示登录卡片;已登录显示用户信息 + 歌单列表 + 选中歌单的曲目。
/// 登录对话框支持双音源切换:网易云(MUSIC_U)与 QQ 音乐(uin+qqmusic_key cookie,解锁 VIP 音质)。</summary>
public sealed partial class PlaylistViewModel : ViewModelBase
{
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

    public ObservableCollection<SongItemViewModel> Tracks { get; } = new();

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
        if (IsLoggedIn || _cookie.MusicU is null) return;
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
        _isCloud = false;
        _currentAggregate = null;
        IsAggregate = false;
    }

    // 增量加载状态:trackIds 是全量权威顺序;仅已解析的歌曲会物化进 Tracks。
    private List<long> _trackIds = new();
    private readonly Dictionary<long, Song> _known = new();
    // 当前歌单的共享播放队列:所有曲目行持有同一列表引用,随物化增长;
    // 点击播放时 PlayerViewModel.SetQueue 会 ToList() 快照,即"此刻已物化的完整歌单"。
    private readonly List<Song> _queueSongs = new();
    private int _materialized;          // 已物化进 Tracks 的曲目数(按 trackIds 顺序)
    private bool _isLoadingMore;
    private int _loadGeneration;        // 打开新歌单时自增,使旧歌单的加载失效
    private bool _isCloud;              // 当前展示的是音乐云盘(而非用户歌单)

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

    /// <summary>音乐云盘:复用歌单页展示。分页拉全量云盘曲目(500/页,跟随 hasMore),
    /// 行队列共享 → 播放全部/上一曲/下一曲都在云盘列表内。已在云盘页时跳过(保留现有内容)。</summary>
    [RelayCommand]
    private async Task OpenCloudAsync()
    {
        if (!IsLoggedIn || _isCloud) return;
        var generation = ++_loadGeneration;
        _isCloud = true;
        _currentAggregate = null;
        IsAggregate = false;
        SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = "音乐云盘" });
        Tracks.Clear();
        PlaylistTitle = "音乐云盘";
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _materialized = 0;
        IsBusy = true;
        Message = null;
        try
        {
            var offset = 0;
            var hasMore = true;
            while (hasMore)
            {
                var (songs, totalCount, more) = await _api.GetCloudListAsync(500, offset);
                if (generation != _loadGeneration) return; // 期间打开了别的歌单,丢弃过期结果
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
        catch (ApiException ex)
        {
            if (generation == _loadGeneration) Message = $"加载云盘失败:{ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration) IsBusy = false;
        }
    }

    /// <summary>点开歌单：只取 v6 一次 → 立即把前段曲目上屏；后续按滚动增量补齐。
    /// 首屏不清零、不阻塞；大歌单(如 1000+ 首“我喜欢的音乐”)不会因全量请求而假死。</summary>
    [RelayCommand]
    private async Task OpenPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        var generation = ++_loadGeneration;
        _isCloud = false;
        _currentAggregate = null;
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
        _materialized = 0;
        IsBusy = true;
        Message = null;
        try
        {
            var overview = await _api.GetPlaylistTrackOverviewAsync(playlist.Id);
            if (generation != _loadGeneration) return; // 期间切了别的歌单,丢弃过期结果
            playlist.RefreshCover(overview.CoverUrl); // 封面随曲目变化(如"我喜欢的音乐"),URL 变了才重载
            _trackIds = overview.TrackIds.ToList();
            foreach (var s in overview.PrefixTracks)
                if (s.Id != 0) _known[s.Id] = s;

            AppendKnownTracks();
            IsBusy = false;

            // 后台静默补充到 ~200 首,让首屏滚动不断档(至多一次 song/detail 请求,不阻塞 UI)
            await LoadMoreAsync(fillTo: 200, generation: generation);
        }
        catch (ApiException ex)
        {
            if (generation == _loadGeneration) Message = $"加载歌单失败:{ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration) IsBusy = false;
        }
    }

    /// <summary>点开 QQ 歌单:一次拉全量曲目(QQ 无 trackIds 增量协议,fcg 按歌单 id 整页返回)。
    /// 复用网易云歌单页 UI:hero/曲目列表/共享队列;_trackIds 留空即无增量补充,播放全部直接用已物化列表。</summary>
    [RelayCommand]
    private async Task OpenQqPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        var generation = ++_loadGeneration;
        _isCloud = false;
        _currentAggregate = null;
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
        _materialized = 0;
        IsBusy = true;
        Message = null;
        try
        {
            var songs = await _qqApi.GetPlaylistTracksAsync(playlist.Id);
            if (generation != _loadGeneration) return; // 期间切了别的歌单,丢弃过期结果
            foreach (var s in songs)
            {
                Tracks.Add(new SongItemViewModel(s, _player.PlayFromList, Tracks.Count + 1, _queueSongs, null, playlist.Name));
                _queueSongs.Add(s);
            }
        }
        catch (ApiException ex)
        {
            if (generation == _loadGeneration) Message = $"加载歌单失败:{ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration) IsBusy = false;
        }
    }

    /// <summary>点开聚合歌单:按 SourceOrder 排序成员(网易云在前/QQ在前,组内保持原序),
    /// 依次拉取各成员歌单全量曲目(网易云 GetPlaylistDetailAsync / QQ GetPlaylistTracksAsync)
    /// 合并进同一个列表与共享队列;单成员失败不影响其余(Message 提示),
    /// 合并完成后重建头部显示合计曲目数与成员歌单名。</summary>
    [RelayCommand]
    private async Task OpenAggregateAsync(Models.AggregatePlaylist? aggregate)
    {
        if (aggregate is null) return;
        var generation = ++_loadGeneration;
        _isCloud = false;
        _currentAggregate = aggregate;
        IsAggregate = true;
        SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = aggregate.Name });
        Tracks.Clear();
        PlaylistTitle = aggregate.Name;
        CreatorName = $"聚合歌单 · {aggregate.Members.Count} 个歌单";
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _materialized = 0;
        IsBusy = true;
        Message = null;
        var failed = 0;
        try
        {
            // 按来源分组排序(稳定排序:同源成员保持原顺序)
            var members = aggregate.SourceOrder == AggregateSourceOrder.QqFirst
                ? aggregate.Members.OrderBy(m => m.Source == MusicSource.QQ ? 0 : 1).ToList()
                : aggregate.Members.OrderBy(m => m.Source == MusicSource.NetEase ? 0 : 1).ToList();
            foreach (var member in members)
            {
                if (generation != _loadGeneration) return; // 期间切了别的歌单,丢弃过期结果
                List<Song> songs;
                try
                {
                    songs = member.Source == MusicSource.QQ
                        ? await _qqApi.GetPlaylistTracksAsync(member.PlaylistId)
                        : await _api.GetPlaylistDetailAsync(member.PlaylistId);
                }
                catch (ApiException ex)
                {
                    failed++;
                    Message = $"歌单[{member.PlaylistName}]拉取失败:{ex.Message}";
                    continue;
                }
                foreach (var s in songs)
                {
                    Tracks.Add(new SongItemViewModel(s, _player.PlayFromList, Tracks.Count + 1, _queueSongs,
                        member.Source == MusicSource.QQ ? null : _api, PlaylistTitle));
                    _queueSongs.Add(s);
                }
            }
            if (generation != _loadGeneration) return;
            // 合并完成:重建头部显示合计曲目数;封面取显示顺序第一首歌曲的封面
            // (走 400px 大图加载,QQ 图床自动就近升档到 500,避免直接用小缩略图 URL 发糊),
            // 简介按显示顺序列成员歌单名
            var cover = Tracks.Count > 0 ? Tracks[0].Song.CoverUrl : "";
            SelectedPlaylist = new PlaylistItemViewModel(new Playlist
            {
                Name = aggregate.Name,
                TrackCount = Tracks.Count,
                CoverUrl = cover,
                Description = string.Join(" · ", members.Select(m => m.PlaylistName)),
            });
            if (cover.Length > 0)
            {
                SelectedPlaylist.EnsureCoverLoaded();
                _ = SelectedPlaylist.EnsureLargeCoverLoadedAsync(); // 头部 260px 大图
            }
            if (failed > 0 && Message is null) Message = $"{failed} 个歌单拉取失败,已展示其余成员";
        }
        finally
        {
            if (generation == _loadGeneration) IsBusy = false;
        }
    }

    /// <summary>滚动接近底部时调用：补齐下一批(≤100)曲目元数据并物化。</summary>
    public Task LoadMoreAsync() => LoadMoreAsync(fillTo: null, generation: _loadGeneration);

    private async Task LoadMoreAsync(int? fillTo, int generation)
    {
        if (_isLoadingMore) return;
        _isLoadingMore = true;
        try
        {
            var target = fillTo ?? Math.Min(_trackIds.Count, _materialized + 100);
            while (_materialized < target && _materialized < _trackIds.Count)
            {
                // 从当前未解析位置取下一段(≤100 个缺失 id)
                var slice = new List<long>();
                for (var i = _materialized; i < _trackIds.Count && slice.Count < 100; i++)
                    if (!_known.ContainsKey(_trackIds[i])) slice.Add(_trackIds[i]);
                if (slice.Count > 0)
                {
                    var songs = await _api.GetSongsByIdsAsync(slice);
                    if (generation != _loadGeneration) return; // 已切歌单
                    foreach (var s in songs)
                        if (s.Id != 0) _known[s.Id] = s;
                }

                var before = _materialized;
                AppendKnownTracks();
                if (generation != _loadGeneration) return;
                // 本批未推进(缺失 id 全部无法解析)则停止,避免死循环
                if (_materialized == before) break;
            }
        }
        catch (ApiException)
        {
            // 单批失败不拖垮歌单;滚动时重试
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    /// <summary>把 trackIds 里连续已解析的曲目物化成列表项。
    /// 所有行共享 _queueSongs(与 Tracks 同步增长):先物化的行不会再拿到比后加载批次更短的旧队列,
    /// 点击播放时按"此刻已物化的完整歌单"快照入队。</summary>
    private void AppendKnownTracks()
    {
        while (_materialized < _trackIds.Count)
        {
            var id = _trackIds[_materialized];
            if (!_known.TryGetValue(id, out var song)) break;
            Tracks.Add(new SongItemViewModel(song, _player.PlayFromList, _materialized + 1, _queueSongs, _api, PlaylistTitle));
            _queueSongs.Add(song);
            _materialized++;
        }
    }

    /// <summary>头部「播放全部」:从第一首开始播放,播放队列 = 当前已物化的完整歌单(经首行共享队列注入)。
    /// 增量模式下先保证队列里有足量已物化曲目,再开始播。</summary>
    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Tracks.Count == 0) return;
        // 静默补齐更多已物化曲目(不阻塞),使“播放全部”的队列更长
        _ = LoadMoreAsync(fillTo: Math.Min(_trackIds.Count, _materialized + 300), generation: _loadGeneration);
        await Tracks[0].PlayCommand.ExecuteAsync(null);
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
