using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>首页推荐卡片(200x250):封面 + 标题 + 副标题 + 右上播放量角标。封面后台加载。
/// activate 为点击行为:歌单卡片→打开歌单页,歌曲卡片→播放(队列=所在区块全部歌);null 不可点。</summary>
public sealed partial class RecommendCardViewModel : ViewModelBase
{
    private static readonly TimeSpan CoverTransitionDelay = TimeSpan.FromMilliseconds(320);
    private readonly Func<Task>? _activate;
    private bool _coverRequestScheduled;

    public RecommendCardViewModel(string title, string subtitle, string coverUrl = "", long playCount = 0, long id = 0, Func<Task>? activate = null)
    {
        Title = title;
        Subtitle = subtitle;
        CoverUrl = coverUrl;
        PlayCount = playCount;
        Id = id;
        _activate = activate;
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string CoverUrl { get; }

    /// <summary>歌单 id 或歌曲 id(仅点击行为用)。</summary>
    public long Id { get; }

    /// <summary>点击卡片(歌单→歌单页;歌曲→播放)。</summary>
    [RelayCommand]
    private Task OpenAsync() => _activate?.Invoke() ?? Task.CompletedTask;

    /// <summary>播放量(歌曲卡片为 0,歌单卡片才有)。</summary>
    public long PlayCount { get; }

    public bool HasPlayCount => PlayCount > 0;

    public string PlayCountText => HasPlayCount ? NetEaseApiClient.FormatPlayCount(PlayCount) : "";

    /// <summary>视图实际请求的封面 URL:过渡期间保持 null(→占位底色),过渡结束后才开始加载。
    /// 若 30 张位图都绑在首帧渲染,切页动画第一帧会被位图绘制卡死(实测 ~250ms),所以延后亮出。</summary>
    [ObservableProperty] private string? _displayCoverUrl;

    /// <summary>卡片第一次实化时延迟到页面过渡结束再请求封面。
    /// 已经显示过的卡片保持 URL，不在返回首页时先清空再排队；否则持续渲染会让
    /// Background 队列长期饥饿，出现首页封面几十秒后才一起恢复。</summary>
    public void PrepareCoverForTransition()
    {
        if (_coverRequestScheduled
            || string.Equals(DisplayCoverUrl, CoverUrl, StringComparison.Ordinal))
            return;

        _coverRequestScheduled = true;
        _ = CommitCoverAfterTransitionAsync();
    }

    private async Task CommitCoverAfterTransitionAsync()
    {
        await Task.Delay(CoverTransitionDelay).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _coverRequestScheduled = false;
            DisplayCoverUrl = CoverUrl;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
}

/// <summary>首页推荐区块:标题 + 横向列表。元素可为卡片(RecommendCardViewModel)或每日歌曲行(SongItemViewModel),
/// 由隐式 DataTemplate 按类型选择模板。每日区块可携带"播放全部"。</summary>
public sealed partial class RecommendSectionViewModel : ViewModelBase
{
    private readonly Func<Task>? _playAll;

    public RecommendSectionViewModel(string title, IReadOnlyList<object> items, bool isBordered = false, Func<Task>? playAll = null)
    {
        Title = title;
        Items = new ObservableCollection<object>(items);
        IsBordered = isBordered;
        _playAll = playAll;
    }

    public string Title { get; }

    public ObservableCollection<object> Items { get; }

    /// <summary>登录态变化后重算区块内歌曲行的可播性。</summary>
    public void RefreshPlayability()
    {
        foreach (var item in Items)
            if (item is SongItemViewModel song) song.RefreshPlayability();
    }

    /// <summary>是否套圆角边框容器(仿原版 DailyMix 的 HorizontalScrollableGridView)。</summary>
    public bool IsBordered { get; }

    public bool HasPlayAll => _playAll is not null;

    /// <summary>播放全部(每日歌曲区块:第一首 + 全量队列)。</summary>
    [RelayCommand]
    private Task PlayAllAsync() => _playAll?.Invoke() ?? Task.CompletedTask;
}

/// <summary>首页(每日歌曲推荐/推荐歌单/热门歌曲/猜你喜欢)。进入首页时从真实 API 拉各区块;
/// 单区块失败不影响其他;每日歌曲推荐合并网易云与 QQ 音乐两源(各自需对应登录,全空时不显示该区块)。</summary>
public sealed class RecommendViewModel : ViewModelBase
{
    /// <summary>热歌榜歌单 id(热门歌曲区块数据源)。</summary>
    private const long HotPlaylistId = 3778678;

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qq;
    private readonly PlayerViewModel _player;
    private readonly DispatcherService _dispatcher;
    private bool _loading;
    private bool _loaded;

    /// <summary>上次加载时的双端登录态:任一端登录/登出都触发首页刷新。</summary>
    private (bool Ne, bool Qq) _lastLoginState;

    public RecommendViewModel(NetEaseApiClient api, QQMusicApiClient qq, DispatcherService dispatcher, PlayerViewModel player)
    {
        _api = api;
        _qq = qq;
        _dispatcher = dispatcher;
        _player = player;
        Sections = new ObservableCollection<RecommendSectionViewModel>();
    }

    public ObservableCollection<RecommendSectionViewModel> Sections { get; }

    /// <summary>登录态变化后重算各区块歌曲行的可播性(整页刷新前先恢复/禁用,立即生效)。</summary>
    public void RefreshPlayability()
    {
        foreach (var section in Sections) section.RefreshPlayability();
    }

    /// <summary>进入首页时调用:首载或任一音源登录态变化时刷新(幂等;网络失败静默保持现状)。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loading) return;
        var state = (_api.IsLoggedIn, _qq.IsLoggedIn);
        if (_loaded && state == _lastLoginState) return;
        _lastLoginState = state;
        _loading = true;
        try
        {
            await LoadAllSectionsAsync().ConfigureAwait(false);
            _loaded = true;
        }
        catch
        {
            // 网络失败静默:保持已有内容,不崩
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>并发拉取各区块(各自容错);数据一到即上屏(固定顺序逐个插入),不等最慢的区块整体刷新。
    /// 首个非空区块就绪时才清空旧内容:全部失败则保留旧内容不闪空。</summary>
    private async Task LoadAllSectionsAsync()
    {
        var dailyTask = SafeAsync(LoadDailyItemsAsync());
        var playlistsTask = SafeAsync(_api.GetPersonalizedPlaylistsAsync(6));
        var hotTask = SafeAsync(LoadHotSongsAsync());
        var newSongsTask = SafeAsync(LoadNewSongsAsync());

        var cleared = false;
        async Task EnsureClearedAsync()
        {
            if (cleared) return;
            await _dispatcher.InvokeAsync(() => Sections.Clear()).ConfigureAwait(false);
            cleared = true;
        }
        async Task AddAsync(string title, IReadOnlyList<object> items, bool isBordered = false, Func<Task>? playAll = null)
        {
            await EnsureClearedAsync().ConfigureAwait(false);
            var section = new RecommendSectionViewModel(title, items, isBordered, playAll);
            await _dispatcher.InvokeAsync(() => Sections.Add(section)).ConfigureAwait(false);
        }

        // 每日歌曲推荐:播全部 = 第一首 + 全量队列(仅歌曲行;兜底歌单卡片无此按钮)
        var daily = await dailyTask.ConfigureAwait(false);
        if (daily.Count > 0)
        {
            var dailyRows = daily.OfType<SongItemViewModel>().ToList();
            var dailySongs = dailyRows.Select(row => row.Song).ToList();
            var firstPlayable = dailyRows.FirstOrDefault(row => row.IsPlayable)?.Song;
            Func<Task>? playAll = firstPlayable is not null
                ? () => _player.PlayFromList(firstPlayable, dailySongs, "每日歌曲推荐")
                : null;
            await AddAsync("每日歌曲推荐", daily, isBordered: true, playAll).ConfigureAwait(false);
        }

        var playlists = await playlistsTask.ConfigureAwait(false);
        if (playlists.Count > 0) await AddAsync("推荐歌单", playlists.Select(ToPlaylistCard).ToList()).ConfigureAwait(false);

        var hot = await hotTask.ConfigureAwait(false);
        if (hot.Count > 0) await AddAsync("热门歌曲", ToSongCards(hot, "热门歌曲")).ConfigureAwait(false);

        var newSongs = await newSongsTask.ConfigureAwait(false);
        if (newSongs.Count > 0) await AddAsync("猜你喜欢", ToSongCards(newSongs, "猜你喜欢")).ConfigureAwait(false);
    }

    /// <summary>每日歌曲推荐区块:网易云与 QQ 音乐的每日推荐并行拉取,合并进同一个容器(网易云在前)。
    /// 每行都带全量合并队列:点任意一行播放,上一曲/下一曲/播完自动切跨两源连续进行。
    /// 两源皆空且网易云已登录时,兜底为网易云每日推荐歌单(卡片);单源失败不影响另一源。</summary>
    private async Task<List<object>> LoadDailyItemsAsync()
    {
        var neTask = _api.IsLoggedIn ? SafeAsync(_api.GetDailyRecommendSongsAsync()) : Task.FromResult(new List<Song>());
        var qqTask = _qq.IsLoggedIn ? SafeAsync(_qq.GetDailyRecommendSongsAsync()) : Task.FromResult(new List<Song>());
        await Task.WhenAll(neTask, qqTask).ConfigureAwait(false);

        var queue = (await neTask.ConfigureAwait(false)).Concat(await qqTask.ConfigureAwait(false)).ToList();
        if (queue.Count > 0)
            return queue.Select(s => (object)new SongItemViewModel(s, _player.PlayFromList,
                queue: queue, api: _api, source: "每日歌曲推荐")).ToList();

        // 兜底:网易云每日推荐歌单卡片(需登录;未登录接口回 code 301 → 空列表)
        if (_api.IsLoggedIn)
            return (await _api.GetDailyRecommendAsync().ConfigureAwait(false)).Select(ToPlaylistCard).ToList();
        return new();
    }

    /// <summary>热门歌曲区块:热歌榜前 6 首完整曲目(点卡片播放需要完整 Song)。</summary>
    private async Task<List<Song>> LoadHotSongsAsync()
    {
        // 只取前 6 首预览,不拉全量 200+ 首(避免启动时白拉 194 首)
        return await _api.GetPlaylistTracksAsync(HotPlaylistId, 6).ConfigureAwait(false);
    }

    /// <summary>猜你喜欢区块:newsong 接口只给卡片信息,按 id 批量补全 Song(专辑/时长/歌手 id);
    /// 补全失败时退化为仅含播放必需字段的最小 Song(卡片仍可点播)。</summary>
    private async Task<List<Song>> LoadNewSongsAsync()
    {
        var items = await _api.GetNewSongsAsync(6).ConfigureAwait(false);
        if (items.Count == 0) return new();
        var ids = items.Select(i => i.Id).ToList();
        var byId = (await SafeAsync(_api.GetSongsByIdsAsync(ids)).ConfigureAwait(false))
            .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        return items
            .Select(i => byId.TryGetValue(i.Id, out var s)
                ? s
                : new Song { Id = i.Id, Name = i.Title, Artist = i.Subtitle, CoverUrl = i.CoverUrl })
            .ToList();
    }

    /// <summary>推荐歌单卡片:点击打开歌单详情(临时构造 PlaylistItemViewModel,复用"我的收藏"页的歌单加载)。</summary>
    private object ToPlaylistCard(RecommendItem i)
        => new RecommendCardViewModel(i.Title, i.Subtitle, i.CoverUrl, i.PlayCount, i.Id, () => OpenPlaylistAsync(i));

    /// <summary>歌曲卡片(热门歌曲/猜你喜欢):点击播放该歌,队列 = 本区块全部歌(上一曲/下一曲在区块内切换)。</summary>
    private List<object> ToSongCards(List<Song> songs, string source)
        => songs.Select(s => (object)new RecommendCardViewModel(s.Name, s.Artist, s.CoverUrl, 0, s.Id,
            () => _player.PlayFromList(s, songs, source))).ToList();

    private Task OpenPlaylistAsync(RecommendItem item)
    {
        var playlist = new PlaylistItemViewModel(new Playlist
        {
            Id = item.Id,
            Name = item.Title,
            CoverUrl = item.CoverUrl,
            TrackCount = item.TrackCount,
        });
        ServiceLocator.Get<MainViewModel>().OpenShellPlaylistCommand.Execute(playlist);
        return Task.CompletedTask;
    }

    private static async Task<List<T>> SafeAsync<T>(Task<List<T>> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return new(); }
    }
}
