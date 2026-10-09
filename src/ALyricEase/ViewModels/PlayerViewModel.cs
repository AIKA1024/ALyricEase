using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Audio;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
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
    private readonly AppStateStore _appState;
    private readonly MusicCacheService _musicCache;
    private MusicCacheLease? _musicCacheLease;
    private readonly PlaybackCacheSession _playbackCache = new();

    private bool _scrubbing;
    private bool _seekPending;
    private long _seekTarget;

    // 播放队列:下一曲/上一曲用。PlayAsync 播放时若来自列表(歌单/搜索)会带上来源,否则单曲。
    private List<Song> _queue = new();
    private int _queueIndex = -1;
    // 队列行 VM 缓存(与 _queue 同序对齐):详情页"接下来播放"列表重建时复用,封面不用重载。
    private List<QueueItemViewModel> _queueVms = new();
    // 懒歌单播放源:逻辑下标覆盖完整歌单，Song 仅在切到该下标时解析。
    // _queue/_queueVms 在此模式下只是“已经物化、可展示的队列窗口”，不再决定随机范围。
    private ILazySongQueue? _lazyQueue;
    private readonly List<int> _lazyQueueVmIndices = new();
    private readonly HashSet<int> _excludedLazyIndices = new();
    private CancellationTokenSource? _queueRequestCancellation;

    // 随机模式采用逻辑下标洗牌袋：完整歌单一轮内不重复，元数据仍按命中下标解析。
    // 历史保存已播放的 Song，上一首/从历史前进无需再次请求歌曲详情。
    private readonly ShuffleIndexBag _shuffleBag = new();
    private static readonly IReadOnlySet<int> NoShuffleExclusions = new HashSet<int>();
    private readonly List<ShuffleHistoryEntry> _shuffleHistory = new();
    private int _shuffleHistoryPosition = -1;
    private bool _shuffleContextIsLazy;
    private ILazySongQueue? _shuffleLazySource;
    private const int MaxShuffleHistoryCount = 2048;

    // “下一首播放”独立于播放模式；优先级高于单曲循环/洗牌袋，播放后回到原模式。
    private readonly List<Song> _playNextSongs = new();

    private readonly record struct ShuffleHistoryEntry(Song Song, int LogicalIndex, bool IsQueueSong);

    private PlaybackState _prevState;
    private int _advancing; // 正在 PlayAsync:抑制 PlayUrl 内部 Stop() 的瞬时 Idle 误判为歌曲播完

    // 私人FM:激活后"下一曲/播完自动切"持续从 FM 接口取歌(无限流);播放其他列表自动退出。
    // 队列保持"当前曲后至少预补 2 首",并只保留少量已播放历史,避免长时间收听时线性增长。
    private const int MaxFmHistoryCount = 5;
    private readonly Queue<Song> _fmBuffer = new();
    private bool _fmFetching;

    [ObservableProperty] private bool _isFmActive;

    public PlayerViewModel(
        IAudioPlayer player,
        NetEaseApiClient api,
        MusicApiProvider sources,
        LyricViewModel lyric,
        ISmtcService smtc,
        AppStateStore appState,
        MusicCacheService musicCache)
    {
        _player = player;
        _api = api;
        _sources = sources;
        _lyric = lyric;
        _smtc = smtc;
        _appState = appState;
        _musicCache = musicCache;
        _smtc.PlayPauseRequested += OnSmtcPlayPause;
        _smtc.SeekRequested += OnSmtcSeek;
        _smtc.NextRequested += OnSmtcNext;
        _smtc.PreviousRequested += OnSmtcPrevious;
        _playbackMode = (PlaybackMode)_appState.PlaybackMode;
        _player.Volume = _appState.Volume;
        _player.StateChanged += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.DurationChanged += OnDurationChanged;
        _player.ErrorOccurred += OnError;
        _volume = _player.Volume;
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
        ResetShuffleState();
        _appState.PlaybackMode = (int)value;
        _appState.Save();
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

    partial void OnVolumeChanged(int value)
    {
        _player.Volume = value;
        _appState.Volume = Math.Clamp(value, 0, 100);
        _appState.ScheduleSave();
    }

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

    /// <summary>播放条红心按钮可见性:本地歌曲(聚合歌单导入)没有平台红心,隐藏。</summary>
    public bool ShowLikeButton => CurrentSong?.Source != MusicSource.Local;

    partial void OnCurrentSongChanged(Song? value)
    {
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayArtist));
        OnPropertyChanged(nameof(ShowLikeButton)); // 本地歌曲没有平台红心,播放条隐藏红心按钮
        RefreshUpcomingItems(); // 切歌后"接下来播放"从新的当前曲起算
        _ = LoadCurrentLikedAsync();
    }

    /// <summary>按当前登录身份重新判定当前曲红心。换号/登出后由账号页调用:
    /// 播放条不属于任何页面,不会随页面重载刷新 —— 不主动通知就会一直显示旧账号的红心状态。</summary>
    public void RefreshCurrentLiked() => _ = LoadCurrentLikedAsync();

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

    /// <summary>红心需要有效登录或登录凭证已失效时触发。参数=需要(重新)登录的音源,
    /// 失效提示文本(未登录引导为 null)。由宿主打开登录窗口并定位到对应标签。</summary>
    public event Action<MusicSource?, string?>? LoginRequired;

    /// <summary>切换当前曲红心:乐观更新,失败回滚;未登录或凭证失效时触发 LoginRequired 弹登录窗口。</summary>
    [RelayCommand]
    private async Task ToggleLikeAsync()
    {
        if (CurrentSong is null) return;
        IUserMusicApi api;
        try { api = _sources.User(CurrentSong.Source); }
        catch { return; }
        if (!api.CanToggleLike)
        {
            LoginRequired?.Invoke(CurrentSong.Source, null);
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
            if (ex is ApiException apiError && CurrentSong.Source == MusicSource.QQ &&
                QQMusicApiClient.ShouldPromptRelogin(apiError.Code))
            {
                // 服务端判定凭证失效或写权限被拒:本地"已登录"是假象,弹登录窗引导重登(此前静默失败无提示)
                LoginRequired?.Invoke(CurrentSong.Source, QQMusicApiClient.ReloginHintText);
                return;
            }
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
        ResetLazyQueue();
        // 当前曲一定保留；其余播放前已经能确定不可播的歌曲不进入队列。
        _queue = queue.Where(song => SameSong(song, current) || CanAttemptPlayback(song)).ToList();
        _queueIndex = FindQueueIndex(current);
        if (_queueIndex < 0) { _queue.Insert(0, current); _queueIndex = 0; }
        QueueSourceName = source;
        _queueVms = _queue.Select(s => new QueueItemViewModel(s, PlayQueueItem, RemoveFromQueue)).ToList();
        ResetShuffleState();
        RefreshUpcomingItems();
    }

    /// <summary>
    /// 从懒歌单播放：界面只传当前已物化窗口，随机/上一首/下一首通过 lazyQueue 的全量逻辑下标按需取歌。
    /// </summary>
    internal async Task<bool> PlayFromLazyList(
        Song song,
        ILazySongQueue lazyQueue,
        int logicalIndex,
        IReadOnlyList<Song> materialized,
        string? source = null)
        => await PlayFromLazyList(
            song,
            lazyQueue,
            logicalIndex,
            materialized.Select((candidate, index) => (LogicalIndex: index, Song: candidate)).ToList(),
            source);

    /// <summary>支持稀疏物化窗口的懒队列入口：卡片播放只需携带首批已知歌曲及其真实逻辑下标。</summary>
    internal async Task<bool> PlayFromLazyList(
        Song song,
        ILazySongQueue lazyQueue,
        int logicalIndex,
        IReadOnlyList<(int LogicalIndex, Song Song)> materialized,
        string? source)
    {
        if (!CanAttemptPlayback(song)) return false;
        var result = await TryPlayAsync(song);
        if (result == PlayAttemptResult.Unavailable) return false;
        if (result != PlayAttemptResult.Started) return true;

        IsFmActive = false;
        ResetLazyQueue();
        _lazyQueue = lazyQueue;
        _queueRequestCancellation = new CancellationTokenSource();
        var displaySongs = materialized
            .Where(pair => SameSong(pair.Song, song) || CanAttemptPlayback(pair.Song))
            .ToList();
        _queue = displaySongs.Select(pair => pair.Song).ToList();
        _queueVms = _queue.Select(candidate => new QueueItemViewModel(
            candidate, PlayQueueItem, RemoveFromQueue)).ToList();
        _lazyQueueVmIndices.AddRange(displaySongs.Select(pair => pair.LogicalIndex));
        _queueIndex = logicalIndex;
        if (!_lazyQueueVmIndices.Contains(logicalIndex))
        {
            _queue.Add(song);
            _queueVms.Add(new QueueItemViewModel(song, PlayQueueItem, RemoveFromQueue));
            _lazyQueueVmIndices.Add(logicalIndex);
        }
        QueueSourceName = source;
        ResetShuffleState();
        RefreshUpcomingItems();
        return true;
    }

    /// <summary>从列表播放:当前曲确认开始播放后再提交新队列，避免失败歌曲污染当前队列。
    /// 返回是否真正开始播放(false = 该曲当前不可播,调用方把歌曲行置为禁用)。
    /// 无来源列表时退化为单曲队列:不能沿用旧队列,否则下一曲/播完自动切会跳回之前歌单里毫不相干的歌。
    /// 播放任何其他列表都会退出私人FM。</summary>
    public async Task<bool> PlayFromList(Song song, IReadOnlyList<Song>? queue, string? source = null)
    {
        if (!CanAttemptPlayback(song)) return false;
        var result = await TryPlayAsync(song);
        if (result == PlayAttemptResult.Unavailable) return false;
        if (result != PlayAttemptResult.Started) return true;

        IsFmActive = false;
        if (queue is { Count: > 0 })
            SetQueue(queue, song, source);
        else
        {
            ResetLazyQueue();
            _queue = new List<Song> { song };
            _queueIndex = 0;
            QueueSourceName = null;
            _queueVms = new List<QueueItemViewModel> { new(song, PlayQueueItem, RemoveFromQueue) };
            ResetShuffleState();
            RefreshUpcomingItems();
        }
        return true;
    }

    /// <summary>"下一首播放":把歌曲插到当前曲目之后,正在播放页"接下来播放"立即生效。
    /// 什么都没在放时退化为直接播放;歌已在队列里则先移出原位置再插到当前曲后(等价"移到下一首")。
    /// 懒队列下插入物化窗口并标记逻辑下标 -1(该歌不属于懒歌单,取歌逻辑不会重复供给)。</summary>
    public void PlaySongNext(Song song)
    {
        if (!CanAttemptPlayback(song)) return;
        if (CurrentSong is null || _queue.Count == 0)
        {
            _ = PlayFromList(song, null);
            return;
        }

        if (CurrentSong is not null && SameSong(song, CurrentSong)) return;
        var existing = FindQueueIndex(song);
        if (existing >= 0) RemoveQueueEntryAt(existing);

        var currentDisplayIndex = CurrentSong is null ? -1 : FindQueueIndex(CurrentSong);
        var insertIndex = currentDisplayIndex >= 0 ? currentDisplayIndex + 1 : 0;
        if (_lazyQueue is null)
            _shuffleBag.InsertAndShift(insertIndex);
        _queue.Insert(insertIndex, song);
        _queueVms.Insert(insertIndex, new QueueItemViewModel(song, PlayQueueItem, RemoveFromQueue));
        if (_lazyQueue is not null)
            _lazyQueueVmIndices.Insert(insertIndex, -1);
        _playNextSongs.RemoveAll(candidate => SameSong(candidate, song));
        _playNextSongs.Insert(0, song);
        RefreshUpcomingItems();
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
        var result = await TryPlayAsync(batch[0]);
        if (result == PlayAttemptResult.Unavailable)
        {
            var failedIndex = FindQueueIndex(batch[0]);
            if (failedIndex >= 0) RemoveQueueEntryAt(failedIndex);
            await PlayFmNextAsync();
        }
    }

    /// <summary>进入 FM 页时调用(幂等):未激活才启动。</summary>
    public Task EnsurePersonalFmStartedAsync() => IsFmActive ? Task.CompletedTask : StartPersonalFmAsync();

    /// <summary>FM 下一曲:队列接近尾部时从缓冲预补(空则再拉一批),然后前进到下一首。</summary>
    private async Task PlayFmNextAsync()
    {
        const int maxUnavailableSkips = 50;
        var unavailableSkips = 0;
        for (var attempt = 0; attempt < maxUnavailableSkips; attempt++)
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
                    var bufferedSong = _fmBuffer.Dequeue();
                    if (!CanAttemptPlayback(bufferedSong))
                    {
                        if (++unavailableSkips >= maxUnavailableSkips)
                        {
                            Message = "私人FM连续歌曲均不可播放,请稍后重试";
                            return;
                        }
                        continue;
                    }
                    _queue.Add(bufferedSong);
                    _queueVms.Add(new QueueItemViewModel(bufferedSong, PlayQueueItem, RemoveFromQueue));
                }
            }
            _ = PrefetchFmAsync();
            var index = _queueIndex + 1;
            var song = _queue[index];
            if (!CanAttemptPlayback(song))
            {
                RemoveQueueEntryAt(index);
                continue;
            }
            var result = await TryPlayAsync(song);
            if (result == PlayAttemptResult.TransientFailure) return;
            if (result == PlayAttemptResult.Unavailable)
            {
                RemoveQueueEntryAt(index);
                continue;
            }
            _queueIndex = FindQueueIndex(song);
            TrimFmHistory();
            RefreshUpcomingItems();
            return;
        }
        Message = "私人FM连续歌曲均不可播放,请稍后重试";
    }

    /// <summary>FM 是无限流,只保留当前曲之前最近几首供"上一曲"使用。
    /// 当前曲与预取的后续曲不动,Song/QueueItemVM(含已加载封面)随旧历史一起释放。</summary>
    private void TrimFmHistory()
    {
        var removeCount = Math.Max(0, _queueIndex - MaxFmHistoryCount);
        if (removeCount == 0) return;
        _queue.RemoveRange(0, removeCount);
        _queueVms.RemoveRange(0, removeCount);
        _queueIndex -= removeCount;
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
        if (_lazyQueue is not null)
        {
            // 详情页仍只展示已物化窗口；排序按完整歌单逻辑下标从当前曲之后循环。
            // 哨兵(逻辑位 -1,"下一首播放"插入)视为当前曲之后立刻播放:排序键取 _queueIndex+0.5。
            foreach (var pair in _lazyQueueVmIndices
                         .Select((logicalIndex, vmIndex) => (logicalIndex, vmIndex))
                         .Where(pair => pair.logicalIndex != _queueIndex
                                        && pair.logicalIndex != -1
                                        && !_excludedLazyIndices.Contains(pair.logicalIndex))
                         .Select(pair => (orderKey: pair.logicalIndex > _queueIndex ? (double)pair.logicalIndex
                                              : pair.logicalIndex + (double)_lazyQueueVmIndices.Count,
                                          vmIndex: pair.vmIndex))
                         .Where(pair => pair.orderKey != _queueIndex)
                         .OrderBy(pair => pair.orderKey > _queueIndex ? 0 : 1)
                         .ThenBy(pair => pair.orderKey))
                UpcomingItems.Add(_queueVms[pair.vmIndex]);
            // 哨兵固定插在最前(它就是"下一首")
            foreach (var vm in _queueVms
                         .Select((vm, vmIndex) => (vm, vmIndex))
                         .Where(pair => _lazyQueueVmIndices[pair.vmIndex] == -1)
                         .OrderByDescending(pair => pair.vmIndex)
                         .Select(pair => pair.vm))
                UpcomingItems.Insert(0, vm);
            return;
        }
        for (var i = 1; i < _queueVms.Count; i++)
            UpcomingItems.Add(_queueVms[(_queueIndex + i) % _queueVms.Count]);
    }

    /// <summary>点播放列表行:直接播该曲(队列不变,仅移动当前位置)。</summary>
    private async Task PlayQueueItem(QueueItemViewModel item)
    {
        var idx = _queueVms.IndexOf(item);
        if (idx < 0) return;
        var song = _queue[idx];
        if (!CanAttemptPlayback(song))
        {
            RemoveQueueEntryAt(idx);
            return;
        }

        if (_lazyQueue is not null)
        {
            var logicalIndex = _lazyQueueVmIndices[idx];
            var result = await TryPlayAsync(song);
            if (result == PlayAttemptResult.Started)
            {
                _playNextSongs.Clear();
                if (logicalIndex == -1)
                {
                    _lazyQueueVmIndices.RemoveAt(idx);
                    _queue.RemoveAt(idx);
                    _queueVms.RemoveAt(idx);
                }
                else
                {
                    _queueIndex = logicalIndex;
                }
                ResetShuffleState();
                RefreshUpcomingItems();
            }
            else if (result == PlayAttemptResult.Unavailable)
            {
                RemoveQueueEntryAt(idx);
            }
            return;
        }
        var playResult = await TryPlayAsync(song);
        if (playResult == PlayAttemptResult.Started)
        {
            _queueIndex = idx;
            _playNextSongs.Clear();
            ResetShuffleState();
            RefreshUpcomingItems();
        }
        else if (playResult == PlayAttemptResult.Unavailable)
        {
            RemoveQueueEntryAt(idx);
        }
    }

    /// <summary>从队列移除一首。移除当前曲不打断播放,当前位置挪到它的前一首(下一曲=被移除曲的后一首)。</summary>
    private void RemoveFromQueue(QueueItemViewModel item)
    {
        var idx = _queueVms.IndexOf(item);
        if (idx < 0) return;
        RemoveQueueEntryAt(idx);
    }

    private void RemoveQueueEntryAt(int idx)
    {
        if ((uint)idx >= (uint)_queue.Count) return;
        var removedSong = _queue[idx];
        _playNextSongs.RemoveAll(candidate => SameSong(candidate, removedSong));
        if (_lazyQueue is not null)
        {
            var logicalIndex = _lazyQueueVmIndices[idx];
            if (logicalIndex == -1)
            {
                // 哨兵(-1,"下一首播放"插入):不属于懒歌单,直接从三个显示列表同步摘除即可
                _lazyQueueVmIndices.RemoveAt(idx);
                _queue.RemoveAt(idx);
                _queueVms.RemoveAt(idx);
                var playingIdx = CurrentSong is null ? -1 : FindQueueIndex(CurrentSong);
                _queueIndex = playingIdx >= 0
                    ? playingIdx
                    : _queue.Count == 0 ? -1 : Math.Min(idx - 1, _queue.Count - 1);
                RefreshUpcomingItems();
                return;
            }
            ExcludeLazyIndex(logicalIndex);
            return;
        }
        _shuffleBag.RemoveAndShift(idx);
        _queue.RemoveAt(idx);
        _queueVms.RemoveAt(idx);
        RemoveShuffleHistoryWhere(entry => entry.IsQueueSong && SameSong(entry.Song, removedSong));
        var playingIndex = CurrentSong is null
            ? -1
            : FindQueueIndex(CurrentSong);
        _queueIndex = playingIndex >= 0
            ? playingIndex
            : _queue.Count == 0 ? -1 : Math.Min(idx - 1, _queue.Count - 1);
        RefreshUpcomingItems();
    }

    private void ExcludeLazyIndex(int logicalIndex)
    {
        _excludedLazyIndices.Add(logicalIndex);
        _shuffleBag.Exclude(logicalIndex);
        RemoveShuffleHistoryWhere(entry => entry.IsQueueSong && entry.LogicalIndex == logicalIndex);
        var displayIndex = _lazyQueueVmIndices.IndexOf(logicalIndex);
        if (displayIndex >= 0)
        {
            var removedSong = _queue[displayIndex];
            _playNextSongs.RemoveAll(candidate => SameSong(candidate, removedSong));
            _lazyQueueVmIndices.RemoveAt(displayIndex);
            _queue.RemoveAt(displayIndex);
            _queueVms.RemoveAt(displayIndex);
        }
        RefreshUpcomingItems();
    }

    private bool CanAttemptPlayback(Song song)
    {
        try { return PlaybackAvailability.CanAttempt(song, _sources.Resolve(song)); }
        catch (NotSupportedException) { return PlaybackAvailability.CanAttempt(song, null); }
    }

    private static bool SameSong(Song left, Song right)
        => left.Source == right.Source && left.Id == right.Id;

    private int FindQueueIndex(Song song)
    {
        var index = _queue.FindIndex(candidate => ReferenceEquals(candidate, song));
        return index >= 0 ? index : _queue.FindIndex(candidate => SameSong(candidate, song));
    }

    private void ResetLazyQueue()
    {
        _queueRequestCancellation?.Cancel();
        _queueRequestCancellation?.Dispose();
        _queueRequestCancellation = null;
        _lazyQueue = null;
        _lazyQueueVmIndices.Clear();
        _excludedLazyIndices.Clear();
        _playNextSongs.Clear();
        _shuffleBag.Clear();
        _shuffleHistory.Clear();
        _shuffleHistoryPosition = -1;
        _shuffleLazySource = null;
    }

    /// <summary>播放一首歌:查播放地址(higher→standard 自动降级),null 提示 VIP/不可播。
    /// 队列在 SongItemViewModel 播放前经 SetQueue 注入,这里只播单曲。
    /// 返回 false=该曲已确认不可播(调用方把歌曲行置为禁用)；成功与瞬时网络异常返回 true，
    /// 后者只表示“不应禁用歌曲行”。</summary>
    private enum PlayAttemptResult
    {
        Started,
        Unavailable,
        TransientFailure,
    }

    [RelayCommand]
    public async Task<bool> PlayAsync(Song? song)
        => await TryPlayAsync(song) != PlayAttemptResult.Unavailable;

    private async Task<PlayAttemptResult> TryPlayAsync(Song? song, bool allowCrossfade = true)
    {
        if (song is null) return PlayAttemptResult.Unavailable;

        // 本地文件不经过音源 API 与磁盘缓存:直接播绝对路径(双击文件/打开方式的入口)。
        if (song.Source == MusicSource.Local)
            return await TryPlayLocalAsync(song, allowCrossfade).ConfigureAwait(true);

        // 重播同一首(单曲循环/播完重按)不做交叉:同一音源重叠是回声不是淡化
        if (CurrentSong is not null && SameSong(song, CurrentSong)) allowCrossfade = false;

        _advancing++; // 到 finally 才减:PlayUrl 内部 Stop() 会瞬时置 Idle,别把它当"播完"触发自动切歌
        Message = null;
        IsLoading = true;
        MusicCacheLease? offlineFallback = null;

        try
        {
            var qualityLevel = AudioQualityMapper.GetRequestLevel(song.Source, _appState.AudioQuality);
            if (_musicCache.TryAcquire(song, qualityLevel) is { } cached)
            {
                song.IsPlaybackUnavailable = false;
                PrepareSongPlayback(song);
                await StartPlayerAsync(cached.FilePath, cached, allowCrossfade);
                return PlayAttemptResult.Started;
            }

            // 当前偏好可能高于磁盘已有音质。在线时仍优先尝试取目标音质，断网/取址失败才降级播放本地版本。
            offlineFallback = _musicCache.TryAcquireBestAvailable(song);
            if (song.PreferCachedPlayback && offlineFallback is not null)
            {
                song.IsPlaybackUnavailable = false;
                PrepareSongPlayback(song);
                await StartPlayerAsync(offlineFallback.FilePath, offlineFallback, allowCrossfade);
                offlineFallback = null;
                Message = "正在播放本地缓存";
                return PlayAttemptResult.Started;
            }

            var api = _sources.Resolve(song);
            var item = await api.GetPlayUrlAsync(song, qualityLevel);
            if (item is null || string.IsNullOrEmpty(item.Url))
            {
                if (offlineFallback is not null)
                {
                    song.IsPlaybackUnavailable = false;
                    PrepareSongPlayback(song);
                    await StartPlayerAsync(offlineFallback.FilePath, offlineFallback, allowCrossfade);
                    offlineFallback = null;
                    Message = "正在播放本地缓存";
                    return PlayAttemptResult.Started;
                }

                song.IsPlaybackUnavailable = true;
                // 区分"未登录/非会员"与"版权限制":VIP 歌曲失败先确保会员状态已加载再给文案
                if (song.Fee != 0 && api.IsLoggedIn)
                    await api.EnsureVipStatusAsync();
                Message = BuildUnplayableMessage(song, api);
                return PlayAttemptResult.Unavailable;
            }

            song.IsPlaybackUnavailable = false;
            PrepareSongPlayback(song);

            // 服务器实际给的档位不高于磁盘已有缓存 ⇒ 直接播缓存,省一次完整在线流。
            // 否则"偏好 sky(等级6)但歌曲最高只有 exhigh(等级3)"的歌:TryAcquire 要求
            // 缓存≥请求档位永远 miss,CacheAsync 又因同档位已存在被去重跳过 ⇒
            // 缓存文件一直躺在磁盘上,每次播放却都重新下载整首歌(320k 一首约 9MB)。
            var servedRank = Math.Max(
                MusicCacheService.GetQualityRank(song.Source, item.Level),
                MusicCacheService.GetQualityRankFromBr(item.Br));
            if (offlineFallback is not null && servedRank <= offlineFallback.QualityRank)
            {
                await StartPlayerAsync(offlineFallback.FilePath, offlineFallback, allowCrossfade);
                offlineFallback = null;
                return PlayAttemptResult.Started;
            }

            if (item.IsTrial == true)
                Message = "VIP 歌曲仅试听 30 秒";
            offlineFallback?.Dispose();
            offlineFallback = null;
            await StartPlayerAsync(item.Url, cacheLease: null, allowCrossfade);
            if (item.IsTrial != true && ReferenceEquals(CurrentSong, song))
                _playbackCache.Begin(
                    token => _musicCache.CacheAsync(
                        song,
                        string.IsNullOrWhiteSpace(item.Level) ? qualityLevel : item.Level,
                        item.Url, token),
                    _player.State == PlaybackState.Playing);
            return PlayAttemptResult.Started;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            if (offlineFallback is not null)
            {
                song.IsPlaybackUnavailable = false;
                PrepareSongPlayback(song);
                await StartPlayerAsync(offlineFallback.FilePath, offlineFallback, allowCrossfade);
                offlineFallback = null;
                Message = "当前网络不可用，正在播放本地缓存";
                return PlayAttemptResult.Started;
            }
            Message = $"播放失败:{ex.Message}";
            return PlayAttemptResult.TransientFailure; // 瞬时异常:不判"不可播放"
        }
        finally
        {
            offlineFallback?.Dispose();
            _advancing--;
            IsLoading = false;
        }
    }

    /// <summary>播放地址为空时的提示文案:按收费类型、登录与会员状态区分,
    /// 免费歌/会员仍不可播 → 版权或区域限制(不再笼统归因会员)。</summary>
    private static string BuildUnplayableMessage(Song song, IMusicApi api)
    {
        if (song.Fee is 0 or 8)
            return "该歌曲暂不可播放(版权或区域限制)";
        if (song.Source == MusicSource.NetEase && song.Fee == 4 && song.IsPurchased != true)
            return api.IsLoggedIn
                ? "该歌曲需单独购买数字专辑后播放"
                : "该歌曲需登录已购买数字专辑的网易云账号后播放";
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

    /// <summary>本地音频文件播放:文件存在即起播,不查缓存/不请求播放地址。
    /// 缺失(移动/删除/恢复的历史记录)判不可播并提示。</summary>
    private async Task<PlayAttemptResult> TryPlayLocalAsync(Song song, bool allowCrossfade)
    {
        var path = song.LocalFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            song.IsPlaybackUnavailable = true;
            Message = "本地文件不存在或已被移动";
            return PlayAttemptResult.Unavailable;
        }

        _advancing++;
        Message = null;
        IsLoading = true;
        try
        {
            song.IsPlaybackUnavailable = false;
            PrepareSongPlayback(song);
            await StartPlayerAsync(path, cacheLease: null, allowCrossfade).ConfigureAwait(true);
            return PlayAttemptResult.Started;
        }
        finally
        {
            _advancing--;
            IsLoading = false;
        }
    }

    /// <summary>播放一组本地音频文件(打开方式/启动参数入口):全部入普通队列并起播第一个。
    /// 空列表不动作。返回是否真正开始播放。</summary>
    public async Task<bool> PlayLocalFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        var songs = paths.Select(LocalAudioFiles.CreateSong).ToList();
        return await PlayFromList(songs[0], songs, "本地音乐").ConfigureAwait(true);
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

    /// <summary>直接播放一个歌单(用户页/收藏页行内 ▶ 与歌单 more 菜单共用)：
    /// 只取概览/首批曲目并立即起播，剩余歌曲通过懒队列按切歌下标解析。</summary>
    [RelayCommand]
    private async Task PlayPlaylistAsync(PlaylistItemViewModel? pvm)
    {
        if (pvm is null || pvm.Id == 0) return;
        try
        {
            if (pvm.Playlist.Source == MusicSource.QQ)
            {
                if (_sources.Resolve(MusicSource.QQ) is not QQMusicApiClient qqApi) return;
                const int pageSize = 50;
                var firstPage = await qqApi.GetPlaylistTrackPageAsync(pvm.Id, 0, pageSize)
                    .ConfigureAwait(true);
                var lazyQueue = new PagedSongQueue(
                    firstPage.TotalCount,
                    firstPage.Songs,
                    async (begin, count, ct) =>
                    {
                        var page = await qqApi.GetPlaylistTrackPageAsync(pvm.Id, begin, count, ct)
                            .ConfigureAwait(false);
                        return new PagedSongQueue.Page(page.Songs, page.TotalCount);
                    },
                    pageSize);
                var materialized = firstPage.Songs
                    .Select((song, index) => (LogicalIndex: index, Song: song))
                    .ToList();
                await PlayFirstAvailableAsync(lazyQueue, materialized, pvm.Name).ConfigureAwait(true);
                return;
            }

            var overview = await _api.GetPlaylistTrackOverviewAsync(pvm.Id).ConfigureAwait(true);
            if (overview.TrackIds.Count == 0) return;

            // v6 通常自带前段曲目：先直接用它起播，不为凑满“首批”额外等待 song/detail。
            var known = overview.PrefixTracks
                .GroupBy(song => song.Id)
                .ToDictionary(group => group.Key, group => group.First());
            var firstIds = overview.TrackIds.Take(50).ToList();
            var lazyNetEase = new IndexedSongQueue(
                overview.TrackIds,
                known.Values,
                _api.GetSongsByIdsAsync);
            var firstWindow = firstIds
                .Select((id, index) => (LogicalIndex: index,
                    Song: known.TryGetValue(id, out var song) ? song : null))
                .Where(pair => pair.Song is not null)
                .Select(pair => (pair.LogicalIndex, pair.Song!))
                .ToList();
            if (await PlayFirstAvailableAsync(lazyNetEase, firstWindow, pvm.Name).ConfigureAwait(true))
                return;

            // 前段元数据为空或全部不可播时，最多补首 50 首缺失项再找一次，仍不触碰完整歌单。
            var missing = firstIds.Where(id => !known.ContainsKey(id)).ToList();
            if (missing.Count == 0) return;
            foreach (var song in await _api.GetSongsByIdsAsync(missing).ConfigureAwait(true))
            {
                known[song.Id] = song;
                var index = firstIds.IndexOf(song.Id);
                if (index >= 0) lazyNetEase.Remember(index, song);
            }
            var resolvedWindow = firstIds
                .Select((id, index) => (LogicalIndex: index,
                    Song: known.TryGetValue(id, out var song) ? song : null))
                .Where(pair => pair.Song is not null)
                .Select(pair => (pair.LogicalIndex, pair.Song!))
                .ToList();
            await PlayFirstAvailableAsync(lazyNetEase, resolvedWindow, pvm.Name).ConfigureAwait(true);
        }
        catch
        {
            // 拉取/播放失败静默
        }
    }

    private async Task<bool> PlayFirstAvailableAsync(
        ILazySongQueue lazyQueue,
        IReadOnlyList<(int LogicalIndex, Song Song)> materialized,
        string source)
    {
        foreach (var (logicalIndex, song) in materialized)
        {
            if (!CanAttemptPlayback(song)) continue;
            if (await PlayFromLazyList(song, lazyQueue, logicalIndex, materialized, source)
                    .ConfigureAwait(true))
                return true;
        }
        return false;
    }

    private void OnSmtcNext() => _ = PlayNextAsync();

    private void OnSmtcPrevious() => _ = PlayPreviousAsync();

    /// <summary>消费“下一首播放”队列。随机模式下它不占用洗牌袋，但会进入播放历史；
    /// 懒歌单中的插入项播放后从物化窗口移除，逻辑位置仍停在原歌单锚点。</summary>
    private async Task<bool> TryPlayPendingNextAsync()
    {
        while (_playNextSongs.Count > 0)
        {
            var pending = _playNextSongs[0];
            var displayIndex = FindQueueIndex(pending);
            if (displayIndex < 0)
            {
                _playNextSongs.RemoveAt(0);
                continue;
            }

            var isLazySentinel = _lazyQueue is not null
                                 && (uint)displayIndex < (uint)_lazyQueueVmIndices.Count
                                 && _lazyQueueVmIndices[displayIndex] == -1;
            var logicalAnchor = _queueIndex;
            var result = await TryPlayAsync(pending);
            if (result == PlayAttemptResult.TransientFailure) return true;

            _playNextSongs.RemoveAt(0);
            if (isLazySentinel)
            {
                _lazyQueueVmIndices.RemoveAt(displayIndex);
                _queue.RemoveAt(displayIndex);
                _queueVms.RemoveAt(displayIndex);
                if (result == PlayAttemptResult.Started)
                {
                    if (PlaybackMode == PlaybackMode.Shuffle && !IsFmActive)
                        CommitShuffleHistory(new ShuffleHistoryEntry(
                            pending, logicalAnchor, IsQueueSong: false));
                    RefreshUpcomingItems();
                    return true;
                }

                RefreshUpcomingItems();
                continue;
            }

            if (result == PlayAttemptResult.Started)
            {
                _queueIndex = FindQueueIndex(pending);
                if (PlaybackMode == PlaybackMode.Shuffle && !IsFmActive)
                    CommitShuffleHistory(new ShuffleHistoryEntry(
                        pending, _queueIndex, IsQueueSong: true));
                RefreshUpcomingItems();
                return true;
            }

            var failedIndex = FindQueueIndex(pending);
            if (failedIndex >= 0) RemoveQueueEntryAt(failedIndex);
        }

        return false;
    }

    /// <summary>播放队列中的下一首。FM 激活时无视播放模式持续从 FM 取歌;
    /// 否则按播放模式:列表循环(尾→头)、单曲循环(重播当前)、随机播放、心动模式(下一首红心,无红心则退回列表循环)。</summary>
    [RelayCommand]
    public async Task PlayNextAsync()
    {
        // 用户显式指定的“下一首”永远先于单曲循环、随机袋和 FM；成功后恢复原模式。
        if (await TryPlayPendingNextAsync()) return;
        if (IsFmActive)
        {
            await PlayFmNextAsync();
            return;
        }
        if (_queue.Count == 0 && _lazyQueue is null) return;
        switch (PlaybackMode)
        {
            case PlaybackMode.SingleLoop when CurrentSong is not null:
                await PlayAsync(CurrentSong);
                break;
            case PlaybackMode.Shuffle:
                await PlayShuffleNextAsync();
                break;
            case PlaybackMode.Heartbeat:
                await PlayLikedAsync(forward: true);
                break;
            case PlaybackMode.ListLoop when _lazyQueue is not null:
                await PlayLazyAdjacentAsync(forward: true);
                break;
            default: // ListLoop:尾→头循环
                await PlayQueueAdjacentAsync(forward: true);
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
            // FM 只保留有限历史;到达最旧一首后不循环跳到尚未播放的队尾。
            while (_queue.Count > 0 && _queueIndex > 0)
            {
                var index = _queueIndex - 1;
                var song = _queue[index];
                if (!CanAttemptPlayback(song))
                {
                    RemoveQueueEntryAt(index);
                    continue;
                }
                var result = await TryPlayAsync(song);
                if (result == PlayAttemptResult.TransientFailure) return;
                if (result == PlayAttemptResult.Unavailable)
                {
                    RemoveQueueEntryAt(index);
                    continue;
                }
                _queueIndex = FindQueueIndex(song);
                RefreshUpcomingItems();
                return;
            }
            return;
        }
        if (_queue.Count == 0 && _lazyQueue is null) return;
        switch (PlaybackMode)
        {
            case PlaybackMode.SingleLoop when CurrentSong is not null:
                await PlayAsync(CurrentSong);
                break;
            case PlaybackMode.Shuffle:
                await PlayShufflePreviousAsync();
                break;
            case PlaybackMode.Heartbeat:
                await PlayLikedAsync(forward: false);
                break;
            case PlaybackMode.ListLoop when _lazyQueue is not null:
                await PlayLazyAdjacentAsync(forward: false);
                break;
            default: // ListLoop:头→尾循环
                await PlayQueueAdjacentAsync(forward: false);
                break;
        }
    }

    private void PrepareSongPlayback(Song song)
    {
        _playbackCache.Cancel();
        _crossfadeAdvancing = false; // 新歌起播:允许在它的末段再触发交叉切歌
        CurrentSong = song;
        Title = song.Name;
        Artist = song.Artist;
        _smtc.SetNowPlaying(song.Name, song.Artist, song.Album, song.CoverUrl);
        SongStarted?.Invoke();
        PositionMs = 0;
        ScrubPositionMs = 0; // 时间文本跟 ScrubPositionMs 走,切歌时一并清零(显示 00:00)
        DurationMs = 0;
        _ = LoadCoverAsync(song);
        _ = _lyric.LoadAsync(song); // 并发加载歌词(按音源路由),失败不阻塞播放
    }

    private async Task StartPlayerAsync(string source, MusicCacheLease? cacheLease, bool allowCrossfade)
    {
        var previousLease = _musicCacheLease;
        try
        {
            // 交叉淡化:旧曲继续播并渐弱、新曲渐强(仅当后端支持、设置开启、且旧曲正在播)。
            // 返回 false = 未执行(不支持/未开/没在播/失败),回落硬切。
            var crossfaded = allowCrossfade && _appState.Crossfade
                             && await _player.TryCrossfadePlayAsync(source, CrossfadeMs());
            if (crossfaded)
            {
                _musicCacheLease = cacheLease;
                if (previousLease is not null)
                {
                    // 旧实例还要把旧音源播完渐变段:缓存租约延迟释放,防渐变中途文件被 LRU 清掉
                    var lease = previousLease;
                    _ = Task.Delay(CrossfadeMs() + 1000).ContinueWith(_ => lease.Dispose());
                }
            }
            else
            {
                _player.PlayUrl(source);
                _musicCacheLease = cacheLease;
                previousLease?.Dispose();
            }

            if (CurrentSong is { } song)
            {
                try { _appState.RecordRecentSong(song); }
                catch { /* 历史记录故障不能中断已经成功开始的播放 */ }
            }
        }
        catch
        {
            _musicCacheLease = null;
            cacheLease?.Dispose();
            throw;
        }
    }

    /// <summary>交叉淡化时长(毫秒);设置页滑杆 0-12 秒,越界夹回。</summary>
    private int CrossfadeMs() =>
        (int)Math.Round(Math.Clamp(_appState.CrossfadeSeconds, 0, 12) * 1000);

    /// <summary>普通队列按方向寻找可播歌曲；确认不可播的项立即从队列移除并继续。</summary>
    private async Task PlayQueueAdjacentAsync(bool forward)
    {
        var remainingAttempts = _queue.Count;
        while (_queue.Count > 0 && remainingAttempts-- > 0)
        {
            var index = forward
                ? (_queueIndex + 1 + _queue.Count) % _queue.Count
                : (_queueIndex - 1 + _queue.Count) % _queue.Count;
            var song = _queue[index];
            if (!CanAttemptPlayback(song))
            {
                RemoveQueueEntryAt(index);
                continue;
            }

            var result = await TryPlayAsync(song);
            if (result == PlayAttemptResult.Started)
            {
                _queueIndex = FindQueueIndex(song);
                RefreshUpcomingItems();
                return;
            }
            if (result == PlayAttemptResult.TransientFailure) return;
            RemoveQueueEntryAt(index);
        }
    }

    /// <summary>随机下一首：优先沿“上一首”留下的前向历史移动；到历史末端后才从洗牌袋取新候选。</summary>
    private async Task PlayShuffleNextAsync()
    {
        EnsureShuffleContext();
        var ct = _queueRequestCancellation?.Token ?? CancellationToken.None;
        try
        {
            while (_shuffleHistoryPosition + 1 < _shuffleHistory.Count)
            {
                var target = _shuffleHistoryPosition + 1;
                var result = await TryPlayShuffleHistoryEntryAsync(_shuffleHistory[target], ct);
                if (result == PlayAttemptResult.Started)
                {
                    _shuffleHistoryPosition = target;
                    return;
                }
                if (result == PlayAttemptResult.TransientFailure) return;
                RemoveShuffleHistoryAt(target);
            }

            var lazySource = _lazyQueue;
            var count = lazySource is null ? _queue.Count : await lazySource.GetCountAsync(ct);
            var remainingAttempts = count;
            while (remainingAttempts-- > 0)
            {
                var currentCount = lazySource is null ? _queue.Count : count;
                if (_shuffleBag.Count == 0)
                    _shuffleBag.Refill(currentCount, _queueIndex,
                        lazySource is null ? NoShuffleExclusions : _excludedLazyIndices,
                        Random.Shared);
                if (!_shuffleBag.TryTake(out var index)) break;

                var (result, song) = await TryPlayShuffleIndexAsync(index, lazySource, ct);
                if (result == PlayAttemptResult.Started && song is not null)
                {
                    CommitShuffleHistory(new ShuffleHistoryEntry(song, index, IsQueueSong: true));
                    return;
                }
                if (result == PlayAttemptResult.TransientFailure)
                {
                    _shuffleBag.PutBack(index);
                    return;
                }

                if (lazySource is not null && !ReferenceEquals(lazySource, _lazyQueue)) return;
            }

            if (CurrentSong is not null) await PlayAsync(CurrentSong);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Message = $"加载播放队列失败:{ex.Message}";
        }
    }

    /// <summary>随机上一首严格沿成功播放历史返回，不重新抽取随机歌曲。</summary>
    private async Task PlayShufflePreviousAsync()
    {
        EnsureShuffleContext();
        var ct = _queueRequestCancellation?.Token ?? CancellationToken.None;
        try
        {
            while (_shuffleHistoryPosition > 0)
            {
                var target = _shuffleHistoryPosition - 1;
                var result = await TryPlayShuffleHistoryEntryAsync(_shuffleHistory[target], ct);
                if (result == PlayAttemptResult.Started)
                {
                    _shuffleHistoryPosition = target;
                    return;
                }
                if (result == PlayAttemptResult.TransientFailure) return;
                RemoveShuffleHistoryAt(target);
            }

            if (CurrentSong is not null) await PlayAsync(CurrentSong);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Message = $"加载播放队列失败:{ex.Message}";
        }
    }

    private async Task<(PlayAttemptResult Result, Song? Song)> TryPlayShuffleIndexAsync(
        int index,
        ILazySongQueue? lazySource,
        CancellationToken ct)
    {
        Song? song;
        if (lazySource is not null)
        {
            song = await lazySource.GetSongAsync(index, ct);
            if (!ReferenceEquals(lazySource, _lazyQueue)) return (PlayAttemptResult.TransientFailure, null);
            if (song is null)
            {
                ExcludeLazyIndex(index);
                return (PlayAttemptResult.Unavailable, null);
            }
        }
        else
        {
            if ((uint)index >= (uint)_queue.Count) return (PlayAttemptResult.Unavailable, null);
            song = _queue[index];
        }

        if (!CanAttemptPlayback(song))
        {
            if (lazySource is not null) ExcludeLazyIndex(index);
            else RemoveQueueEntryAt(index);
            return (PlayAttemptResult.Unavailable, song);
        }

        var result = await TryPlayAsync(song);
        if (result == PlayAttemptResult.Unavailable)
        {
            if (lazySource is not null) ExcludeLazyIndex(index);
            else
            {
                var queueIndex = FindQueueIndex(song);
                if (queueIndex >= 0) RemoveQueueEntryAt(queueIndex);
            }
            return (result, song);
        }
        if (result != PlayAttemptResult.Started) return (result, song);

        _queueIndex = lazySource is null ? FindQueueIndex(song) : index;
        if (lazySource is not null) RememberLazyDisplaySong(index, song);
        RefreshUpcomingItems();
        return (result, song);
    }

    private async Task<PlayAttemptResult> TryPlayShuffleHistoryEntryAsync(
        ShuffleHistoryEntry entry,
        CancellationToken ct)
    {
        if (entry.IsQueueSong)
        {
            if (_shuffleContextIsLazy)
            {
                if (_excludedLazyIndices.Contains(entry.LogicalIndex))
                    return PlayAttemptResult.Unavailable;
            }
            else if (FindQueueIndex(entry.Song) < 0)
            {
                return PlayAttemptResult.Unavailable;
            }
        }

        if (!CanAttemptPlayback(entry.Song)) return PlayAttemptResult.Unavailable;
        var result = await TryPlayAsync(entry.Song);
        if (result != PlayAttemptResult.Started) return result;

        if (_shuffleContextIsLazy)
        {
            _queueIndex = entry.LogicalIndex;
            if (entry.IsQueueSong) RememberLazyDisplaySong(entry.LogicalIndex, entry.Song);
        }
        else
        {
            _queueIndex = FindQueueIndex(entry.Song);
        }
        RefreshUpcomingItems();
        return result;
    }

    /// <summary>心动模式:沿队列方向找下一首红心(喜欢的)歌;队列里没有红心则退回列表循环。</summary>
    private async Task PlayLikedAsync(bool forward)
    {
        // 心动模式尚未开放给懒歌单；保持完整队列语义，退化为按方向取逻辑相邻歌曲。
        if (_lazyQueue is not null)
        {
            await PlayLazyAdjacentAsync(forward);
            return;
        }
        if (_queue.Count == 0) return;
        await _api.EnsureLikedIdsAsync(); // 已加载则幂等;未登录 → 空集合
        var candidates = new List<Song>();
        for (var i = 1; i <= _queue.Count; i++)
        {
            var idx = (forward ? _queueIndex + i : _queueIndex - i + _queue.Count) % _queue.Count;
            if (_api.IsLiked(_queue[idx].Id))
                candidates.Add(_queue[idx]);
        }
        foreach (var song in candidates)
        {
            var index = FindQueueIndex(song);
            if (index < 0) continue;
            if (!CanAttemptPlayback(song))
            {
                RemoveQueueEntryAt(index);
                continue;
            }
            var result = await TryPlayAsync(song);
            if (result == PlayAttemptResult.Started)
            {
                _queueIndex = FindQueueIndex(song);
                RefreshUpcomingItems();
                return;
            }
            if (result == PlayAttemptResult.TransientFailure) return;
            RemoveQueueEntryAt(index);
        }
        // 无红心 → 退回列表循环方向
        await PlayQueueAdjacentAsync(forward);
    }

    private async Task PlayLazyAdjacentAsync(bool forward)
    {
        var source = _lazyQueue;
        if (source is null) return;
        var ct = _queueRequestCancellation?.Token ?? CancellationToken.None;
        var originIndex = _queueIndex;
        try
        {
            var count = await source.GetCountAsync(ct);
            if (count <= 0) return;
            for (var step = 1; step <= count; step++)
            {
                var index = forward
                    ? (originIndex + step) % count
                    : (originIndex - step % count + count) % count;
                if (_excludedLazyIndices.Contains(index)) continue;
                var song = await source.GetSongAsync(index, ct);
                if (!ReferenceEquals(source, _lazyQueue)) return;
                if (song is null)
                {
                    _excludedLazyIndices.Add(index);
                    continue;
                }
                if (!CanAttemptPlayback(song))
                {
                    ExcludeLazyIndex(index);
                    continue;
                }
                var result = await TryPlayAsync(song);
                if (result == PlayAttemptResult.Unavailable)
                {
                    ExcludeLazyIndex(index);
                    continue;
                }
                if (result == PlayAttemptResult.TransientFailure) return;
                _queueIndex = index;
                RememberLazyDisplaySong(index, song);
                RefreshUpcomingItems();
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Message = $"加载播放队列失败:{ex.Message}";
        }
    }

    private void RememberLazyDisplaySong(int logicalIndex, Song song)
    {
        var existing = _lazyQueueVmIndices.IndexOf(logicalIndex);
        if (existing >= 0)
        {
            _queue[existing] = song;
            return;
        }
        _queue.Add(song);
        _queueVms.Add(new QueueItemViewModel(song, PlayQueueItem, RemoveFromQueue));
        _lazyQueueVmIndices.Add(logicalIndex);
    }

    private void EnsureShuffleContext()
    {
        if (_shuffleContextIsLazy != (_lazyQueue is not null)
            || !ReferenceEquals(_shuffleLazySource, _lazyQueue)
            || _shuffleHistory.Count == 0)
            ResetShuffleState();
    }

    private void ResetShuffleState()
    {
        _shuffleBag.Clear();
        _shuffleHistory.Clear();
        _shuffleHistoryPosition = -1;
        _shuffleContextIsLazy = _lazyQueue is not null;
        _shuffleLazySource = _lazyQueue;
        if (CurrentSong is null) return;

        var isQueueSong = _shuffleContextIsLazy
            ? IsCurrentLazyQueueSong()
            : FindQueueIndex(CurrentSong) >= 0;
        _shuffleHistory.Add(new ShuffleHistoryEntry(CurrentSong, _queueIndex, isQueueSong));
        _shuffleHistoryPosition = 0;
    }

    private bool IsCurrentLazyQueueSong()
    {
        if (CurrentSong is null || _queueIndex < 0 || _excludedLazyIndices.Contains(_queueIndex))
            return false;

        for (var displayIndex = 0; displayIndex < _lazyQueueVmIndices.Count; displayIndex++)
            if (_lazyQueueVmIndices[displayIndex] == _queueIndex
                && SameSong(_queue[displayIndex], CurrentSong))
                return true;
        return false;
    }

    private void CommitShuffleHistory(ShuffleHistoryEntry entry)
    {
        EnsureShuffleContext();
        if (_shuffleHistoryPosition + 1 < _shuffleHistory.Count)
            _shuffleHistory.RemoveRange(
                _shuffleHistoryPosition + 1,
                _shuffleHistory.Count - _shuffleHistoryPosition - 1);

        _shuffleHistory.Add(entry);
        _shuffleHistoryPosition = _shuffleHistory.Count - 1;
        if (_shuffleHistory.Count <= MaxShuffleHistoryCount) return;

        var remove = _shuffleHistory.Count - MaxShuffleHistoryCount;
        _shuffleHistory.RemoveRange(0, remove);
        _shuffleHistoryPosition -= remove;
    }

    private void RemoveShuffleHistoryAt(int index)
    {
        if ((uint)index >= (uint)_shuffleHistory.Count) return;
        _shuffleHistory.RemoveAt(index);
        if (index <= _shuffleHistoryPosition) _shuffleHistoryPosition--;
        _shuffleHistoryPosition = Math.Min(_shuffleHistoryPosition, _shuffleHistory.Count - 1);
    }

    private void RemoveShuffleHistoryWhere(Predicate<ShuffleHistoryEntry> predicate)
    {
        for (var index = _shuffleHistory.Count - 1; index >= 0; index--)
            if (predicate(_shuffleHistory[index]))
                RemoveShuffleHistoryAt(index);
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
        if (newState == PlaybackState.Idle) _playbackCache.Cancel();
        else _playbackCache.UpdatePlaying(IsPlaying);
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
        _playbackCache.Tick();
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
        MaybeCrossfadeAdvance(); // 交叉淡化:剩最后一段时提前切歌,新旧曲重叠渐变
    }

    // 交叉淡化自动切歌的一次会话标记:防止剩余窗口内反复触发(选歌失败时尤其吵)
    private bool _crossfadeAdvancing;

    /// <summary>交叉淡化:剩 ≤ 淡化时长时提前走切歌流程(真实重叠需要新旧两个实例同时发声)。
    /// 只在"下一首是不同的歌"时提前切 —— 单曲循环/单曲队列重播同一首,重叠是回声不是淡化,
    /// 让它自然播完走普通切换。播放中把进度 Seek 进末段同样会触发(语义一致:剩 N 秒就淡出)。</summary>
    private void MaybeCrossfadeAdvance()
    {
        if (_crossfadeAdvancing || _advancing > 0) return;
        if (!_appState.Crossfade || !_player.SupportsCrossfade) return;
        if (CurrentSong is null || !IsPlaying || _scrubbing) return;
        var fadeMs = CrossfadeMs();
        if (fadeMs <= 0 || DurationMs <= 0) return;
        var remaining = DurationMs - PositionMs;
        if (remaining > fadeMs || remaining <= 0) return;
        if (PlaybackMode == PlaybackMode.SingleLoop) return;
        if (!IsFmActive && _lazyQueue is null && _queue.Count <= 1) return;

        _crossfadeAdvancing = true;
        _ = AdvanceWithCrossfadeAsync();
    }

    private async Task AdvanceWithCrossfadeAsync()
    {
        await PlayNextAsync();
        // 刻意不在收尾重置 _crossfadeAdvancing:本曲剩余窗口内不重试(选歌失败时尤其吵);
        // 新歌起播由 PrepareSongPlayback 重置,选歌失败则留给"自然播完 → Idle"的普通切歌路径。
    }

    private void OnDurationChanged(object? sender, long value) => DurationMs = value;

    private void OnError(object? sender, string message)
    {
        _playbackCache.Cancel();
        Message = message;
    }

    private async Task LoadCoverAsync(Song song)
    {
        // 歌单行通常已加载 100px 封面：先立即复用，随后在线请求 640px 成功时再无缝替换。
        // 以 Song 引用校验异步结果，防止快速切歌后迟到封面覆盖当前歌曲。
        var fallback = CoverLoader.TryGetLoadedVariant(song.CoverUrl);
        if (ReferenceEquals(CurrentSong, song)) Cover = fallback;
        var large = await CoverLoader.LoadAsync(song.CoverUrl, 640);
        if (ReferenceEquals(CurrentSong, song) && large is not null)
            Cover = large;
    }

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
        _playbackCache.Dispose();
        _musicCacheLease?.Dispose();
        _musicCacheLease = null;
        ResetLazyQueue();
        _smtc.PlayPauseRequested -= OnSmtcPlayPause;
        _smtc.SeekRequested -= OnSmtcSeek;
        _player.StateChanged -= OnStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.DurationChanged -= OnDurationChanged;
        _player.ErrorOccurred -= OnError;
    }
}
