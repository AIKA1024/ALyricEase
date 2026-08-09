using ALyricEase.Infrastructure;
using LibVLCSharp.Shared;

namespace ALyricEase.Services.Audio;

/// <summary>LibVLCSharp 实现。进程内单例:LibVLC 全局共享,单个 MediaPlayer。
/// 事件回调在播放器线程,统一经 DispatcherService 转 UI 线程。</summary>
public sealed class LibVlcAudioPlayer : IAudioPlayer
{
    private static readonly Lazy<LibVLC> SharedLibVlc = new(() =>
        new LibVLC(
            "--no-video",
            "--quiet",
            "--no-osd",
            "--http-referrer=https://music.163.com",
            "--http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/125.0.0.0 Safari/537.36"));

    private readonly MediaPlayer _mp;
    private readonly DispatcherService _dispatcher;
    private Media? _media;

    public LibVlcAudioPlayer(DispatcherService dispatcher)
    {
        _dispatcher = dispatcher;
        _mp = new MediaPlayer(SharedLibVlc.Value);

        _mp.TimeChanged += (_, e) => _dispatcher.Post(() => PositionChanged?.Invoke(this, e.Time));
        _mp.LengthChanged += (_, e) => _dispatcher.Post(() => DurationChanged?.Invoke(this, e.Length));
        _mp.EndReached += (_, _) => _dispatcher.Post(() => RaiseState(PlaybackState.Idle));
        _mp.Playing += (_, _) => _dispatcher.Post(() => RaiseState(PlaybackState.Playing));
        _mp.Paused += (_, _) => _dispatcher.Post(() => RaiseState(PlaybackState.Paused));
        _mp.Stopped += (_, _) => _dispatcher.Post(() => RaiseState(PlaybackState.Idle));
        _mp.EncounteredError += (_, _) => _dispatcher.Post(() => ErrorOccurred?.Invoke(this, "播放出错"));
    }

    public PlaybackState State { get; private set; } = PlaybackState.Idle;

    public long PositionMs
    {
        get => _mp.Time;
        set { try { _mp.Time = value; } catch { /* 未就绪时忽略 */ } }
    }

    public long DurationMs => _mp.Length;

    public int Volume
    {
        get => _mp.Volume;
        set { try { _mp.Volume = Math.Clamp(value, 0, 100); } catch { /* 未就绪时忽略 */ } }
    }

    public event EventHandler? StateChanged;

    public event EventHandler<long>? PositionChanged;

    public event EventHandler<long>? DurationChanged;

    public event EventHandler<string>? ErrorOccurred;

    public void PlayUrl(string url)
    {
        Stop();
        _media?.Dispose();
        _media = new Media(SharedLibVlc.Value, url, FromType.FromLocation);
        _mp.Play(_media);
    }

    public void Pause() => _mp.Pause();

    public void Resume() => _mp.Play();

    public void Stop()
    {
        try { _mp.Stop(); }
        catch { /* 忽略 */ }
    }

    public void Dispose()
    {
        _media?.Dispose();
        _mp.Dispose();
    }

    private void RaiseState(PlaybackState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
