using System;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>重命名歌单对话框 VM:侧栏歌单子项右键打开,预填当前名。目标可为平台歌单
/// (确认后按音源调用重命名接口)或聚合歌单(本地实体,经委托换实例持久化+重建侧栏);
/// 成功经回调交给宿主(MainViewModel)关弹窗,失败在框内呈现文案不关框。名字未变时直接关框。</summary>
public sealed partial class RenamePlaylistDialogViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;
    private readonly Action<PlaylistItemViewModel> _onPlaylistRenamed;
    private readonly Func<AggregatePlaylist, string, Task> _renameAggregate;
    private readonly Action<AggregatePlaylist> _onAggregateRenamed;

    private PlaylistItemViewModel? _targetPlaylist;
    private AggregatePlaylist? _targetAggregate;

    public RenamePlaylistDialogViewModel(PlaylistViewModel playlist,
        Action<PlaylistItemViewModel> onPlaylistRenamed,
        Func<AggregatePlaylist, string, Task> renameAggregate,
        Action<AggregatePlaylist> onAggregateRenamed)
    {
        _playlist = playlist;
        _onPlaylistRenamed = onPlaylistRenamed;
        _renameAggregate = renameAggregate;
        _onAggregateRenamed = onAggregateRenamed;
    }

    /// <summary>新名字(打开时预填当前名)。</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>提交进行中(防重复提交)。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>服务端/网络错误文案(非空时框内红字显示,不关框)。</summary>
    [ObservableProperty] private string? _message;

    /// <summary>音源标题(标题内插:"重命名QQ音乐歌单"/"重命名聚合歌单")。</summary>
    public string SourceLabel => _targetAggregate is not null ? "聚合"
        : _targetPlaylist?.Playlist.Source == MusicSource.QQ ? "QQ音乐" : "网易云";

    /// <summary>名字非空且不在提交中才可确认。</summary>
    public bool CanConfirm => !IsBusy && Name.Trim().Length > 0;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));

    /// <summary>打开对话框时调用:按目标歌单预填当前名。</summary>
    public void Refresh(PlaylistItemViewModel target)
    {
        _targetPlaylist = target;
        _targetAggregate = null;
        Name = target.Name;
        Message = null;
        IsBusy = false;
        OnPropertyChanged(nameof(SourceLabel));
    }

    /// <summary>打开对话框时调用:聚合歌单目标。</summary>
    public void Refresh(AggregatePlaylist target)
    {
        _targetAggregate = target;
        _targetPlaylist = null;
        Name = target.Name;
        Message = null;
        IsBusy = false;
        OnPropertyChanged(nameof(SourceLabel));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var name = Name.Trim();
        if (name.Length == 0 || IsBusy) return;
        if (_targetPlaylist is { } playlist)
        {
            if (name == playlist.Name)
            {
                _onPlaylistRenamed(playlist); // 名字没变:直接关框,不打接口
                return;
            }
            IsBusy = true;
            Message = null;
            try
            {
                await _playlist.RenamePlaylistAsync(playlist, name);
                _onPlaylistRenamed(playlist);
            }
            catch (ApiException ex)
            {
                Message = ex.Message; // 留在框内让用户改名/取消
            }
            finally
            {
                IsBusy = false;
            }
        }
        else if (_targetAggregate is { } aggregate)
        {
            if (name == aggregate.Name)
            {
                _onAggregateRenamed(aggregate);
                return;
            }
            IsBusy = true;
            Message = null;
            try
            {
                await _renameAggregate(aggregate, name);
                _onAggregateRenamed(aggregate);
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
