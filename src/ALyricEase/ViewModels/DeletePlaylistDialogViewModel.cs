using System;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>删除歌单确认对话框 VM:侧栏歌单子项右键打开,确认后按音源调用删除接口;
/// 成功经回调交给宿主(MainViewModel)关弹窗并刷新侧栏;失败在框内呈现服务端文案不关框。
/// 收藏的非本人歌单服务端会拒绝,文案原样呈现。</summary>
public sealed partial class DeletePlaylistDialogViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;
    private readonly Action<PlaylistItemViewModel> _onConfirm;

    private PlaylistItemViewModel? _target;

    public DeletePlaylistDialogViewModel(PlaylistViewModel playlist, Action<PlaylistItemViewModel> onConfirm)
    {
        _playlist = playlist;
        _onConfirm = onConfirm;
    }

    /// <summary>确认文案(含歌单名与不可恢复警示)。</summary>
    public string ConfirmText => _target is null
        ? ""
        : $"确定删除歌单「{_target.Name}」吗?删除后无法恢复。";

    /// <summary>音源标题(标题内插,如"删除QQ音乐歌单")。</summary>
    public string SourceLabel => _target?.Playlist.Source == MusicSource.QQ ? "QQ音乐" : "网易云";

    /// <summary>提交进行中(防重复提交)。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>服务端/网络错误文案(非空时框内红字显示,不关框)。</summary>
    [ObservableProperty] private string? _message;

    public bool CanConfirm => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));

    /// <summary>打开对话框时调用:按目标歌单准备确认文案。</summary>
    public void Refresh(PlaylistItemViewModel target)
    {
        _target = target;
        IsBusy = false;
        Message = null;
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(ConfirmText));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_target is null || IsBusy) return;
        IsBusy = true;
        Message = null;
        try
        {
            await _playlist.DeletePlaylistAsync(_target);
            _onConfirm(_target);
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
}
