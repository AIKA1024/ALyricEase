using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手全部歌曲页("热门歌曲·查看更多"入口):服务端分页流式加载,滚动近底部增量补页,
/// 歌曲行封面/红心由视图容器 realized 时懒加载。网易云 /api/v1/artist/songs 与 QQ
/// GetSingerSongList 两套分页在此统一。</summary>
public sealed partial class ArtistSongsPageViewModel : NavigationDetailViewModelBase
{
    private const int PageSize = 50;

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly PlayerViewModel _player;
    private readonly MusicCacheService _musicCache;

    private ArtistPageRef? _ref;
    private int _offset;
    private int _total = -1; // 服务端总数;-1 = 未知(按页长判断是否还有下一页)
    private readonly List<Song> _loaded = new();
    private readonly List<SongItemViewModel> _allSongRows = new();
    private bool _loading;
    private Task? _loadTask;
    private Task? _loadAllForFilterTask;
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;

    public ArtistSongsPageViewModel(
        NetEaseApiClient api,
        QQMusicApiClient qqApi,
        PlayerViewModel player,
        MusicCacheService musicCache)
    {
        _api = api;
        _qqApi = qqApi;
        _player = player;
        _musicCache = musicCache;
        Filters = CollectionSortAndFilterViewModel.ForTracks("在歌曲中搜索");
        Filters.FilterChanged += OnFiltersChanged;
    }

    public RangeObservableCollection<SongItemViewModel> Songs { get; } = new();

    public CollectionSortAndFilterViewModel Filters { get; }

    [ObservableProperty] private string _name = "";

    /// <summary>热门歌曲总数；服务端未返回总数时显示当前已获取数量。</summary>
    [ObservableProperty] private string _subtitle = "";

    [ObservableProperty] private bool _hasSongs;

    /// <summary>还有下一页(加载失败也会置 false,避免滚动到底无限重试)。</summary>
    [ObservableProperty] private bool _hasMore;

    /// <summary>增量补页进行中(页尾加载指示)。</summary>
    [ObservableProperty] private bool _isLoadingMore;

    /// <summary>进入页面时调用:清掉上一位歌手的内容,拉首页。</summary>
    public async Task LoadAsync(ArtistPageRef artistRef)
    {
        BeginPageLoad();
        _ref = artistRef;
        Name = artistRef.Name;
        Songs.Clear();
        _allSongRows.Clear();
        _loaded.Clear();
        Filters.Reset();
        _offset = 0;
        _total = -1;
        HasSongs = false;
        IsLoadingMore = false;
        HasMore = true;
        Subtitle = "加载中…";
        await LoadMoreAsync();
    }

    /// <summary>增量补一页(单飞:在途时返回同一任务)。滚动近底部由视图调用。</summary>
    public Task LoadMoreAsync()
    {
        if (_ref is null || _loadCancellation is null || !HasMore || _loading)
            return _loadTask ?? Task.CompletedTask;
        _loading = true;
        IsLoadingMore = true;
        return _loadTask = LoadPageCoreAsync(_loadGeneration, _loadCancellation.Token);
    }

    private async Task LoadPageCoreAsync(int generation, CancellationToken ct)
    {
        var hasMore = false;
        try
        {
            IReadOnlyList<Song> page;
            if (_ref!.IsQq)
            {
                var (qqPage, qqTotal) = await _qqApi.GetArtistSongPageAsync(
                    _ref.QqMid, _offset, PageSize, ct);
                if (!IsCurrentLoad(generation, ct)) return;
                if (qqTotal > 0) _total = qqTotal;
                page = qqPage;
                // QQ 总数不下发时按满页判断还有下一页
                hasMore = page.Count > 0 && _offset + page.Count < (_total > 0 ? _total : int.MaxValue);
            }
            else
            {
                var (nePage, neTotal, neMore) = await _api.GetArtistSongPageAsync(
                    _ref.NetEaseId, PageSize, _offset, ct);
                if (!IsCurrentLoad(generation, ct)) return;
                if (neTotal > 0) _total = neTotal;
                page = nePage;
                hasMore = neMore;
            }

            if (page.Count > 0)
            {
                _loaded.AddRange(page);
                _offset += page.Count;
                var firstIndex = _allSongRows.Count + 1;
                for (var i = 0; i < page.Count; i++)
                    _allSongRows.Add(new SongItemViewModel(
                        page[i], _player.PlayFromList, index: firstIndex + i, queue: _loaded,
                        api: _ref.IsQq ? null : _api, source: _ref.Name));
                RefreshVisibleSongs();
                HasSongs = true;
            }
            Subtitle = $"共 {(_total > 0 ? _total : _allSongRows.Count)} 首";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // 网络失败:停止续拉(已加载内容保留),用户滚到底部不再重复请求
        }
        finally
        {
            if (IsCurrentLoad(generation, ct))
            {
                HasMore = hasMore;
                _loading = false;
                IsLoadingMore = false;
                _loadTask = null;
            }
        }
    }

    internal DetailNavigationSnapshot? CaptureAndReleaseNavigationSnapshot()
    {
        if (_ref is null)
        {
            ReleaseCurrentPageData();
            return null;
        }

        var artistRef = _ref;
        var wasLoading = _loading;
        var cacheKey = Guid.NewGuid().ToString("N");
        var snapshot = new DetailNavigationSnapshot(
            cacheKey,
            DetailPageKind.ArtistSongs,
            artistRef.Source,
            artistRef.NetEaseId,
            artistRef.QqMid,
            artistRef.Name,
            Filters.SelectedSortIndex,
            Filters.SearchText,
            Filters.IsExpanded,
            PageScrollOffset);
        CancelCurrentLoad();

        if (_allSongRows.Count > 0 || !wasLoading)
        {
            _ = _musicCache.CacheDetailPageSnapshotAsync(
                cacheKey,
                new DetailPageCacheData(
                    _allSongRows.Select(ToCacheTrack).ToList(),
                    [],
                    [],
                    Name: Name,
                    Subtitle: Subtitle,
                    Offset: _offset,
                    Total: _total,
                    HasMore: HasMore));
        }

        ReleaseCurrentPageData();
        return snapshot;
    }

    internal void ReleaseCurrentPageData()
    {
        CancelCurrentLoad();
        _ref = null;
        Songs.Clear();
        _allSongRows.Clear();
        _loaded.Clear();
        Filters.Reset();
        _offset = 0;
        _total = -1;
        HasSongs = false;
        HasMore = false;
        IsLoadingMore = false;
        Name = "";
        Subtitle = "";
        ResetPageScrollState();
    }

    internal async Task RestoreNavigationSnapshotAsync(DetailNavigationSnapshot snapshot)
    {
        BeginPageLoad();
        var generation = _loadGeneration;
        var ct = _loadCancellation!.Token;
        var artistRef = new ArtistPageRef(
            snapshot.Source, snapshot.Id, snapshot.Mid, snapshot.Name);
        _ref = artistRef;
        Name = snapshot.Name;
        Songs.Clear();
        _allSongRows.Clear();
        _loaded.Clear();
        Filters.Reset();
        _offset = 0;
        _total = -1;
        HasSongs = false;
        HasMore = false;
        IsLoadingMore = false;

        var cached = await _musicCache.TryTakeDetailPageSnapshotAsync(snapshot.CacheKey);
        if (!IsCurrentLoad(generation, ct)) return;
        if (cached is null)
        {
            var fallbackLoad = LoadAsync(artistRef);
            var fallbackGeneration = _loadGeneration;
            await fallbackLoad;
            if (fallbackGeneration != _loadGeneration) return;
            Filters.RestoreState(
                snapshot.SelectedSortIndex, snapshot.SearchText, snapshot.IsFilterExpanded);
            RestorePageScrollState(snapshot.ScrollOffset);
            return;
        }

        Name = cached.Name;
        Subtitle = cached.Subtitle;
        _offset = Math.Max(0, cached.Offset);
        _total = cached.Total;
        HasMore = cached.HasMore;
        foreach (var track in cached.Tracks)
        {
            RestoreSongFlags(track);
            _loaded.Add(track.Song);
        }
        for (var index = 0; index < cached.Tracks.Count; index++)
        {
            var track = cached.Tracks[index];
            var row = new SongItemViewModel(
                track.Song,
                _player.PlayFromList,
                index + 1,
                _loaded,
                track.Song.Source == MusicSource.NetEase ? _api : null,
                Name);
            row.IsPlayable = track.IsPlayable;
            _allSongRows.Add(row);
        }
        HasSongs = _allSongRows.Count > 0;
        Filters.RestoreState(
            snapshot.SelectedSortIndex, snapshot.SearchText, snapshot.IsFilterExpanded);
        RestorePageScrollState(snapshot.ScrollOffset);
    }

    internal bool HasRetainedPageData => _ref is not null || _allSongRows.Count > 0;

    internal int RetainedTrackCount => _allSongRows.Count;

    internal void DiscardNavigationSnapshot(DetailNavigationSnapshot? snapshot)
    {
        if (snapshot is not null)
            _ = _musicCache.DiscardDetailPageSnapshotAsync(snapshot.CacheKey);
    }

    private void BeginPageLoad()
    {
        CancelCurrentLoad();
        _loadCancellation = new CancellationTokenSource();
        ResetPageScrollState();
    }

    private void CancelCurrentLoad()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _loading = false;
        _loadTask = null;
        _loadAllForFilterTask = null;
        IsLoadingMore = false;
    }

    private bool IsCurrentLoad(int generation, CancellationToken token)
        => generation == _loadGeneration && !token.IsCancellationRequested;

    private static NavigationPageCacheTrack ToCacheTrack(SongItemViewModel row) => new(
        row.Song,
        row.IsPlayable,
        true,
        row.Song.IsPlaybackUnavailable,
        row.Song.PreferCachedPlayback);

    private static void RestoreSongFlags(NavigationPageCacheTrack track)
    {
        track.Song.IsPlaybackUnavailable = track.IsPlaybackUnavailable;
        track.Song.PreferCachedPlayback = track.PreferCachedPlayback;
    }

    private void OnFiltersChanged()
    {
        RefreshVisibleSongs();
        if (Filters.IsActive)
            _ = EnsureAllLoadedForFilterAsync();
    }

    private void RefreshVisibleSongs()
    {
        var projection = Filters.ApplyToTracks(_allSongRows);
        if (Songs.Count <= projection.Count
            && Songs.SequenceEqual(projection.Take(Songs.Count)))
        {
            Songs.AddRange(projection.Skip(Songs.Count).ToList());
            return;
        }

        Songs.ReplaceAll(projection);
    }

    /// <summary>排序和搜索对完整歌手曲库生效，而不是只处理当前已经滚动加载的页面。</summary>
    private Task EnsureAllLoadedForFilterAsync()
    {
        if (_loadAllForFilterTask is not null)
            return _loadAllForFilterTask;

        var generation = _loadGeneration;
        return _loadAllForFilterTask = LoadAllForFilterCoreAsync(generation);
    }

    private async Task LoadAllForFilterCoreAsync(int generation)
    {
        await Task.Yield();
        try
        {
            while (generation == _loadGeneration && Filters.IsActive && HasMore)
            {
                var before = _offset;
                await LoadMoreAsync();
                if (_offset == before) break;
            }
        }
        finally
        {
            if (generation == _loadGeneration)
                _loadAllForFilterTask = null;
        }
    }

    [RelayCommand]
    private async Task PlayAllAsync()
    {
        var firstPlayable = Songs.FirstOrDefault(song => song.IsPlayable);
        if (firstPlayable is not null)
            await firstPlayable.PlayCommand.ExecuteAsync(null);
    }
}
