using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌单 VM:MUSIC_U 粘贴登录 → 用户歌单 → 点开歌单看曲目(双击播放)。
/// 未登录显示登录卡片;已登录显示用户信息 + 歌单列表 + 选中歌单的曲目。</summary>
public sealed partial class PlaylistViewModel : ViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly CookieStore _cookie;
    private readonly PlayerViewModel _player;

    public PlaylistViewModel(NetEaseApiClient api, CookieStore cookie, PlayerViewModel player)
    {
        _api = api;
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

    public bool ShowLogin => !IsLoggedIn;

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();
    public ObservableCollection<SongItemViewModel> Tracks { get; } = new();

    partial void OnIsLoggedInChanged(bool value) => OnPropertyChanged(nameof(ShowLogin));

    /// <summary>进入页面时调用:已存 MUSIC_U 则恢复登录态(不阻塞 UI,失败静默)。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (IsLoggedIn || _cookie.MusicU is null) return;
        await LoadProfileAndPlaylistsAsync();
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
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

    [RelayCommand]
    /// <summary>清除本地 Cookie 并重置登录态(账号页"删除本地Cookie"按钮调用)。</summary>
    public void Logout()
    {
        _cookie.MusicU = null;
        _cookie.Save();
        IsLoggedIn = false;
        UserName = "";
        AvatarUrl = "";
        PlaylistTitle = "";
        Playlists.Clear();
        Tracks.Clear();
        _isCloud = false;
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

    /// <summary>音乐云盘:复用歌单页展示。分页拉全量云盘曲目(500/页,跟随 hasMore),
    /// 行队列共享 → 播放全部/上一曲/下一曲都在云盘列表内。已在云盘页时跳过(保留现有内容)。</summary>
    [RelayCommand]
    private async Task OpenCloudAsync()
    {
        if (!IsLoggedIn || _isCloud) return;
        var generation = ++_loadGeneration;
        _isCloud = true;
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
        SelectedPlaylist = playlist;
        playlist.EnsureCoverLoaded(); // 头部大封面
        _ = playlist.EnsureLargeCoverLoadedAsync(); // 600px 大图,保证头部 200px 显示清晰
        Tracks.Clear();
        PlaylistTitle = playlist.Name;
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
