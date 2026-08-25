using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>首页推荐卡片(200x250):封面 + 标题 + 副标题 + 右上播放量角标。封面后台加载。
/// activate 为点击行为:歌单卡片→打开歌单页,歌曲卡片→播放(队列=所在区块全部歌);null 不可点。</summary>
public sealed partial class RecommendCardViewModel : ViewModelBase
{
    private readonly string _coverUrl;
    private readonly Func<Task>? _activate;
    private bool _coverRequested;
    private bool _coverRevealed;

    public RecommendCardViewModel(string title, string subtitle, string coverUrl = "", long playCount = 0, long id = 0, Func<Task>? activate = null)
    {
        Title = title;
        Subtitle = subtitle;
        _coverUrl = coverUrl;
        PlayCount = playCount;
        Id = id;
        _activate = activate;
    }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>歌单 id 或歌曲 id(仅点击行为用)。</summary>
    public long Id { get; }

    /// <summary>点击卡片(歌单→歌单页;歌曲→播放)。</summary>
    [RelayCommand]
    private Task OpenAsync() => _activate?.Invoke() ?? Task.CompletedTask;

    /// <summary>播放量(歌曲卡片为 0,歌单卡片才有)。</summary>
    public long PlayCount { get; }

    public bool HasPlayCount => PlayCount > 0;

    public string PlayCountText => HasPlayCount ? NetEaseApiClient.FormatPlayCount(PlayCount) : "";

    /// <summary>容器 realized 时调用:首次才拉封面(幂等)。</summary>
    public void EnsureCoverLoaded()
    {
        if (_coverRequested || Cover is not null) return;
        _coverRequested = true;
        if (string.IsNullOrEmpty(_coverUrl)) return;
        _ = LoadCoverAsync();
    }

    /// <summary>后台加载完成的真实封面(240px)。</summary>
    [ObservableProperty] private IImage? _cover;

    /// <summary>视图实际显示的封面:过渡期间保持 null(→共享占位图),过渡结束后才换成真实封面。
    /// 若 30 张位图都绑在首帧渲染,切页动画第一帧会被位图绘制卡死(实测 ~250ms),所以延后亮出。</summary>
    [ObservableProperty] private IImage? _displayCover;

    private async Task LoadCoverAsync() => Cover = await CoverLoader.LoadAsync(_coverUrl, 240);

    partial void OnCoverChanged(IImage? value)
    {
        // 封面后台加载完成(首次进入,晚于切页):本轮过渡已结束(_coverRevealed)就直接亮出,
        // 未结束则等 Background 翻转任务统一处理(任务里读的是最新 Cover)。
        if (value is not null && _coverRevealed) DisplayCover = value;
    }

    /// <summary>每次容器实化(每次挂树)调用:先把显示封面重置回占位图,再安排过渡结束后亮出真实封面。
    /// 若 30 张位图直接参与首帧渲染,切页动画第一帧会被卡死(实测旧行为 ~300ms);
    /// 每次切回都重置,保证"切回来"同样走延后路径(即使封面已缓存/被淘汰)。</summary>
    public void PrepareCoverForTransition()
    {
        DisplayCover = null; // 本轮首帧回到共享占位图(首轮本来就是 null,无害;后续轮次清除已亮出的封面)
        _coverRevealed = false;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _coverRevealed = true;
            if (DisplayCover is null) DisplayCover = Cover;
        }, Avalonia.Threading.DispatcherPriority.Background);
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

    /// <summary>是否套圆角边框容器(仿原版 DailyMix 的 HorizontalScrollableGridView)。</summary>
    public bool IsBordered { get; }

    public bool HasPlayAll => _playAll is not null;

    /// <summary>播放全部(每日歌曲区块:第一首 + 全量队列)。</summary>
    [RelayCommand]
    private Task PlayAllAsync() => _playAll?.Invoke() ?? Task.CompletedTask;
}

/// <summary>首页(每日歌曲推荐/推荐歌单/热门歌曲/猜你喜欢)。进入首页时从真实 API 拉各区块;
/// 单区块失败不影响其他;每日歌曲推荐需登录,未登录时不显示该区块。</summary>
public sealed class RecommendViewModel : ViewModelBase
{
    /// <summary>热歌榜歌单 id(热门歌曲区块数据源)。</summary>
    private const long HotPlaylistId = 3778678;

    private readonly NetEaseApiClient _api;
    private readonly PlayerViewModel _player;
    private readonly DispatcherService _dispatcher;
    private bool _loading;
    private bool _loaded;
    private bool _lastWasLoggedIn;

    public RecommendViewModel(NetEaseApiClient api, DispatcherService dispatcher, PlayerViewModel player)
    {
        _api = api;
        _dispatcher = dispatcher;
        _player = player;
        Sections = new ObservableCollection<RecommendSectionViewModel>();
    }

    public ObservableCollection<RecommendSectionViewModel> Sections { get; }

    /// <summary>进入首页时调用:首载或登录态变化时刷新(幂等;网络失败静默保持现状)。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loading) return;
        var loggedIn = _api.IsLoggedIn;
        if (_loaded && loggedIn == _lastWasLoggedIn) return;
        _lastWasLoggedIn = loggedIn;
        _loading = true;
        try
        {
            await LoadAllSectionsAsync(loggedIn).ConfigureAwait(false);
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

    /// <summary>并发拉取各区块(各自容错),完成后一次性替换 Section 列表避免逐个闪烁。</summary>
    private async Task LoadAllSectionsAsync(bool loggedIn)
    {
        var playlistsTask = SafeAsync(_api.GetPersonalizedPlaylistsAsync(6));
        var hotTask = SafeAsync(LoadHotSongsAsync());
        var newSongsTask = SafeAsync(LoadNewSongsAsync());
        var dailyTask = loggedIn ? SafeAsync(LoadDailyItemsAsync()) : Task.FromResult(new List<object>());
        await Task.WhenAll(playlistsTask, hotTask, newSongsTask, dailyTask).ConfigureAwait(false);

        var sections = new List<RecommendSectionViewModel>();
        var daily = await dailyTask.ConfigureAwait(false);
        var playlists = await playlistsTask.ConfigureAwait(false);
        var hot = await hotTask.ConfigureAwait(false);
        var newSongs = await newSongsTask.ConfigureAwait(false);
        if (daily.Count > 0)
        {
            // 每日歌曲推荐:播全部 = 第一首 + 全量队列(仅当是歌曲行时;兜底歌单卡片无此按钮)
            var dailySongs = daily.OfType<SongItemViewModel>().Select(s => s.Song).ToList();
            Func<Task>? playAll = null;
            if (dailySongs.Count > 0)
            {
                var queue = dailySongs;
                playAll = () => _player.PlayFromList(queue[0], queue, "每日歌曲推荐");
            }
            sections.Add(new RecommendSectionViewModel("每日歌曲推荐", daily, isBordered: true, playAll));
        }
        if (playlists.Count > 0) sections.Add(new RecommendSectionViewModel("推荐歌单", playlists.Select(ToPlaylistCard).ToList()));
        if (hot.Count > 0) sections.Add(new RecommendSectionViewModel("热门歌曲", ToSongCards(hot, "热门歌曲")));
        if (newSongs.Count > 0) sections.Add(new RecommendSectionViewModel("猜你喜欢", ToSongCards(newSongs, "猜你喜欢")));

        // 网络回调在线程池,集合更新必须回 UI 线程
        await _dispatcher.InvokeAsync(() =>
        {
            Sections.Clear();
            foreach (var s in sections) Sections.Add(s);
        }).ConfigureAwait(false);
    }

    /// <summary>每日歌曲推荐区块:优先取每日歌曲(行),接口不可用时兜底为每日推荐歌单(卡片)。
    /// 每行都带全量每日歌曲队列:点任意一行播放,上一曲/下一曲/播完自动切都在每日推荐列表内进行。</summary>
    private async Task<List<object>> LoadDailyItemsAsync()
    {
        var songs = await _api.GetDailyRecommendSongsAsync().ConfigureAwait(false);
        if (songs.Count > 0)
            return songs.Select(s => (object)new SongItemViewModel(s, _player.PlayFromList, queue: songs, api: _api, source: "每日歌曲推荐")).ToList();

        var playlists = await _api.GetDailyRecommendAsync().ConfigureAwait(false);
        return playlists.Select(ToPlaylistCard).ToList();
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
