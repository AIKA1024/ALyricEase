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
    private CancellationTokenSource? _qualityCancellation;
    private int _preferredQuality;
    internal Task QualityLoadTask { get; private set; } = Task.CompletedTask;
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
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyPropertyChangedFor(nameof(CanChooseQuality))]
    private bool _isLoadingQualities;
    [ObservableProperty] private bool _canRetryQualities;
    public bool CanChooseQuality => !IsBusy && !IsComplete && !IsLoadingQualities;
    public string CloseText => IsComplete ? "完成" : "取消";

    public void Refresh(Song song, int preferredQuality)
    {
        _qualityCancellation?.Cancel();
        _song = song;
        _preferredQuality = preferredQuality;
        SongText = $"{song.Name} · {song.Artist}";
        IsComplete = false;
        IsBusy = false;
        HasError = false;
        Progress = 0;
        QualityLoadTask = LoadQualitiesAsync();
    }

    [RelayCommand]
    private Task RetryQualitiesAsync() => QualityLoadTask = LoadQualitiesAsync();

    private async Task LoadQualitiesAsync()
    {
        if (_song is null || IsBusy) return;
        _qualityCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _qualityCancellation = cancellation;
        var song = _song;
        IsLoadingQualities = true;
        CanRetryQualities = false;
        HasError = false;
        SelectedQuality = null;
        Qualities = [];
        Status = "正在查询歌曲音质…";
        try
        {
            var options = await service.GetAvailableQualitiesAsync(song, cancellation.Token);
            if (!ReferenceEquals(_qualityCancellation, cancellation)) return;
            cancellation.Token.ThrowIfCancellationRequested();
            if (options is null)
                throw new InvalidOperationException("平台暂未返回这首歌曲的音质信息，请重试。");
            Qualities = options;
            var preferredRank = AudioQualityMapper.NormalizeIndex(_preferredQuality);
            // 首选设置中的档位，否则选不高于偏好的最高档，最后才选最低可用档。
            var allowed = Enumerable.Range(0, preferredRank + 1)
                .Select(i => AudioQualityMapper.GetRequestLevel(song.Source, i)).ToHashSet();
            SelectedQuality = options.LastOrDefault(q => allowed.Contains(q.Level)) ?? options.FirstOrDefault();
            Status = options.Count == 0 ? "这首歌曲暂无可下载的标准、HQ、SQ 或 Hi-Res 音质。"
                : "将保存到系统“下载”文件夹；已有相同音质缓存时直接保存。";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_qualityCancellation, cancellation)) return;
            Status = ex is OperationCanceledException ? "查询音质超时，请重试。" : $"查询音质失败：{ex.Message}";
            HasError = true;
            CanRetryQualities = true;
        }
        finally
        {
            if (ReferenceEquals(_qualityCancellation, cancellation))
            {
                _qualityCancellation = null;
                IsLoadingQualities = false;
            }
        }
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
        else
        {
            var loading = _qualityCancellation;
            _qualityCancellation = null;
            loading?.Cancel();
            IsLoadingQualities = false;
            close();
        }
    }
}
