using ALyricEase.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>综合搜索合并歌曲的“我喜欢”平台选择对话框。</summary>
public sealed partial class LikeSourceDialogViewModel : ViewModelBase
{
    private readonly Action _close;
    private SongItemViewModel? _song;

    public LikeSourceDialogViewModel(Action close) => _close = close;

    [ObservableProperty]
    private MusicSource _selectedSource = MusicSource.NetEase;

    [ObservableProperty]
    private bool _isWorking;

    public string SongText => _song is null
        ? ""
        : string.IsNullOrWhiteSpace(_song.Artist)
            ? _song.Name
            : $"{_song.Name} · {_song.Artist}";

    public bool HasNetEase => _song?.HasLikeRecording(MusicSource.NetEase) == true;

    public bool HasQq => _song?.HasLikeRecording(MusicSource.QQ) == true;

    public bool IsNetEaseSelected
    {
        get => SelectedSource == MusicSource.NetEase;
        set { if (value) SelectedSource = MusicSource.NetEase; }
    }

    public bool IsQqSelected
    {
        get => SelectedSource == MusicSource.QQ;
        set { if (value) SelectedSource = MusicSource.QQ; }
    }

    public string NetEaseStateText => SourceStateText(MusicSource.NetEase);

    public string QqStateText => SourceStateText(MusicSource.QQ);

    public string NetEaseAutomationName => IsNetEaseSelected
        ? "网易云音乐，当前已选择"
        : "选择网易云音乐";

    public string QqAutomationName => IsQqSelected
        ? "QQ 音乐，当前已选择"
        : "选择 QQ 音乐";

    public string SelectionHint => _song?.IsLikedOn(SelectedSource) == true
        ? $"确认后将从{SongItemViewModel.SourceDisplayName(SelectedSource)}“我喜欢”中移除。"
        : $"确认后将收藏到{SongItemViewModel.SourceDisplayName(SelectedSource)}“我喜欢”。";

    public void Refresh(SongItemViewModel song)
    {
        _song = song;
        var preferred = song.PreferredCombinedLikeSource;
        SelectedSource = preferred is { } source && song.HasLikeRecording(source)
            ? source
            : song.Song.Source;
        if (!song.HasLikeRecording(SelectedSource))
            SelectedSource = song.HasLikeRecording(MusicSource.NetEase)
                ? MusicSource.NetEase
                : MusicSource.QQ;
        IsWorking = false;
        NotifyStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private Task ConfirmAsync() => ExecuteAsync(setAsDefault: false);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private Task ConfirmAndSetDefaultAsync() => ExecuteAsync(setAsDefault: true);

    [RelayCommand]
    private void SelectNetEase() => SelectedSource = MusicSource.NetEase;

    [RelayCommand]
    private void SelectQq() => SelectedSource = MusicSource.QQ;

    private bool CanConfirm()
        => !IsWorking && _song?.HasLikeRecording(SelectedSource) == true;

    private async Task ExecuteAsync(bool setAsDefault)
    {
        if (_song is not { } song || !CanConfirm()) return;
        IsWorking = true;
        NotifyCommands();
        try
        {
            if (setAsDefault)
                song.SetPreferredCombinedLikeSource(SelectedSource);
            await song.ToggleLikeOnSourceAsync(SelectedSource);
            _close();
        }
        finally
        {
            IsWorking = false;
            NotifyCommands();
        }
    }

    partial void OnSelectedSourceChanged(MusicSource value)
    {
        OnPropertyChanged(nameof(IsNetEaseSelected));
        OnPropertyChanged(nameof(IsQqSelected));
        OnPropertyChanged(nameof(NetEaseAutomationName));
        OnPropertyChanged(nameof(QqAutomationName));
        OnPropertyChanged(nameof(SelectionHint));
        NotifyCommands();
    }

    partial void OnIsWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectionHint));
        NotifyCommands();
    }

    private string SourceStateText(MusicSource source)
    {
        if (_song?.IsLikedOn(source) == true) return "已在“我喜欢”中";
        return _song?.CanToggleLikeOn(source) == true ? "已登录" : "确认后需要登录";
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(SongText));
        OnPropertyChanged(nameof(HasNetEase));
        OnPropertyChanged(nameof(HasQq));
        OnPropertyChanged(nameof(IsNetEaseSelected));
        OnPropertyChanged(nameof(IsQqSelected));
        OnPropertyChanged(nameof(NetEaseStateText));
        OnPropertyChanged(nameof(QqStateText));
        OnPropertyChanged(nameof(NetEaseAutomationName));
        OnPropertyChanged(nameof(QqAutomationName));
        OnPropertyChanged(nameof(SelectionHint));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        ConfirmCommand.NotifyCanExecuteChanged();
        ConfirmAndSetDefaultCommand.NotifyCanExecuteChanged();
    }
}
