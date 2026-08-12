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

/// <summary>首页推荐卡片(200x250):封面 + 标题 + 副标题 + 右上播放量角标。封面后台加载。</summary>
public sealed partial class RecommendCardViewModel : ViewModelBase
{
    private readonly string _coverUrl;
    private bool _coverRequested;

    public RecommendCardViewModel(string title, string subtitle, string coverUrl = "", long playCount = 0)
    {
        Title = title;
        Subtitle = subtitle;
        _coverUrl = coverUrl;
        PlayCount = playCount;
    }

    public string Title { get; }

    public string Subtitle { get; }

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

    [ObservableProperty] private IImage? _cover;

    private async Task LoadCoverAsync() => Cover = await CoverLoader.LoadAsync(_coverUrl, 240);
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
        var newSongsTask = SafeAsync(_api.GetNewSongsAsync(6));
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
                playAll = () => _player.PlayFromList(queue[0], queue);
            }
            sections.Add(new RecommendSectionViewModel("每日歌曲推荐", daily, isBordered: true, playAll));
        }
        if (playlists.Count > 0) sections.Add(new RecommendSectionViewModel("推荐歌单", playlists.Select(ToCard).ToList()));
        if (hot.Count > 0) sections.Add(new RecommendSectionViewModel("热门歌曲", hot.Select(ToCard).ToList()));
        if (newSongs.Count > 0) sections.Add(new RecommendSectionViewModel("猜你喜欢", newSongs.Select(ToCard).ToList()));

        // 网络回调在线程池,集合更新必须回 UI 线程
        await _dispatcher.InvokeAsync(() =>
        {
            Sections.Clear();
            foreach (var s in sections) Sections.Add(s);
        }).ConfigureAwait(false);
    }

    /// <summary>每日歌曲推荐区块:优先取每日歌曲(行),接口不可用时兜底为每日推荐歌单(卡片)。</summary>
    private async Task<List<object>> LoadDailyItemsAsync()
    {
        var songs = await _api.GetDailyRecommendSongsAsync().ConfigureAwait(false);
        if (songs.Count > 0)
            return songs.Select(s => (object)new SongItemViewModel(s, _player.PlayFromList, api: _api)).ToList();

        var playlists = await _api.GetDailyRecommendAsync().ConfigureAwait(false);
        return playlists.Select(ToCard).ToList();
    }

    private async Task<List<RecommendItem>> LoadHotSongsAsync()
    {
        // 只取前 6 首预览,不拉全量 200+ 首(避免启动时白拉 194 首)
        var songs = await _api.GetPlaylistTracksAsync(HotPlaylistId, 6).ConfigureAwait(false);
        return songs.Select(s => new RecommendItem(s.Id, s.Name, s.Artist, s.CoverUrl)).ToList();
    }

    private static object ToCard(RecommendItem i) => new RecommendCardViewModel(i.Title, i.Subtitle, i.CoverUrl, i.PlayCount);

    private static async Task<List<T>> SafeAsync<T>(Task<List<T>> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return new(); }
    }
}
