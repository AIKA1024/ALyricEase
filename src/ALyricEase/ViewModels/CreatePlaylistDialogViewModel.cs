using System;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>创建歌单对话框 VM:侧栏"网易云/QQ音乐"分组头"+"按钮打开,输入歌单名
/// (可选勾选隐私歌单,仅网易云生效)确认后调用对应音源的创建接口;成功经回调交给
/// 宿主(MainViewModel)刷新侧栏并打开新歌单,失败在框内呈现服务端文案不关框。</summary>
public sealed partial class CreatePlaylistDialogViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;
    private readonly Action<Playlist> _onConfirm;
    private readonly Action<Models.LocalPlaylist>? _onLocalConfirm;

    /// <summary>当前目标音源(Refresh 设置;决定路由与文案)。本地模式忽略。</summary>
    private MusicSource _source = MusicSource.NetEase;

    /// <summary>本地音乐歌单创建模式(RefreshLocal 打开;确认走本地实体创建而非平台 API)。</summary>
    private bool _isLocal;

    public CreatePlaylistDialogViewModel(PlaylistViewModel playlist, Action<Playlist> onConfirm,
        Action<Models.LocalPlaylist>? onLocalConfirm = null)
    {
        _playlist = playlist;
        _onConfirm = onConfirm;
        _onLocalConfirm = onLocalConfirm;
    }

    /// <summary>歌单名。</summary>
    [ObservableProperty] private string _name = "";

    /// <summary>隐私歌单(仅自己可见;QQ 音乐该接口无此参数,勾选不生效)。</summary>
    [ObservableProperty] private bool _isPrivate;

    /// <summary>创建请求进行中(防重复提交)。</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>服务端/网络错误文案(非空时框内红字显示,不关框)。</summary>
    [ObservableProperty] private string? _message;

    /// <summary>当前目标是否 QQ 音乐(驱动音源相关文案显隐)。</summary>
    [ObservableProperty] private bool _isQq;

    public bool IsNetEase => !IsQq;

    /// <summary>隐私歌单勾选框可见性:本地模式与 QQ 音乐都不展示(本地无平台可见性概念)。</summary>
    public bool ShowIsPrivate => !_isLocal && !IsQq;

    /// <summary>音源标题(标题内插,如"创建网易云歌单"/"创建本地音乐歌单")。</summary>
    public string SourceLabel => _isLocal ? "本地音乐" : IsQq ? "QQ音乐" : "网易云";

    /// <summary>名字非空且不在提交中才可确认。</summary>
    public bool CanConfirm => !IsBusy && Name.Trim().Length > 0;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConfirm));

    partial void OnIsQqChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNetEase));
        OnPropertyChanged(nameof(ShowIsPrivate));
    }

    /// <summary>打开对话框时调用:按目标音源重置输入。</summary>
    public void Refresh(MusicSource source)
    {
        _source = source;
        _isLocal = false;
        IsQq = source == MusicSource.QQ;
        Name = "";
        IsPrivate = false;
        Message = null;
        IsBusy = false;
        OnPropertyChanged(nameof(SourceLabel));
    }

    /// <summary>打开对话框时调用(本地音乐歌单创建模式):无平台 API 参与,确认即建实体。</summary>
    public void RefreshLocal()
    {
        _isLocal = true;
        IsQq = false;
        Name = "";
        IsPrivate = false;
        Message = null;
        IsBusy = false;
        OnPropertyChanged(nameof(SourceLabel));
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var name = Name.Trim();
        if (name.Length == 0 || IsBusy) return;
        if (_isLocal)
        {
            // 本地音乐歌单:无 API 调用,实体创建即完成
            var local = _playlist.CreateLocalPlaylist(name);
            _onLocalConfirm?.Invoke(local);
            return;
        }
        IsBusy = true;
        Message = null;
        try
        {
            var created = await _playlist.CreatePlaylistAsync(_source, name, IsPrivate);
            _onConfirm(created);
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
