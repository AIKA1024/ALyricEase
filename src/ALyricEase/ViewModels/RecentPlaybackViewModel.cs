using System.Collections.ObjectModel;
using System.Linq;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>本地最近播放：展示成功开播的去重历史，并复用统一歌曲行的播放、红心与详情跳转。</summary>
public sealed partial class RecentPlaybackViewModel : ViewModelBase
{
    private readonly AppStateStore _appState;
    private readonly PlayerViewModel _player;
    private readonly NetEaseApiClient _api;

    public RecentPlaybackViewModel(AppStateStore appState, PlayerViewModel player, NetEaseApiClient api)
    {
        _appState = appState;
        _player = player;
        _api = api;
        _appState.RecentSongsChanged += Rebuild;
        Rebuild();
    }

    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    public bool HasSongs => Songs.Count > 0;

    public bool ShowEmpty => !HasSongs;

    public string Subtitle => $"共 {Songs.Count} 首";

    /// <summary>登录/会员状态变化后刷新歌曲行的本地可播状态。</summary>
    public void RefreshPlayability()
    {
        foreach (var item in Songs) item.RefreshPlayability();
    }

    private void Rebuild()
    {
        var queue = _appState.RecentSongs.ToArray();
        var existing = Songs.ToDictionary(row => (row.Song.Source, row.Song.Id, row.Song.Mid));
        var desired = new List<SongItemViewModel>(queue.Length);
        for (var i = 0; i < queue.Length; i++)
        {
            var song = queue[i];
            if (existing.TryGetValue((song.Source, song.Id, song.Mid), out var row)
                && SameMetadata(row.Song, song))
            {
                row.Renumber(i + 1);
                // 行复用后也必须改绑最新顺序，否则从历史行播放仍会带入旧队列。
                row.RebindPlayback(_player.PlayFromList, queue, "最近播放");
                desired.Add(row);
            }
            else
                desired.Add(new SongItemViewModel(song, _player.PlayFromList, i + 1, queue, _api, "最近播放"));
        }
        CollectionSync.Apply(Songs, desired);
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(Subtitle));
    }

    // 历史会复制刚播放的 Song；相同快照复用行，元数据变化时仅替换该行。
    private static bool SameMetadata(Song a, Song b) => ReferenceEquals(a, b)
        || a.Name == b.Name && a.Artist == b.Artist && a.Album == b.Album
        && a.CoverUrl == b.CoverUrl && a.DurationMs == b.DurationMs
        && a.Fee == b.Fee && a.IsPurchased == b.IsPurchased
        && a.AlbumId == b.AlbumId && a.AlbumMid == b.AlbumMid
        && a.LocalFilePath == b.LocalFilePath
        && a.IsPlaybackUnavailable == b.IsPlaybackUnavailable && a.IsNoCopyright == b.IsNoCopyright
        && a.ArtistIds.SequenceEqual(b.ArtistIds) && a.ArtistNames.SequenceEqual(b.ArtistNames)
        && a.ArtistMids.SequenceEqual(b.ArtistMids);

    [RelayCommand]
    private async Task PlayAllAsync()
    {
        var firstPlayable = Songs.FirstOrDefault(item => item.IsPlayable);
        if (firstPlayable is not null)
            await firstPlayable.PlayCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void Clear() => _appState.ClearRecentSongs();
}
