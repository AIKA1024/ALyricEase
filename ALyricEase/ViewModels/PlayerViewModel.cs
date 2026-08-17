using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

/// <summary>播放模式:列表循环 / 单曲循环 / 随机播放 / 心动模式(网易云新模式,优先播红心喜欢的歌)。</summary>
public enum PlaybackMode
{
    ListLoop,
    SingleLoop,
    Shuffle,
    Heartbeat,
}

/// <summary>播放器 VM:状态机(Idle→Loading→Playing/Paused)、进度/音量、
/// 播放/暂停控制。事件均已在 UI 线程(见 IAudioPlayer 契约),可直接更新可观察属性。</summary>
public sealed partial class PlayerViewModel : ViewModelBase, IDisposable
{
    private readonly IAudioPlayer _player;
    private readonly NetEaseApiClient _api;
    private readonly LyricViewModel _lyric;
    private readonly ISmtcService _smtc;

    private bool _scrubbing;

    // 播放队列:下一曲/上一曲用。PlayAsync 播放时若来自列表(歌单/搜索)会带上来源,否则单曲。
    private List<Song> _queue = new();
    private int _queueIndex = -1;
    // 队列行 VM 缓存(与 _queue 同序对齐):详情页"接下来播放"列表重建时复用,封面不用重载。
    private List<QueueItemViewModel> _queueVms = new();

    private PlaybackState _prevState;
    private int _advancing; // 正在 PlayAsync:抑制 PlayUrl 内部 Stop() 的瞬时 Idle 误判为歌曲播完

    public PlayerViewModel(IAudioPlayer player, NetEaseApiClient api, LyricViewModel lyric, ISmtcService smtc)
    {
        _player = player;
        _api = api;
        _lyric = lyric;
        _smtc = smtc;
        _smtc.PlayPauseRequested += OnSmtcPlayPause;
        _smtc.SeekRequested += OnSmtcSeek;
        _smtc.NextRequested += OnSmtcNext;
        _smtc.PreviousRequested += OnSmtcPrevious;
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

    /// <summary>当前播放模式(点播放条模式按钮循环切换)。</summary>
    [ObservableProperty] private PlaybackMode _playbackMode = PlaybackMode.ListLoop;

    /// <summary>当前曲是否已红心(在"我喜欢的音乐"里)。</summary>
    [ObservableProperty] private bool _isCurrentLiked;

    /// <summary>播放来源名(歌单/每日推荐/专辑等,详情页"接下来播放"头部显示);单曲播放为 null。</summary>
    [ObservableProperty] private string? _queueSourceName;

    public bool HasQueueSource => !string.IsNullOrEmpty(QueueSourceName);

    partial void OnQueueSourceNameChanged(string? value) => OnPropertyChanged(nameof(HasQueueSource));

    /// <summary>详情页"接下来播放"列表:当前曲之后的队列(循环顺序,当前曲不重复出现)。
    /// 随 SetQueue/切歌/移除重建,行 VM 复用缓存。</summary>
    public ObservableCollection<QueueItemViewModel> UpcomingItems { get; } = new();

    /// <summary>曲名显示:未放歌时显示应用名占位(原版显示 "LyricEase")。</summary>
    public string DisplayTitle => CurrentSong is null ? "ALyricEase" : Title;

    /// <summary>副标题显示:未放歌时显示原版标语占位。</summary>
    public string DisplayArtist => CurrentSong is null ? "Dedicated to creators." : Artist;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

    partial void OnArtistChanged(string value) => OnPropertyChanged(nameof(DisplayArtist));

    /// <summary>模式按钮图标:三个循环模式用 Segoe MDL2(原版 Symbol.RepeatAll/RepeatOne/Shuffle),
    /// 心动模式用 Fluent 字体 E944(HeartPulse,与红心图标区分)。</summary>
    public string PlaybackModeGlyph => PlaybackMode switch
    {
        PlaybackMode.SingleLoop => "",
        PlaybackMode.Shuffle => "",
        PlaybackMode.Heartbeat => "",
        _ => "",
    };

    /// <summary>模式按钮 ToolTip。</summary>
    public string PlaybackModeName => PlaybackMode switch
    {
        PlaybackMode.SingleLoop => "单曲循环",
        PlaybackMode.Shuffle => "随机播放",
        PlaybackMode.Heartbeat => "心动模式",
        _ => "列表循环",
    };

    /// <summary>仅心动模式用 Fluent 图标字体(其余用 Segoe MDL2)。</summary>
    public bool IsFluentModeIcon => PlaybackMode == PlaybackMode.Heartbeat;

    partial void OnPlaybackModeChanged(PlaybackMode value)
    {
        OnPropertyChanged(nameof(PlaybackModeGlyph));
        OnPropertyChanged(nameof(PlaybackModeName));
        OnPropertyChanged(nameof(IsFluentModeIcon));
        OnPropertyChanged(nameof(IsListLoopMode));
        OnPropertyChanged(nameof(IsSingleLoopMode));
        OnPropertyChanged(nameof(IsShuffleMode));
    }

    /// <summary>详情页播放模式菜单的单选绑定项(ToggleType=Radio):选中即切到对应模式。</summary>
    public bool IsListLoopMode
    {
        get => PlaybackMode == PlaybackMode.ListLoop;
        set { if (value) PlaybackMode = PlaybackMode.ListLoop; }
    }

    public bool IsSingleLoopMode
    {
        get => PlaybackMode == PlaybackMode.SingleLoop;
        set { if (value) PlaybackMode = PlaybackMode.SingleLoop; }
    }

    public bool IsShuffleMode
    {
        get => PlaybackMode == PlaybackMode.Shuffle;
        set { if (value) PlaybackMode = PlaybackMode.Shuffle; }
    }

    /// <summary>循环切换播放模式:列表循环 → 单曲循环 → 随机播放。
    /// 心动模式暂未实现,不参与切换(枚举值保留,PlayNextAsync 中逻辑留作后续启用)。</summary>
    [RelayCommand]
    private void TogglePlaybackMode()
        => PlaybackMode = (PlaybackMode)(((int)PlaybackMode + 1) % 3);

    partial void OnVolumeChanged(int value) => _player.Volume = value;

    partial void OnPositionMsChanged(long value) => PositionText = FormatTime(value);

    partial void OnDurationMsChanged(long value)
    {
        DurationText = FormatTime(value);
        OnPropertyChanged(nameof(ProgressMaximum));
    }

    /// <summary>进度条 Maximum:空闲时 DurationMs=0 → 至少 1,避免 Min==Max 造成 NaN 布局循环。</summary>
    public double ProgressMaximum => DurationMs > 0 ? DurationMs : 1;

    /// <summary>是否有当前曲目(进度条据此显示)。用 CurrentSong 而非 DurationMs 判断:
    /// 切歌时 PlayAsync 会先把 DurationMs 清零再等新歌时长,按 DurationMs 会在切歌瞬间隐藏进度条;
    /// 只有开软件未放歌(无曲目)时才隐藏。</summary>
    public bool HasProgress => CurrentSong is not null;

    partial void OnCurrentSongChanged(Song? value)
    {
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayArtist));
        RefreshUpcomingItems(); // 切歌后"接下来播放"从新的当前曲起算
        _ = LoadCurrentLikedAsync();
    }

    /// <summary>当前曲红心状态(后台加载;未登录/失败保持未喜欢)。</summary>
    private async Task LoadCurrentLikedAsync()
    {
        if (CurrentSong is not { } song) return;
        try
        {
            await _api.EnsureLikedIdsAsync();
            if (CurrentSong?.Id == song.Id) // 切歌后丢弃过期结果
                IsCurrentLiked = _api.IsLiked(song.Id);
        }
        catch
        {
            // 保持未喜欢
        }
    }

    /// <summary>红心需要登录(未登录时触发,由宿主打开登录窗口)。</summary>
    public event Action? LoginRequired;

    /// <summary>切换当前曲红心:乐观更新,失败回滚;未登录时触发 LoginRequired 弹登录窗口。</summary>
    [RelayCommand]
    private async Task ToggleLikeAsync()
    {
        if (CurrentSong is null) return;
        if (_api.LikedPlaylistId == 0) // 未登录:没有"我喜欢的音乐"歌单
        {
            LoginRequired?.Invoke();
            return;
        }
        var prev = IsCurrentLiked;
        IsCurrentLiked = !prev; // 乐观更新,立即反馈
        try
        {
            IsCurrentLiked = await _api.LikeToggleAsync(CurrentSong.Id);
        }
        catch
        {
            IsCurrentLiked = prev; // 失败回滚
        }
    }

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

    /// <summary>设置播放队列 + 定位当前曲目。播放来自歌单/搜索前调用,使下一曲/上一曲可用。
    /// source 为播放来源名(歌单/每日推荐等),详情页"接下来播放"头部显示。</summary>
    public void SetQueue(IReadOnlyList<Song> queue, Song current, string? source = null)
    {
        if (queue is not { Count: > 0 }) return;
        _queue = queue.ToList();
        _queueIndex = _queue.FindIndex(s => s.Id == current.Id);
        if (_queueIndex < 0) { _queue.Insert(0, current); _queueIndex = 0; }
        QueueSourceName = source;
        _queueVms = _queue.Select(s => new QueueItemViewModel(s, PlayQueueItem, RemoveFromQueue)).ToList();
        RefreshUpcomingItems();
    }

    /// <summary>从列表播放:先记录队列,再播当前曲。供 SongItemViewModel 的队列播放回调使用。
    /// 无来源列表时退化为单曲队列:不能沿用旧队列,否则下一曲/播完自动切会跳回之前歌单里毫不相干的歌。</summary>
    public Task PlayFromList(Song song, IReadOnlyList<Song>? queue, string? source = null)
    {
        if (queue is { Count: > 0 })
            SetQueue(queue, song, source);
        else
        {
            _queue = new List<Song> { song };
            _queueIndex = 0;
            QueueSourceName = null;
            _queueVms = new List<QueueItemViewModel> { new(song, PlayQueueItem, RemoveFromQueue) };
            RefreshUpcomingItems();
        }
        return PlayAsync(song);
    }

    /// <summary>重建"接下来播放"列表:从当前曲下一首开始的循环顺序(当前曲不在列)。</summary>
    private void RefreshUpcomingItems()
    {
        UpcomingItems.Clear();
        if (_queueIndex < 0) return;
        for (var i = 1; i < _queueVms.Count; i++)
            UpcomingItems.Add(_queueVms[(_queueIndex + i) % _queueVms.Count]);
    }

    /// <summary>点播放列表行:直接播该曲(队列不变,仅移动当前位置)。</summary>
    private Task PlayQueueItem(QueueItemViewModel item)
    {
        var idx = _queueVms.IndexOf(item);
        if (idx < 0) return Task.CompletedTask;
        _queueIndex = idx;
        return PlayAsync(_queue[idx]);
    }

    /// <summary>从队列移除一首。移除当前曲不打断播放,当前位置挪到它的前一首(下一曲=被移除曲的后一首)。</summary>
    private void RemoveFromQueue(QueueItemViewModel item)
    {
        var idx = _queueVms.IndexOf(item);
        if (idx < 0) return;
        _queue.RemoveAt(idx);
        _queueVms.RemoveAt(idx);
        if (idx <= _queueIndex) _queueIndex--;
        RefreshUpcomingItems();
    }

    /// <summary>播放一首歌:查播放地址(higher→standard 自动降级),null 提示 VIP/不可播。
    /// 队列在 SongItemViewModel 播放前经 SetQueue 注入,这里只播单曲。</summary>
    [RelayCommand]
    public async Task PlayAsync(Song? song)
    {
        if (song is null) return;

        _advancing++; // 到 finally 才减:PlayUrl 内部 Stop() 会瞬时置 Idle,别把它当"播完"触发自动切歌
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
            _advancing--;
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

    private void OnSmtcNext() => _ = PlayNextAsync();

    private void OnSmtcPrevious() => _ = PlayPreviousAsync();

    /// <summary>播放队列中的下一首。按播放模式:列表循环(尾→头)、单曲循环(重播当前)、
    /// 随机播放、心动模式(下一首红心,无红心则退回列表循环)。</summary>
    [RelayCommand]
    public async Task PlayNextAsync()
    {
        if (_queue.Count == 0) return;
        switch (PlaybackMode)
        {
            case PlaybackMode.SingleLoop when CurrentSong is not null:
                await PlayAsync(CurrentSong);
                break;
            case PlaybackMode.Shuffle:
                await PlayShuffleAsync();
                break;
            case PlaybackMode.Heartbeat:
                await PlayLikedAsync(forward: true);
                break;
            default: // ListLoop:尾→头循环
                _queueIndex = (_queueIndex + 1) % _queue.Count;
                await PlayAsync(_queue[_queueIndex]);
                break;
        }
    }

    /// <summary>播放队列中的上一首,按当前播放模式(列表循环头→尾、单曲重播、随机、心动上一首红心)。</summary>
    [RelayCommand]
    public async Task PlayPreviousAsync()
    {
        if (_queue.Count == 0) return;
        switch (PlaybackMode)
        {
            case PlaybackMode.SingleLoop when CurrentSong is not null:
                await PlayAsync(CurrentSong);
                break;
            case PlaybackMode.Shuffle:
                await PlayShuffleAsync();
                break;
            case PlaybackMode.Heartbeat:
                await PlayLikedAsync(forward: false);
                break;
            default: // ListLoop:头→尾循环
                _queueIndex = (_queueIndex - 1 + _queue.Count) % _queue.Count;
                await PlayAsync(_queue[_queueIndex]);
                break;
        }
    }

    /// <summary>随机播放:队列中随机挑一首(队列 &gt; 1 时避开当前曲)。</summary>
    private async Task PlayShuffleAsync()
    {
        if (_queue.Count <= 1)
        {
            if (CurrentSong is not null) await PlayAsync(CurrentSong);
            return;
        }
        int idx;
        do { idx = Random.Shared.Next(_queue.Count); } while (idx == _queueIndex);
        _queueIndex = idx;
        await PlayAsync(_queue[idx]);
    }

    /// <summary>心动模式:沿队列方向找下一首红心(喜欢的)歌;队列里没有红心则退回列表循环。</summary>
    private async Task PlayLikedAsync(bool forward)
    {
        if (_queue.Count == 0) return;
        await _api.EnsureLikedIdsAsync(); // 已加载则幂等;未登录 → 空集合
        for (var i = 1; i <= _queue.Count; i++)
        {
            var idx = (forward ? _queueIndex + i : _queueIndex - i + _queue.Count) % _queue.Count;
            if (_api.IsLiked(_queue[idx].Id))
            {
                _queueIndex = idx;
                await PlayAsync(_queue[idx]);
                return;
            }
        }
        // 无红心 → 退回列表循环方向
        _queueIndex = (forward ? _queueIndex + 1 : _queueIndex - 1 + _queue.Count) % _queue.Count;
        await PlayAsync(_queue[_queueIndex]);
    }

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
        var newState = _player.State;
        IsLoading = newState == PlaybackState.Loading;
        IsPlaying = newState == PlaybackState.Playing;
        // 歌曲自然播完(Playing/Paused → Idle)按当前模式自动切下一首;
        // PlayUrl 内部 Stop() 的瞬时 Idle 用 _advancing 抑制(启动新歌不是播完)。
        if (_advancing == 0
            && _prevState is PlaybackState.Playing or PlaybackState.Paused
            && newState == PlaybackState.Idle
            && CurrentSong is not null)
        {
            _ = PlayNextAsync();
        }
        _prevState = newState;
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
