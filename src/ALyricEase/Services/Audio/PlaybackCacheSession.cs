namespace ALyricEase.Services.Audio;

/// <summary>
/// 单次播放的音频缓存任务。由 UI 线程随播放器事件更新；用单调时钟累计 Playing 时间，
/// 不依赖歌曲进度，暂停/加载不计时，Seek 不会跳过门槛。下载与清理在后台执行。
/// </summary>
internal sealed class PlaybackCacheSession(TimeProvider? timeProvider = null) : IDisposable
{
    internal static readonly TimeSpan MinimumPlaybackTime = TimeSpan.FromSeconds(20);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private Func<CancellationToken, Task>? _download;
    private CancellationTokenSource? _cancellation;
    private TimeSpan _played;
    private long _lastTimestamp;
    private bool _playing;

    internal Task? DownloadTask { get; private set; }

    public void Begin(Func<CancellationToken, Task> download, bool isPlaying)
    {
        Cancel();
        _download = download;
        _cancellation = new CancellationTokenSource();
        _playing = isPlaying;
        _lastTimestamp = _time.GetTimestamp();
    }

    public void UpdatePlaying(bool isPlaying)
    {
        AccumulatePlayingTime();
        _playing = isPlaying;
        TryStartDownload();
    }

    public void Tick()
    {
        AccumulatePlayingTime();
        TryStartDownload();
    }

    private void AccumulatePlayingTime()
    {
        var now = _time.GetTimestamp();
        if (_playing && _download is not null)
            _played += _time.GetElapsedTime(_lastTimestamp, now);
        _lastTimestamp = now;
    }

    private void TryStartDownload()
    {
        if (!_playing || _played < MinimumPlaybackTime || _download is not { } download) return;
        var token = _cancellation!.Token;
        _download = null; // 每次播放只触发一次；后续进度事件不重复下载。
        DownloadTask = Task.Run(async () =>
        {
            try
            {
                if (!token.IsCancellationRequested)
                    await download(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { /* 后台缓存失败不能影响当前播放。 */ }
        });
    }

    public void Cancel()
    {
        var cancellation = _cancellation;
        var task = DownloadTask;
        _cancellation = null;
        _download = null;
        DownloadTask = null;
        _playing = false;
        _played = TimeSpan.Zero;
        if (cancellation is null) return;
        cancellation.Cancel();
        // 在下载清理完成后再释放 CTS，避免和网络流的取消注册竞争。
        if (task is null) cancellation.Dispose();
        else _ = task.ContinueWith(
            _ => cancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose() => Cancel();
}
