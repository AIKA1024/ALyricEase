namespace ALyricEase.Services.Audio;

/// <summary>音频播放器抽象,便于替换后端。所有事件回调保证已在 UI 线程。</summary>
public interface IAudioPlayer : IDisposable
{
    PlaybackState State { get; }

    /// <summary>读当前进度;写 = Seek(毫秒)。</summary>
    long PositionMs { get; set; }

    /// <summary>总时长,毫秒;-1 表示未知。</summary>
    long DurationMs { get; }

    /// <summary>音量 0..100。</summary>
    int Volume { get; set; }

    event EventHandler? StateChanged;

    event EventHandler<long>? PositionChanged;

    event EventHandler<long>? DurationChanged;

    event EventHandler<string>? ErrorOccurred;

    void PlayUrl(string url);

    void Pause();

    void Resume();

    void Stop();
}
