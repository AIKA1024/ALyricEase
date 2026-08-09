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

    private const int MaterializeBatch = 150;

    private List<Song> _allTracks = new();
    private int _materialized;

    /// <summary>点开歌单:一次取曲目元数据(单请求,小),客户端分批物化 + 封面懒加载(真正的内存大头)。</summary>
    [RelayCommand]
    private async Task OpenPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        Tracks.Clear();
        PlaylistTitle = playlist.Name;
        IsBusy = true;
        Message = null;
        try
        {
            _allTracks = await _api.GetPlaylistDetailAsync(playlist.Id);
            _materialized = 0;
            AppendBatch();
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

    /// <summary>滚动接近底部时调用:把下一批曲目物化成列表项(封面仍按可见懒加载)。</summary>
    public void LoadMoreAsync()
    {
        if (IsBusy) return;
        AppendBatch();
    }

    private void AppendBatch()
    {
        var end = Math.Min(_materialized + MaterializeBatch, _allTracks.Count);
        for (; _materialized < end; _materialized++)
            Tracks.Add(new SongItemViewModel(_allTracks[_materialized], _player.PlayAsync));
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

    private async Task LoadAvatarAsync() => AvatarImage = await CoverLoader.LoadAsync(AvatarUrl);
}
