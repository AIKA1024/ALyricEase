using System;
using ALyricEase.Models;

namespace ALyricEase.ViewModels;

/// <summary>歌单列表项:展示封面 + 名称 + 曲目数。封面后台加载。</summary>
public sealed partial class PlaylistItemViewModel : ViewModelBase
{
    private string _currentCoverUrl;

    public PlaylistItemViewModel(Playlist playlist)
    {
        Playlist = playlist;
        _currentCoverUrl = playlist.CoverUrl;
        _trackCount = playlist.TrackCount;
        _playCount = playlist.PlayCount;
    }

    public Playlist Playlist { get; }

    public long Id => Playlist.Id;

    public string Name => Playlist.Name;

    public string CoverUrl => _currentCoverUrl;

    public string Description => Playlist.Description;

    public string SourceLabel => Playlist.Source == Services.MusicSource.QQ
        ? "QQ 音乐"
        : "网易云音乐";

    /// <summary>有简介才显示(API 多数歌单无简介)。</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    private int _trackCount;

    public int TrackCount => _trackCount;

    public string TrackCountText => $"{TrackCount} 首";

    /// <summary>累计播放次数格式化文本(用户页角标用;0 显示 "0")。
    /// QQ 列表接口不下发(恒 0),用户页后台补拉后经 <see cref="UpdatePlayCount"/> 原地更新。</summary>
    private double _playCount;

    public string PlayCountText => Services.NetEase.NetEaseApiClient.FormatPlayCount(_playCount);

    /// <summary>播放量后台补拉落地(与 <see cref="UpdateTrackCount"/> 同款:不替换 VM 实例)。</summary>
    public void UpdatePlayCount(double value)
    {
        if (_playCount == value) return;
        _playCount = value;
        OnPropertyChanged(nameof(PlayCountText));
    }

    /// <summary>歌单创建者(用户页 more 菜单"创建者"项/跳用户页用;非用户页入口为 null)。</summary>
    public string? CreatorName { get; init; }

    public long CreatorId { get; init; }

    /// <summary>流式加载页面更新已物化数量，不必反复替换整个 VM 或重新加载头部封面。</summary>
    public void UpdateTrackCount(int value)
    {
        if (_trackCount == value) return;
        _trackCount = value;
        OnPropertyChanged(nameof(TrackCount));
        OnPropertyChanged(nameof(TrackCountText));
    }

    /// <summary>歌单封面可能随曲目变化(如"我喜欢的音乐"自动生成封面,加歌后 coverImgUrl 会变):
    /// URL 变了才通知视图重载；旧 Image 会取消请求并释放位图租约。</summary>
    public void RefreshCover(string coverUrl)
    {
        if (string.IsNullOrEmpty(coverUrl) || coverUrl == _currentCoverUrl) return;
        _currentCoverUrl = coverUrl;
        OnPropertyChanged(nameof(CoverUrl));
    }
}
