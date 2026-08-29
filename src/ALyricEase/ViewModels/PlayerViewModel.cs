using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
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
    private readonly MusicApiProvider _sources;
    private readonly LyricViewModel _lyric;
    private readonly ISmtcService _smtc;

    private bool _scrubbing;
    private bool _seekPending;
    private long _seekTarget;

    // 播放队列:下一曲/上一曲用。PlayAsync 播放时若来自列表(歌单/搜索)会带上来源,否则单曲。
    private List<Song> _queue = new();
    private int _queueIndex = -1;
    // 队列行 VM 缓存(与 _queue 同序对齐):详情页"接下来播放"列表重建时复用,封面不用重载。
    private List<QueueItemViewModel> _queueVms = new();

    private PlaybackState _prevState;
    private int _advancing; // 正在 PlayAsync:抑制 PlayUrl 内部 Stop() 的瞬时 Idle 误判为歌曲播完

    // 私人FM:激活后"下一曲/播完自动切"持续从 FM 接口取歌(无限流);播放其他列表自动退出。
    // 队列保持"当前曲后至少预补 2 首",详情页"接下来播放"能看到后续 FM 曲目。
    private readonly Queue<Song> _fmBuffer = new();
    private bool _fmFetching;

    [ObservableProperty] private bool _isFmActive;

    public PlayerViewModel(IAudioPlayer player, NetEaseApiClient api, MusicApiProvider sources, LyricViewModel lyric, ISmtcService smtc)
    {
        _player = player;
        _api = api;
        _sources = sources;
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
    [ObservableProperty] private double _scrubPositionMs;
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

    /// <summary>模式按钮图标:统一用内嵌 Fluent 字体(F172/EF34/EF37 循环三态,
    /// E70F HeartPulse 心动模式,码位见 Icons.axaml)。</summary>
    public string PlaybackModeGlyph => PlaybackMode switch
    {
        PlaybackMode.SingleLoop => "",
        PlaybackMode.Shuffle => "",
        PlaybackMode.Heartbeat => "",
        _ => "",
    };

    /// <summary>模式按钮 ToolTip。</summary>
    public string PlaybackModeName => PlaybackMode switch
    {
        PlaybackMode.SingleLoop => "单曲循环",
        PlaybackMode.Shuffle => "随机播放",
        PlaybackMode.Heartbeat => "心动模式",
        _ => "列表循环",
    };


    partial void OnPlaybackModeChanged(PlaybackMode value)
    {
        OnPropertyChanged(nameof(PlaybackModeGlyph));
        OnPropertyChanged(nameof(PlaybackModeName));
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

    /// <summary>时间文本跟 ScrubPositionMs(进度条显示位置)走而非 PositionMs:拖动进度条时
    /// 只有 ScrubPositionMs 在变,气泡/时间文本才能随拖动位置实时更新;正常播放、悬停球、
    /// Seek 后,OnPositionChanged/EndScrub 都会把真实进度同步给 ScrubPositionMs,文本保持一致。</summary>
    partial void OnScrubPositionMsChanged(double value) => PositionText = FormatTime((long)Math.Round(value));

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

    /// <summary>当前曲红心状态(后台加载;未登录/失败保持未喜欢)。红心按音源路由到对应平台。</summary>
    private async Task LoadCurrentLikedAsync()
    {
        if (CurrentSong is not { } song) return;
        IUserMusicApi api;
        try { api = _sources.User(song.Source); }
        catch { return; }
        try
        {
            await api.EnsureLikedIdsAsync();
            if (CurrentSong?.Id == song.Id) // 切歌后丢弃过期结果
                IsCurrentLiked = api.IsLiked(song.Id);
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
        IUserMusicApi api;
        try { api = _sources.User(CurrentSong.Source); }
        catch { return; }
        if (!api.CanToggleLike)
        {
            LoginRequired?.Invoke();
            return;
        }
        var prev = IsCurrentLiked;
        IsCurrentLiked = !prev; // 乐观更新,立即反馈
        try
        {
            IsCurrentLiked = await api.LikeToggleAsync(CurrentSong.Id);
        }
        catch (Exception ex)
        {
            IsCurrentLiked = prev; // 失败回滚
            if (CurrentSong.Source != MusicSource.NetEase)
                Message = $"QQ 红心写入未生效({(ex as ApiException)?.Message ?? ex.Message})";
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
    /// 返回是否真正开始播放(false = 该曲当前不可播,调用方把歌曲行置为禁用)。
    /// 无来源列表时退化为单曲队列:不能沿用旧队列,否则下一曲/播完自动切会跳回之前歌单里毫不相干的歌。
    /// 播放任何其他列表都会退出私人FM。</summary>
    public Task<bool> PlayFromList(Song song, IReadOnlyList<Song>? queue, string? source = null)
    {
        IsFmActive = false;
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

    // ---------- 私人FM ----------

    /// <summary>开始私人FM:拉一批(约 3 首)入队播放第一首;之后"下一曲/播完自动切"持续从 FM 取歌。
    /// 未登录时提示(不阻塞页面)。已在 FM 中则幂等。</summary>
    [RelayCommand]
    public async Task StartPersonalFmAsync()
    {
        if (IsFmActive) return;
        if (!_api.IsLoggedIn)
        {
            Message = "登录后收听私人FM";
            return;
        }
        Message = null;
        if (!await FillFmBufferAsync())
        {
            Message = "私人FM获取失败,请稍后重试";
            return;
        }
        var batch = new List<Song>();
        while (_fmBuffer.Count > 0) batch.Add(_fmBuffer.Dequeue());
        SetQueue(batch, batch[0], "私人FM");
        IsFmActive = true;
        _ = PrefetchFmAsync();
        await PlayAsync(batch[0]);
    }

    /// <summary>进入 FM 页时调用(幂等):未激活才启动。</summary>
    public Task EnsurePersonalFmStartedAsync() => IsFmActive ? Task.CompletedTask : StartPersonalFmAsync();

    /// <summary>FM 下一曲:队列接近尾部时从缓冲预补(空则再拉一批),然后前进到下一首。</summary>
    private async Task PlayFmNextAsync()
    {
        if (_queueIndex >= _queue.Count - 1)
        {
            while (_queue.Count - 1 - _queueIndex < 2)
            {
                if (_fmBuffer.Count == 0 && !await FillFmBufferAsync())
                {
                    Message = "私人FM获取失败,请稍后重试";
                    return;
                }
                var song = _fmBuffer.Dequeue();
                _queue.Add(song);
                _queueVms.Add(new QueueItemViewModel(song, PlayQueueItem, RemoveFromQueue));
            }
            RefreshUpcomingItems();
        }
        _ = PrefetchFmAsync();
        _queueIndex++;
        await PlayAsync(_queue[_queueIndex]);
    }

    /// <summary>后台补充 FM 缓冲(缓冲剩余 ≤1 时拉一批)。</summary>
    private async Task PrefetchFmAsync()
    {
        if (_fmBuffer.Count > 1) return;
        await FillFmBufferAsync();
    }

    /// <summary>从 FM 接口拉一批入缓冲。并发单飞;失败返回 false(保留旧缓冲)。
    /// 不用 ConfigureAwait(false):Enqueue 必须回 UI 线程,与 Dequeue(全部 UI 线程)互斥。</summary>
    private async Task<bool> FillFmBufferAsync()
    {
        if (_fmFetching) return _fmBuffer.Count > 0;
        _fmFetching = true;
        try
        {
            var songs = await _api.GetPersonalFmAsync();
            foreach (var s in songs)
                _fmBuffer.Enqueue(s);
            return _fmBuffer.Count > 0;
        }
        catch
        {
            return _fmBuffer.Count > 0;
        }
        finally
        {
            _fmFetching = false;
        }
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
    /// 队列在 SongItemViewModel 播放前经 SetQueue 注入,这里只播单曲。
    /// 返回 true=已开始播放(含试听),false=该曲当前不可播(调用方把歌曲行置为禁用)。
    /// 网络/接口瞬时异常返回 true(不视为"不可播放",行保持可点)。</summary>
    [RelayCommand]
    public async Task<bool> PlayAsync(Song? song)
    {
        if (song is null) return false;

        _advancing++; // 到 finally 才减:PlayUrl 内部 Stop() 会瞬时置 Idle,别把它当"播完"触发自动切歌
        CurrentSong = song;
        Title = song.Name;
        Artist = song.Artist;
        _smtc.SetNowPlaying(song.Name, song.Artist, song.Album, song.CoverUrl);
        SongStarted?.Invoke();
        PositionMs = 0;
        ScrubPositionMs = 0; // 时间文本跟 ScrubPositionMs 走,切歌时一并清零(显示 00:00)
        DurationMs = 0;
        Message = null;
        IsLoading = true;
        IsPlaying = false;
        _ = LoadCoverAsync(song.CoverUrl);
        _ = _lyric.LoadAsync(song); // 并发加载歌词(按音源路由),失败不阻塞播放

        try
        {
            var api = _sources.Resolve(song);
            var item = await api.GetPlayUrlAsync(song, "higher");
            if (item is null || string.IsNullOrEmpty(item.Url))
            {
                // 区分"未登录/非会员"与"版权限制":VIP 歌曲失败先确保会员状态已加载再给文案
                if (song.Fee != 0 && api.IsLoggedIn)
                    await api.EnsureVipStatusAsync();
                Message = BuildUnplayableMessage(song, api);
                return false;
            }

            if (item.IsTrial == true)
                Message = "VIP 歌曲仅试听 30 秒";
            _player.PlayUrl(item.Url);
            return true;
        }
        catch (ApiException ex)
        {
            Message = $"播放失败:{ex.Message}";
            return true; // 瞬时异常:不判"不可播放"
        }
        finally
        {
            _advancing--;
            IsLoading = false;
        }
    }

    /// <summary>播放地址为空时的提示文案:按"歌曲是否 VIP × 是否登录 × 是否会员"区分,
    /// 免费歌/会员仍不可播 → 版权或区域限制(不再笼统归因会员)。</summary>
    private static string BuildUnplayableMessage(Song song, IMusicApi api)
    {
        if (song.Fee == 0)
            return "该歌曲暂不可播放(版权或区域限制)";
        if (song.Source == MusicSource.QQ)
        {
            if (!api.IsLoggedIn) return "该歌曲为 QQ 音乐 VIP 歌曲,登录 QQ 音乐后解锁";
            return api.IsVip
                ? "该歌曲暂不可播放(版权或区域限制)"
                : "该歌曲为 VIP 歌曲,当前账号未开通 QQ 音乐会员(绿钻)";
        }
        // 网易云
        if (!api.IsLoggedIn) return "该歌曲为网易云 VIP 歌曲,登录后解锁";
        return api.IsVip
            ? "该歌曲暂不可播放(版权或区域限制)"
            : "该歌曲为 VIP 歌曲,当前账号未开通网易云会员(音乐包/黑胶)";
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

    /// <summary>播放队列中的下一首。FM 激活时无视播放模式持续从 FM 取歌;
    /// 否则按播放模式:列表循环(尾→头)、单曲循环(重播当前)、随机播放、心动模式(下一首红心,无红心则退回列表循环)。</summary>
    [RelayCommand]
    public async Task PlayNextAsync()
    {
        if (IsFmActive)
        {
            await PlayFmNextAsync();
            return;
        }
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

    /// <summary>播放队列中的上一首。FM 激活时在已播放的 FM 队列内回退;
    /// 否则按当前播放模式(列表循环头→尾、单曲重播、随机、心动上一首红心)。</summary>
    [RelayCommand]
    public async Task PlayPreviousAsync()
    {
        if (IsFmActive)
        {
            if (_queue.Count == 0) return;
            _queueIndex = (_queueIndex - 1 + _queue.Count) % _queue.Count;
            await PlayAsync(_queue[_queueIndex]);
            return;
        }
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

    /// <summary>进度条拖动开始:把当前真实进度复制到 ScrubPositionMs,并暂停用播放器进度覆盖 Slider。</summary>
    public void BeginScrub()
    {
        ScrubPositionMs = PositionMs;
        _scrubbing = true;
        _seekPending = false;
    }

    /// <summary>进度条松开:默认使用 ScrubPositionMs(底部 PlayerBar 也走这里)。</summary>
    public void EndScrub() => EndScrub(ScrubPositionMs);

    /// <summary>进度条松开:直接用 Slider/ScrubPositionMs 的最终值 Seek。
    /// Seek 后进入短暂的 _seekPending 状态,等播放器进度追上目标值再恢复同步 Slider。</summary>
    public void EndScrub(double positionMs)
    {
        _scrubbing = false;

        SeekTo((long)Math.Round(positionMs));
    }

    /// <summary>跳到指定播放时间；供进度条、歌词点按等交互共用。</summary>
    public void SeekTo(long positionMs)
    {
        if (CurrentSong is null) return;

        var upperBound = DurationMs > 0 ? DurationMs : long.MaxValue;
        var ms = Math.Clamp(positionMs, 0, upperBound);
        PositionMs = ms;
        ScrubPositionMs = ms;
        _player.PositionMs = ms;
        _lyric.UpdatePosition(ms);
        _seekPending = true;
        _seekTarget = ms;
        _ = ClearSeekPendingAfterTimeoutAsync();
    }

    private async Task ClearSeekPendingAfterTimeoutAsync()
    {
        // 如果播放器一直没回报接近 Seek 目标的位置,最多 1s 后恢复同步,避免 Slider 卡住。
        await Task.Delay(1000);
        _seekPending = false;
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
        PositionMs = value;

        if (_seekPending)
        {
            // Seek 后只等播放器进度追上目标,避免旧进度事件把 ScrubPositionMs/Slider 拉回去。
            if (Math.Abs(value - _seekTarget) <= 500)
            {
                _seekPending = false;
                ScrubPositionMs = value;
            }
        }
        else if (!_scrubbing)
        {
            // 正常播放:真实进度同步给 Slider。
            ScrubPositionMs = value;
        }

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
