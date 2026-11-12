using System;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>删除歌单确认对话框 VM:侧栏歌单子项右键打开。目标可为平台歌单(确认后按音源
/// 调用删除接口,真删服务端歌单)、聚合歌单(本地实体,仅移除聚合与成员引用,成员歌单不受影响)
/// 或本地音乐歌单(仅移除本地实体与引用,音频文件本体不删);
/// 成功经回调交给宿主(MainViewModel)关弹窗,失败在框内呈现文案不关框。
/// 收藏的非本人平台歌单服务端会拒绝,文案原样呈现。</summary>
public sealed partial class DeletePlaylistDialogViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;
    private readonly Action<PlaylistItemViewModel> _onPlaylistDeleted;
    private readonly Func<AggregatePlaylist, Task> _deleteAggregate;
    private readonly Action<AggregatePlaylist> _onAggregateDeleted;
    private readonly Func<LocalPlaylist, Task> _deleteLocalPlaylist;
    private readonly Action<LocalPlaylist> _onLocalPlaylistDeleted;

    private PlaylistItemViewModel? _targetPlaylist;
    private AggregatePlaylist? _targetAggregate;
    private LocalPlaylist? _targetLocalPlaylist;

    public DeletePlaylistDialogViewModel(PlaylistViewModel playlist,
        Action<PlaylistItemViewModel> onPlaylistDeleted,
        Func<AggregatePlaylist, Task> deleteAggregate,
        Action<AggregatePlaylist> onAggregateDeleted,
        Func<LocalPlaylist, Task> deleteLocalPlaylist,
        Action<LocalPlaylist> onLocalPlaylistDeleted)
    {
        _playlist = playlist;
        _onPlaylistDeleted = onPlaylistDeleted;
        _deleteAggregate = deleteAggregate;
        _onAggregateDeleted = onAggregateDeleted;
        _deleteLocalPlaylist = deleteLocalPlaylist;
        _onLocalPlaylistDeleted = onLocalPlaylistDeleted;
    }

    /// <summary>确认文案(按目标类型给不同警示:平台歌单不可恢复,聚合仅移除入口,
    /// 本地音乐歌单只删歌单不删磁盘上的音频文件)。</summary>
    public string ConfirmText => _targetLocalPlaylist is { } local
        ? $"确定删除本地音乐歌单「{local.Name}」吗?仅移除歌单与曲目引用,磁盘上的音频文件不会被删除。"
        : _targetAggregate is { } aggregate
            ? $"确定删除聚合歌单「{aggregate.Name}」吗?仅移除聚合,成员歌单不受影响。"
            : _targetPlaylist is { } playlist
                ? $"确定删除歌单「{playlist.Name}」吗?删除后无法恢复。"
                : "";

    /// <summary>底部提示(按目标类型区分删除后果)。</summary>
    public string FooterHint => _targetLocalPlaylist is not null
        ? "音频文件保持不变,引用该歌单的聚合成员会同步移除"
        : _targetAggregate is not null
            ? "成员歌单保持不变"
            : "该歌单将从账号中移除";

    /// <summary>音源标题(标题内插:"删除QQ音乐歌单"/"删除聚合歌单"/"删除本地音乐歌单")。</summary>
    public string SourceLabel => _targetLocalPlaylist is not null ? "本地音乐"
        : _targetAggregate is not null ? "聚合"
        : _targetPlaylist?.Playlist.Source == MusicSource.QQ ? "QQ音乐" : "网易云";

    /// <summary>提交进行中(防重复提交)。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>服务端/网络错误文案(非空时框内红字显示,不关框)。</summary>
    [ObservableProperty] private string? _message;

    public bool CanConfirm => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));

    /// <summary>打开对话框时调用:按目标歌单准备确认文案。</summary>
    public void Refresh(PlaylistItemViewModel target)
    {
        _targetPlaylist = target;
        _targetAggregate = null;
        _targetLocalPlaylist = null;
        IsBusy = false;
        Message = null;
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(FooterHint));
    }

    /// <summary>打开对话框时调用:聚合歌单目标。</summary>
    public void Refresh(AggregatePlaylist target)
    {
        _targetAggregate = target;
        _targetPlaylist = null;
        _targetLocalPlaylist = null;
        IsBusy = false;
        Message = null;
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(FooterHint));
    }

    /// <summary>打开对话框时调用:本地音乐歌单目标。</summary>
    public void Refresh(LocalPlaylist target)
    {
        _targetLocalPlaylist = target;
        _targetPlaylist = null;
        _targetAggregate = null;
        IsBusy = false;
        Message = null;
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(FooterHint));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy) return;
        if (_targetPlaylist is { } playlist)
        {
            IsBusy = true;
            Message = null;
            try
            {
                await _playlist.DeletePlaylistAsync(playlist);
                _onPlaylistDeleted(playlist);
            }
            catch (ApiException ex)
            {
                Message = ex.Message; // 留在框内(如操作频繁/非本人歌单)
            }
            finally
            {
                IsBusy = false;
            }
        }
        else if (_targetAggregate is { } aggregate)
        {
            IsBusy = true;
            Message = null;
            try
            {
                await _deleteAggregate(aggregate);
                _onAggregateDeleted(aggregate);
            }
            catch (Exception ex)
            {
                Message = ex.Message; // 本地持久化失败等
            }
            finally
            {
                IsBusy = false;
            }
        }
        else if (_targetLocalPlaylist is { } local)
        {
            IsBusy = true;
            Message = null;
            try
            {
                await _deleteLocalPlaylist(local);
                _onLocalPlaylistDeleted(local);
            }
            catch (Exception ex)
            {
                Message = ex.Message; // 本地持久化失败等
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
