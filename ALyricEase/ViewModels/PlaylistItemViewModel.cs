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

    public string Description => Playlist.Description;

    /// <summary>有简介才显示(API 多数歌单无简介)。</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public int TrackCount => Playlist.TrackCount;

    public string TrackCountText => $"{TrackCount} 首";

    [ObservableProperty] private IImage? _cover;

    private async Task LoadCoverAsync() => Cover = await CoverLoader.LoadAsync(Playlist.CoverUrl, 100);

    /// <summary>头部大封面:400 尺寸拉取(显示 260-300px,2x DPI 足够清晰)。仅选中时调用一次。</summary>
    private bool _largeCoverRequested;
    public async Task EnsureLargeCoverLoadedAsync()
    {
        if (_largeCoverRequested || LargeCover is not null) return;
        _largeCoverRequested = true;
        LargeCover = await CoverLoader.LoadAsync(Playlist.CoverUrl, 400);
    }

    [ObservableProperty] private IImage? _largeCover;
}
