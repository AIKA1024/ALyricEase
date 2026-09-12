using System.Collections.ObjectModel;
using System.Linq;
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

    /// <summary>登录/会员状态变化后刷新 VIP 曲目的可播性。</summary>
    public void RefreshPlayability()
    {
        foreach (var item in Songs) item.RefreshPlayability();
    }

    private void Rebuild()
    {
        var queue = _appState.RecentSongs.ToArray();
        Songs.Clear();
        for (var i = 0; i < queue.Length; i++)
            Songs.Add(new SongItemViewModel(
                queue[i], _player.PlayFromList, i + 1, queue, _api, "最近播放"));
        OnPropertyChanged(nameof(HasSongs));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(Subtitle));
    }

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
