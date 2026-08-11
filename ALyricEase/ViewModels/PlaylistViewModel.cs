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
    private void Logout()
    {
        _cookie.MusicU = null;
        _cookie.Save();
        IsLoggedIn = false;
        UserName = "";
        AvatarUrl = "";
        PlaylistTitle = "";
        Playlists.Clear();
        Tracks.Clear();
    }

    private List<Song> _allTracks = new();

    /// <summary>点开歌单:全量取曲目元数据(v6+分批 song/detail),一次性填入列表。
    /// 列表已虚拟化,只有可见行物化 + 封面按可见懒加载,内存与歌单规模无关。</summary>
    [RelayCommand]
    private async Task OpenPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        SelectedPlaylist = playlist;
        playlist.EnsureCoverLoaded(); // 头部大封面
        _ = playlist.EnsureLargeCoverLoadedAsync(); // 600px 大图,保证头部 200px 显示清晰
        Tracks.Clear();
        PlaylistTitle = playlist.Name;
        IsBusy = true;
        Message = null;
        try
        {
            _allTracks = await _api.GetPlaylistDetailAsync(playlist.Id);
            for (var i = 0; i < _allTracks.Count; i++)
                Tracks.Add(new SongItemViewModel(_allTracks[i], _player.PlayFromList, i + 1, _allTracks));

            // 首屏 ~40 首封面预热,避免首屏全默认图;其余交给容器 realized 懒加载(不并发拉全量)
            const int prefetch = 40;
            for (var i = 0; i < Tracks.Count && i < prefetch; i++)
                Tracks[i].EnsureCoverLoaded();
        }
        catch (ApiException ex)
        {
            Message = $"加载歌单失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>头部「播放全部」:从第一首开始播放(后续可扩展为顺序队列)。</summary>
    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Tracks.Count == 0) return;
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
