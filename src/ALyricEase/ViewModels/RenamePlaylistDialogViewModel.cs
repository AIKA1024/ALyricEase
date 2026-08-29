using System;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>重命名歌单对话框 VM:侧栏歌单子项右键打开,预填当前名,确认后按音源调用重命名接口;
/// 成功经回调交给宿主(MainViewModel)关弹窗并刷新侧栏,失败在框内呈现服务端文案不关框。
/// 名字未变时直接关框不打接口。</summary>
public sealed partial class RenamePlaylistDialogViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;
    private readonly Action<PlaylistItemViewModel> _onConfirm;

    private PlaylistItemViewModel? _target;

    public RenamePlaylistDialogViewModel(PlaylistViewModel playlist, Action<PlaylistItemViewModel> onConfirm)
    {
        _playlist = playlist;
        _onConfirm = onConfirm;
    }

    /// <summary>新名字(打开时预填当前名)。</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>提交进行中(防重复提交)。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>服务端/网络错误文案(非空时框内红字显示,不关框)。</summary>
    [ObservableProperty] private string? _message;

    /// <summary>音源标题(标题内插,如"重命名QQ音乐歌单")。</summary>
    public string SourceLabel => _target?.Playlist.Source == MusicSource.QQ ? "QQ音乐" : "网易云";

    /// <summary>名字非空且不在提交中才可确认。</summary>
    public bool CanConfirm => !IsBusy && Name.Trim().Length > 0;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));

    /// <summary>打开对话框时调用:按目标歌单预填当前名。</summary>
    public void Refresh(PlaylistItemViewModel target)
    {
        _target = target;
        Name = target.Name;
        Message = null;
        IsBusy = false;
        OnPropertyChanged(nameof(SourceLabel));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (_target is null) return;
        var name = Name.Trim();
        if (name.Length == 0 || IsBusy) return;
        if (name == _target.Name)
        {
            _onConfirm(_target); // 名字没变:直接关框,不打接口
            return;
        }
        IsBusy = true;
        Message = null;
        try
        {
            await _playlist.RenamePlaylistAsync(_target, name);
            _onConfirm(_target);
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
}
