using System;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.Audio;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.Smtc;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>播放器 VM:状态机(Idle→Loading→Playing/Paused)、进度/音量、
/// 播放/暂停控制。事件均已在 UI 线程(见 IAudioPlayer 契约),可直接更新可观察属性。</summary>
public sealed partial class PlayerViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioPlayer _player;
    private readonly NetEaseApiClient _api;
    private readonly LyricViewModel _lyric;
    private readonly SmtcService _smtc;

    private bool _scrubbing;

    public PlayerViewModel(IAudioPlayer player, NetEaseApiClient api, LyricViewModel lyric, SmtcService smtc)
    {
        _player = player;
        _api = api;
        _lyric = lyric;
        _smtc = smtc;
        _smtc.PlayPauseRequested += OnSmtcPlayPause;
        _smtc.SeekRequested += OnSmtcSeek;
        _player.Volume = 80;
        _player.StateChanged += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.DurationChanged += OnDurationChanged;
        _player.ErrorOccurred += OnError;
        Volume = _player.Volume;
    }

    [ObservableProperty] private Song? _currentSong;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private IImage? _cover;
    [ObservableProperty] private long _positionMs;
    [ObservableProperty] private long _durationMs;
    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _positionText = "00:00";
    [ObservableProperty] private string _durationText = "00:00";

    partial void OnVolumeChanged(int value) => _player.Volume = value;

    partial void OnPositionMsChanged(long value) => PositionText = FormatTime(value);

    partial void OnDurationMsChanged(long value)
    {
        DurationText = FormatTime(value);
        OnPropertyChanged(nameof(ProgressMaximum));
        OnPropertyChanged(nameof(HasProgress));
    }

    /// <summary>进度条 Maximum:空闲时 DurationMs=0 → 至少 1,避免 Min==Max 造成 NaN 布局循环。</summary>
    public double ProgressMaximum => DurationMs > 0 ? DurationMs : 1;

    /// <summary>是否有可显示的时长(无则进度条禁用)。</summary>
    public bool HasProgress => DurationMs > 0;

    /// <summary>播放/暂停/加载图标互切(View 里三个 PathIcon)。</summary>
    public bool ShowPauseIcon => IsPlaying;
    public bool ShowLoadingIcon => IsLoading;
    public bool ShowPlayIcon => !IsLoading && !IsPlaying;

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPauseIcon));
        OnPropertyChanged(nameof(ShowPlayIcon));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLoadingIcon));
        OnPropertyChanged(nameof(ShowPlayIcon));
    }

    /// <summary>一首歌开始加载/播放(UI 线程),供主窗口导航切到正在播放页。</summary>
    public event Action? SongStarted;

    /// <summary>播放一首歌:查播放地址(higher→standard 自动降级),null 提示 VIP/不可播。</summary>
    [RelayCommand]
    public async Task PlayAsync(Song? song)
    {
        if (song is null) return;

        CurrentSong = song;
        Title = song.Name;
        Artist = song.Artist;
        _smtc.SetNowPlaying(song.Name, song.Artist, song.Album, song.CoverUrl);
        SongStarted?.Invoke();
        PositionMs = 0;
        DurationMs = 0;
        Message = null;
        IsLoading = true;
        IsPlaying = false;
        _ = LoadCoverAsync(song.CoverUrl);
        _ = _lyric.LoadAsync(song.Id); // 并发加载歌词,失败不阻塞播放

        try
        {
            var item = await _api.GetPlayUrlAsync(song.Id, "higher");
            if (item is null || string.IsNullOrEmpty(item.Url))
            {
                Message = "该歌曲需会员或不可播放(海外 IP 可能受限)";
                return;
            }

            if (item.IsTrial == true)
                Message = "VIP 歌曲仅试听 30 秒";
            _player.PlayUrl(item.Url);
        }
        catch (ApiException ex)
        {
            Message = $"播放失败:{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        switch (_player.State)
        {
            case PlaybackState.Playing:
                _player.Pause();
                break;
            case PlaybackState.Paused:
                _player.Resume();
                break;
            case PlaybackState.Idle when CurrentSong is not null:
                _ = PlayAsync(CurrentSong); // 播完/停止后重按从头播
                break;
        }
    }

    private void OnSmtcPlayPause() => TogglePlayPauseCommand.Execute(null);

    private void OnSmtcSeek(long ms)
    {
        if (_player.State is not (PlaybackState.Playing or PlaybackState.Paused)) return;
        _player.PositionMs = ms;
    }

    /// <summary>进度条拖动开始:暂停接收播放器位置更新。</summary>
    public void BeginScrub() => _scrubbing = true;

    /// <summary>进度条松开:Seek 到当前位置,恢复接收更新。</summary>
    public void EndScrub()
    {
        _scrubbing = false;
        if (_player.State is PlaybackState.Playing or PlaybackState.Paused)
            _player.PositionMs = PositionMs;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        IsLoading = _player.State == PlaybackState.Loading;
        IsPlaying = _player.State == PlaybackState.Playing;
    }

    private void OnPositionChanged(object? sender, long value)
    {
        if (_scrubbing) return;
        PositionMs = value;
        _lyric.UpdatePosition(value); // 驱动歌词高亮(UI 线程)
    }

    private void OnDurationChanged(object? sender, long value) => DurationMs = value;

    private void OnError(object? sender, string message) => Message = message;

    private async Task LoadCoverAsync(string url) => Cover = await CoverLoader.LoadAsync(url, 640);

    private static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    public void Dispose()
    {
        _smtc.PlayPauseRequested -= OnSmtcPlayPause;
        _smtc.SeekRequested -= OnSmtcSeek;
        _player.StateChanged -= OnStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.DurationChanged -= OnDurationChanged;
        _player.ErrorOccurred -= OnError;
    }
}
