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

    public Playlist Playlist { get; private set; }

    public long Id => Playlist.Id;

    public string Name => Playlist.Name;

    public string CoverUrl => _currentCoverUrl;

    public string Description => Playlist.Description;

    public string SourceLabel => Playlist.Source == Services.MusicSource.QQ
        ? "QQ 音乐"
        : "网易云音乐";

    /// <summary>音源角标("全部平台"筛选下的列表行封面左下角显示):与个性推荐歌曲行共用同一份位图。</summary>
    public Avalonia.Media.IImage? SourceBadge => Infrastructure.SourceBadges.For(Playlist.Source);

    /// <summary>有简介才显示(API 多数歌单无简介)。</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    private int _trackCount;

    public int TrackCount => _trackCount;

    public string TrackCountText => $"{TrackCount} 首";

    /// <summary>累计播放次数格式化文本(用户页角标用;0 显示 "0")。
    /// QQ 列表接口不下发(恒 0),用户页后台补拉后经 <see cref="UpdatePlayCount"/> 原地更新。</summary>
    private double _playCount;
    internal double CurrentPlayCount => _playCount;

    public string PlayCountText => Services.NetEase.NetEaseApiClient.FormatPlayCount(_playCount);

    /// <summary>播放量后台补拉落地(与 <see cref="UpdateTrackCount"/> 同款:不替换 VM 实例)。</summary>
    public void UpdatePlayCount(double value)
    {
        if (_playCount == value) return;
        _playCount = value;
        OnPropertyChanged(nameof(PlayCountText));
    }

    /// <summary>歌单创建者(用户页 more 菜单"创建者"项/跳用户页用;非用户页入口为 null)。</summary>
    private string? _creatorName;
    public string? CreatorName { get => _creatorName; init => _creatorName = value; }

    private long _creatorId;
    public long CreatorId { get => _creatorId; init => _creatorId = value; }

    /// <summary>资料库联网同步时保留 VM 与图片控件，只通知实际变化的展示字段。</summary>
    internal void UpdateFrom(PlaylistItemViewModel fresh)
    {
        var old = Playlist;
        Playlist = fresh.Playlist;
        if (old.Name != Name) OnPropertyChanged(nameof(Name));
        if (old.Description != Description)
        {
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(HasDescription));
        }
        if (_currentCoverUrl != fresh.CoverUrl)
        {
            _currentCoverUrl = fresh.CoverUrl;
            OnPropertyChanged(nameof(CoverUrl));
        }
        UpdateTrackCount(fresh.TrackCount);
        UpdatePlayCount(fresh._playCount);
        if (_creatorName != fresh.CreatorName)
        {
            _creatorName = fresh.CreatorName;
            OnPropertyChanged(nameof(CreatorName));
        }
        if (_creatorId != fresh.CreatorId)
        {
            _creatorId = fresh.CreatorId;
            OnPropertyChanged(nameof(CreatorId));
        }
        if (old.Name != Playlist.Name || old.Description != Playlist.Description
            || old.CoverUrl != Playlist.CoverUrl || old.TrackCount != Playlist.TrackCount
            || old.PlayCount != Playlist.PlayCount || old.CanAddTracks != Playlist.CanAddTracks
            || old.CreatorName != Playlist.CreatorName)
            OnPropertyChanged(nameof(Playlist));
    }

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
