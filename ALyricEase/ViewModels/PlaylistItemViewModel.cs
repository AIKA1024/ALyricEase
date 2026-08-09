using System;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ALyricEase.ViewModels;

/// <summary>歌单列表项:展示封面 + 名称 + 曲目数。封面后台加载。</summary>
public sealed partial class PlaylistItemViewModel : ViewModelBase
{
    private bool _coverRequested;

    public PlaylistItemViewModel(Playlist playlist)
    {
        Playlist = playlist;
    }

    /// <summary>容器 realized 时调用:首次才拉封面(幂等)。</summary>
    public void EnsureCoverLoaded()
    {
        if (_coverRequested || Cover is not null) return;
        _coverRequested = true;
        _ = LoadCoverAsync();
    }

    public Playlist Playlist { get; }

    public long Id => Playlist.Id;

    public string Name => Playlist.Name;

    public int TrackCount => Playlist.TrackCount;

    [ObservableProperty] private IImage? _cover;

    private async Task LoadCoverAsync() => Cover = await CoverLoader.LoadAsync(Playlist.CoverUrl, 100);
}
