using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>“添加到歌单”模态窗口 VM：按当前歌曲音源列出账号拥有的可写歌单，
/// 支持名称筛选；单击歌单立即写入，成功关闭，失败留在窗口内显示服务端文案。</summary>
public sealed partial class AddSongToPlaylistDialogViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;
    private readonly Action _onAdded;
    private readonly List<PlaylistItemViewModel> _allItems = [];
    private Song? _song;

    public AddSongToPlaylistDialogViewModel(PlaylistViewModel playlist, Action onAdded)
    {
        _playlist = playlist;
        _onAdded = onAdded;
    }

    /// <summary>筛选后的可写歌单。复用侧栏条目 VM，以共享已加载的封面和曲目数。</summary>
    public ObservableCollection<PlaylistItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _message;

    [ObservableProperty] private bool _hasItems;

    public bool HasNoItems => !HasItems;

    public string SongText => _song is null
        ? ""
        : SongRecordingResolver.GetSources(_song).Count > 1
            ? $"将「{_song.Name}」添加到网易云音乐或 QQ 音乐歌单："
            : $"将「{_song.Name}」添加到：";

    public string EmptyText
    {
        get
        {
            if (_song is null) return "当前没有可添加的歌曲。";
            var sources = SongRecordingResolver.GetSources(_song);
            var loggedIn = sources.Any(IsLoggedIn);
            if (!loggedIn)
                return sources.Count > 1
                    ? "尚未登录网易云音乐或 QQ 音乐账号。"
                    : $"尚未登录{(sources[0] == MusicSource.QQ ? "QQ音乐" : "网易云音乐")}账号。";
            return SearchText.Trim().Length > 0
                ? "没有匹配的歌单。"
                : sources.Count > 1
                    ? "已登录的平台暂无可添加的歌单。"
                    : "当前账号暂无可添加的歌单。";
        }
    }

    /// <summary>每次打开都按当前侧栏快照重建候选。合并歌曲同时列出两个平台的可写歌单。</summary>
    public void Refresh(Song song)
    {
        _song = song;
        SearchText = "";
        Message = null;
        IsBusy = false;
        _allItems.Clear();
        foreach (var source in SongRecordingResolver.GetSources(song))
        {
            var sourceItems = source == MusicSource.QQ
                ? _playlist.QqPlaylists
                : _playlist.Playlists;
            _allItems.AddRange(sourceItems.Where(item => item.Playlist.CanAddTracks));
        }
        ApplyFilter();
        OnPropertyChanged(nameof(SongText));
        OnPropertyChanged(nameof(EmptyText));
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(EmptyText));
    }

    partial void OnHasItemsChanged(bool value) => OnPropertyChanged(nameof(HasNoItems));

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        Items.Clear();
        foreach (var item in _allItems)
        {
            if (query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                Items.Add(item);
        }
        HasItems = Items.Count > 0;
    }

    [RelayCommand]
    private async Task AddAsync(PlaylistItemViewModel? item)
    {
        if (item is null || _song is null || IsBusy) return;
        var recording = SongRecordingResolver.Resolve(_song, item.Playlist.Source);
        if (recording is null)
        {
            Message = $"这条搜索结果没有{item.SourceLabel}版本，无法添加。";
            return;
        }
        IsBusy = true;
        Message = null;
        try
        {
            await _playlist.AddSongToPlaylistAsync(item.Playlist, recording);
            item.UpdateTrackCount(item.TrackCount + 1);
            _onAdded();
        }
        catch (ApiException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsLoggedIn(MusicSource source)
        => source == MusicSource.QQ ? _playlist.IsQqLoggedIn : _playlist.IsLoggedIn;
}
