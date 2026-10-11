using ALyricEase.Models;
using ALyricEase.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

public sealed partial class DownloadSongDialogViewModel(
    SongDownloadService service, Action close) : ViewModelBase
{
    private Song? _song;
    private CancellationTokenSource? _cancellation;
    [ObservableProperty] private string _songText = "";
    [ObservableProperty] private IReadOnlyList<SongDownloadQuality> _qualities = [];
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private SongDownloadQuality? _selectedQuality;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyPropertyChangedFor(nameof(CanChooseQuality))]
    private bool _isBusy;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyPropertyChangedFor(nameof(CanChooseQuality))]
    [NotifyPropertyChangedFor(nameof(CloseText))]
    private bool _isComplete;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate = true;
    public bool CanChooseQuality => !IsBusy && !IsComplete;
    public string CloseText => IsComplete ? "完成" : "取消";

    public void Refresh(Song song, int preferredQuality)
    {
        _song = song;
        SongText = $"{song.Name} · {song.Artist}";
        Qualities = SongDownloadService.GetQualities(song.Source);
        var level = AudioQualityMapper.GetRequestLevel(song.Source, preferredQuality);
        SelectedQuality = Qualities.FirstOrDefault(option => option.Level == level) ?? Qualities.FirstOrDefault();
        IsComplete = false;
        IsBusy = false;
        HasError = false;
        Progress = 0;
        Status = "将保存到系统“下载”文件夹；已有相同音质缓存时直接保存。";
    }

    private bool CanDownload() => CanChooseQuality && SelectedQuality is not null;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (_song is null || SelectedQuality is null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        HasError = false;
        Progress = 0;
        IsIndeterminate = true;
        Status = "正在准备下载…";
        var progress = new Progress<SongDownloadProgress>(value =>
        {
            if (!ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested) return;
            IsIndeterminate = value.TotalBytes is not > 0;
            Progress = value.TotalBytes is > 0 ? 100d * value.Bytes / value.TotalBytes.Value : 0;
            var action = value.FromCache ? "正在保存缓存" : "正在下载";
            Status = IsIndeterminate ? $"{action} · {value.Bytes / 1048576d:F1} MB"
                : $"{action} · {Progress:F0}%";
        });
        try
        {
            var result = await service.DownloadAsync(_song, SelectedQuality, progress, cancellation.Token);
            IsComplete = true;
            Status = $"{(result.FromCache ? "已从缓存保存" : "下载完成")}\n{result.Location}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            close();
        }
        catch (OperationCanceledException)
        {
            Status = "下载已取消或连接超时，请重试。";
            HasError = true;
        }
        catch (Exception ex)
        {
            Status = $"下载失败：{ex.Message}";
            HasError = true;
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (_cancellation is { } cancellation)
        {
            Status = "正在取消…";
            cancellation.Cancel();
        }
        else close();
    }
}
