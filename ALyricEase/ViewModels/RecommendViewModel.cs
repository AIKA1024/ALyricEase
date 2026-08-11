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

namespace ALyricEase.ViewModels;

/// <summary>首页推荐卡(160x200):封面 + 标题 + 描述。封面后台加载。</summary>
public sealed partial class RecommendCardViewModel : ViewModelBase
{
    private readonly string _coverUrl;
    private bool _coverRequested;

    public RecommendCardViewModel(string title, string subtitle, string coverUrl = "")
    {
        Title = title;
        Subtitle = subtitle;
        _coverUrl = coverUrl;
    }

    public string Title { get; }

    public string Subtitle { get; }

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

/// <summary>首页推荐区块:标题 + 横向卡片列表。</summary>
public sealed partial class RecommendSectionViewModel : ViewModelBase
{
    public RecommendSectionViewModel(string title, IReadOnlyList<RecommendCardViewModel> cards)
    {
        Title = title;
        Cards = new ObservableCollection<RecommendCardViewModel>(cards);
    }

    public string Title { get; }

    public ObservableCollection<RecommendCardViewModel> Cards { get; }
}

/// <summary>首页(每日推荐/推荐歌单/热门歌曲/猜你喜欢)。进入首页时从真实 API 拉各区块;
/// 单区块失败不影响其他;每日推荐需登录,未登录时不显示该区块。</summary>
public sealed class RecommendViewModel : ViewModelBase
{
    /// <summary>热歌榜歌单 id(热门歌曲区块数据源)。</summary>
    private const long HotPlaylistId = 3778678;

    private readonly NetEaseApiClient _api;
    private readonly DispatcherService _dispatcher;
    private bool _loading;
    private bool _loaded;
    private bool _lastWasLoggedIn;

    public RecommendViewModel(NetEaseApiClient api, DispatcherService dispatcher)
    {
        _api = api;
        _dispatcher = dispatcher;
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
        var dailyTask = loggedIn ? SafeAsync(_api.GetDailyRecommendAsync()) : Task.FromResult(new List<RecommendItem>());
        await Task.WhenAll(playlistsTask, hotTask, newSongsTask, dailyTask).ConfigureAwait(false);

        var sections = new List<RecommendSectionViewModel>();
        var daily = await dailyTask.ConfigureAwait(false);
        var playlists = await playlistsTask.ConfigureAwait(false);
        var hot = await hotTask.ConfigureAwait(false);
        var newSongs = await newSongsTask.ConfigureAwait(false);
        if (daily.Count > 0) sections.Add(ToSection("每日推荐", daily));
        if (playlists.Count > 0) sections.Add(ToSection("推荐歌单", playlists));
        if (hot.Count > 0) sections.Add(ToSection("热门歌曲", hot));
        if (newSongs.Count > 0) sections.Add(ToSection("猜你喜欢", newSongs));

        // 网络回调在线程池,集合更新必须回 UI 线程
        await _dispatcher.InvokeAsync(() =>
        {
            Sections.Clear();
            foreach (var s in sections) Sections.Add(s);
        }).ConfigureAwait(false);
    }

    private async Task<List<RecommendItem>> LoadHotSongsAsync()
    {
        // 只取前 6 首预览,不拉全量 200+ 首(避免启动时白拉 194 首)
        var songs = await _api.GetPlaylistTracksAsync(HotPlaylistId, 6).ConfigureAwait(false);
        return songs.Select(s => new RecommendItem(s.Id, s.Name, s.Artist, s.CoverUrl)).ToList();
    }

    private static async Task<List<RecommendItem>> SafeAsync(Task<List<RecommendItem>> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return new(); }
    }

    private static RecommendSectionViewModel ToSection(string title, IReadOnlyList<RecommendItem> items)
        => new(title, items.Select(i => new RecommendCardViewModel(i.Title, i.Subtitle, i.CoverUrl)).ToList());
}
