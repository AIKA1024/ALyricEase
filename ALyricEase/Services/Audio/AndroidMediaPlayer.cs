#if ANDROID
using System;
using System.Threading;
using ALyricEase.Infrastructure;

namespace ALyricEase.Services.Audio;

/// <summary>Android 原生音频播放(Android.Media.MediaPlayer)。
/// #if ANDROID 门控:本轮未加 net10.0-android TFM,此文件不参与编译。
/// 计划在未来加 Android 工作负载/TFM 后启用 —— 到那一步前【未验证】。</summary>
public sealed class AndroidMediaPlayer : IAudioPlayer
{
    private const int PollIntervalMs = 200;

    private readonly DispatcherService _dispatcher;
    private readonly Android.Media.MediaPlayer _mp;
    private Timer? _timer;
    private PlaybackState _state = PlaybackState.Idle;
    private bool _prepared;
    private long _lastPosMs = -1;
    private long _lastDurMs;

    public AndroidMediaPlayer(DispatcherService dispatcher)
    {
        _dispatcher = dispatcher;
        _mp = new Android.Media.MediaPlayer();
        _mp.Prepared += (_, _) => _dispatcher.Post(() => { _prepared = true; _mp.Start(); });
        _mp.Completion += (_, _) => _dispatcher.Post(() => RaiseState(PlaybackState.Idle));
        _mp.Error += (_, _) => _dispatcher.Post(() =>
        {
            ErrorOccurred?.Invoke(this, "播放出错");
            RaiseState(PlaybackState.Idle);
        });
    }

    public PlaybackState State => _state;

    public long PositionMs
    {
        get { try { return _prepared ? _mp.CurrentPosition : 0; } catch { return 0; } }
        set { try { if (_prepared) _mp.SeekTo((int)Math.Max(0, value)); } catch { } }
    }

    public long DurationMs
    {
        get { try { return _prepared ? _mp.Duration : 0; } catch { return 0; } }
    }

    private int _volume;

    public int Volume
    {
        // Android MediaPlayer 无 getVolume(),用私有字段回读(避免编译不过)
        get => _volume;
        set { _volume = value; var v = Math.Clamp(value, 0, 100) / 100f; _mp.SetVolume(v, v); }
    }

    public event EventHandler? StateChanged;

    public event EventHandler<long>? PositionChanged;

    public event EventHandler<long>? DurationChanged;

    public event EventHandler<string>? ErrorOccurred;

    public void PlayUrl(string url)
    {
        Stop();
        try
        {
            _prepared = false;
            _lastPosMs = -1;
            _lastDurMs = 0;
            _mp.Reset();
            _mp.SetDataSource(url); // http(s) URL;未来如需 Referer 头改用 SetDataSource(url, headers)
            StartPolling();
            _mp.PrepareAsync();
        }
        catch (Exception)
        {
            _dispatcher.Post(() => ErrorOccurred?.Invoke(this, "播放出错"));
        }
    }

    public void Pause() { try { _mp.Pause(); } catch { } }

    public void Resume() { try { _mp.Start(); } catch { } }

    public void Stop()
    {
        try { _mp.Stop(); } catch { }
        try { _mp.Reset(); } catch { }
        _prepared = false;
        StopPolling();
        RaiseState(PlaybackState.Idle);
    }

    public void Dispose()
    {
        StopPolling();
        try { _mp.Release(); } catch { }
    }

    private void StartPolling() => _timer ??= new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);

    private void StopPolling()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Poll()
    {
        try
        {
            if (!_prepared) return;
            var pos = PositionMs;
            var dur = DurationMs;
            if (pos != _lastPosMs) { _lastPosMs = pos; var p = pos; _dispatcher.Post(() => PositionChanged?.Invoke(this, p)); }
            if (dur != _lastDurMs) { _lastDurMs = dur; var d = dur; _dispatcher.Post(() => DurationChanged?.Invoke(this, d)); }
            if (_mp.IsPlaying && _state != PlaybackState.Playing)
                _dispatcher.Post(() => RaiseState(PlaybackState.Playing));
        }
        catch { }
    }

    private void RaiseState(PlaybackState s)
    {
        if (s == _state) return;
        _state = s;
        if (s == PlaybackState.Idle) StopPolling();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
#endif
