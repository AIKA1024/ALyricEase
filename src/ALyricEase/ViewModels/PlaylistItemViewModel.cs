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
    private string _currentCoverUrl;

    public PlaylistItemViewModel(Playlist playlist)
    {
        Playlist = playlist;
        _currentCoverUrl = playlist.CoverUrl;
    }

    /// <summary>容器 realized 时调用:首次才拉封面(幂等)。</summary>
    public void EnsureCoverLoaded()
    {
        if (_coverRequested || Cover is not null) return;
        _coverRequested = true;
        _ = LoadCoverAsync(_currentCoverUrl);
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

    private async Task LoadCoverAsync(string url) => Cover = await CoverLoader.LoadAsync(url, 100);

    /// <summary>头部大封面:400 尺寸拉取(显示 260-300px,2x DPI 足够清晰)。仅选中时调用一次。</summary>
    private bool _largeCoverRequested;
    public async Task EnsureLargeCoverLoadedAsync()
    {
        if (_largeCoverRequested || LargeCover is not null) return;
        _largeCoverRequested = true;
        LargeCover = await CoverLoader.LoadAsync(_currentCoverUrl, 400);
    }

    /// <summary>歌单封面可能随曲目变化(如"我喜欢的音乐"自动生成封面,加歌后 coverImgUrl 会变):
    /// URL 变了才重载封面图(小图 + 大图),避免每次打开都刷新。</summary>
    public void RefreshCover(string coverUrl)
    {
        if (string.IsNullOrEmpty(coverUrl) || coverUrl == _currentCoverUrl) return;
        _currentCoverUrl = coverUrl;
        _coverRequested = false;
        _largeCoverRequested = false;
        _ = LoadCoverAsync(coverUrl);
        _ = LoadLargeCoverAsync(coverUrl);
    }

    private async Task LoadLargeCoverAsync(string url) => LargeCover = await CoverLoader.LoadAsync(url, 400);

    [ObservableProperty] private IImage? _largeCover;
}
