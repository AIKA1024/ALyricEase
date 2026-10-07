using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models.Dtos;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
namespace ALyricEase.ViewModels;

/// <summary>歌手全部专辑页("专辑·查看更多"入口):服务端分页流式加载,滚动近底部增量补页,
/// 封面由视图容器 realized 时懒加载(AlbumGrid 分块虚拟化)。QQ GetAlbumList 与网易云
/// /api/artist/albums 两套分页在此统一。</summary>
public sealed partial class ArtistAlbumsPageViewModel : NavigationDetailViewModelBase
{
    private const int PageSize = 30;

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly MusicCacheService _musicCache;
    private readonly AppStateStore _state;

    private ArtistPageRef? _ref;
    private int _offset;
    private readonly List<AlbumCardViewModel> _allAlbums = new();
    private bool _loading;
    private Task? _loadTask;
    private Task? _loadAllForFilterTask;
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;

    public ArtistAlbumsPageViewModel(
        NetEaseApiClient api,
        QQMusicApiClient qqApi,
        MusicCacheService musicCache,
        AppStateStore state)
    {
        _api = api;
        _qqApi = qqApi;
        _musicCache = musicCache;
        _state = state;
        _isListMode = state.ArtistAlbumsListMode ?? (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS());
        Filters = CollectionSortAndFilterViewModel.ForAlbums("在专辑中搜索");
        Filters.FilterChanged += OnFiltersChanged;
    }

    public RangeObservableCollection<AlbumCardViewModel> Albums { get; } = new();

    public CollectionSortAndFilterViewModel Filters { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayModeToolTip))]
    private bool _isListMode;

    public string DisplayModeToolTip => IsListMode ? "切换为大图模式" : "切换为详细列表模式";

    partial void OnIsListModeChanged(bool value)
    {
        _state.ArtistAlbumsListMode = value;
        _state.Save();
    }

    [ObservableProperty] private string _name = "";

    /// <summary>加载进度文案。</summary>
    [ObservableProperty] private string _subtitle = "";

    [ObservableProperty] private bool _hasAlbums;

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
        Albums.Clear();
        _allAlbums.Clear();
        Filters.Reset();
        _offset = 0;
        HasAlbums = false;
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
            IReadOnlyList<ArtistAlbumItem> page;
            if (_ref!.IsQq)
            {
                page = await _qqApi.GetArtistAlbumPageAsync(
                    _ref.QqMid, _offset, PageSize, ct);
                if (!IsCurrentLoad(generation, ct)) return;
                // QQ 不下发总数,满页视为可能还有下一页
                hasMore = page.Count >= PageSize;
            }
            else
            {
                var (nePage, neMore) = await _api.GetArtistAlbumPageAsync(
                    _ref.NetEaseId, PageSize, _offset, ct);
                if (!IsCurrentLoad(generation, ct)) return;
                page = nePage;
                hasMore = neMore;
            }

            if (page.Count > 0)
            {
                _offset += page.Count;
                List<AlbumCardViewModel>? qqCards = _ref.IsQq ? new List<AlbumCardViewModel>() : null;
                foreach (var a in page)
                {
                    var card = _ref.IsQq
                        ? new AlbumCardViewModel(a.Id, a.Name, a.PicUrl, a.Mid,
                            publishTimeMs: a.PublishTime, songCount: a.Size)
                        : new AlbumCardViewModel(a.Id, a.Name, a.PicUrl,
                            publishTimeMs: a.PublishTime, songCount: a.Size);
                    qqCards?.Add(card);
                    _allAlbums.Add(card);
                }
                RefreshVisibleAlbums();
                HasAlbums = true;
                // QQ 列表接口不带曲数(totalNum 恒 0),批量补拉后就地回填卡片信息行
                if (qqCards is not null)
                    _ = FetchQqSongCountsAsync(qqCards, generation, ct);
            }
            Subtitle = $"已加载 {_allAlbums.Count} 张专辑";
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

    /// <summary>批量补拉 QQ 专辑曲数并回填卡片(列表接口不带曲数)。锦上添花:失败静默,换歌手即作废。</summary>
    private async Task FetchQqSongCountsAsync(
        IReadOnlyList<AlbumCardViewModel> cards, int generation, CancellationToken ct)
    {
        try
        {
            var mids = cards.Select(c => c.Mid).ToList();
            var counts = await _qqApi.GetAlbumSongCountsAsync(mids, ct).ConfigureAwait(true);
            if (!IsCurrentLoad(generation, ct)) return;
            foreach (var card in cards)
                if (counts.TryGetValue(card.Mid, out var count))
                    card.UpdateSongCount(count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // 曲数补拉失败不影响卡片其余信息
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
            DetailPageKind.ArtistAlbums,
            artistRef.Source,
            artistRef.NetEaseId,
            artistRef.QqMid,
            artistRef.Name,
            Filters.SelectedSortIndex,
            Filters.SearchText,
            Filters.IsExpanded,
            PageScrollOffset);
        CancelCurrentLoad();

        if (_allAlbums.Count > 0 || !wasLoading)
        {
            _ = _musicCache.CacheDetailPageSnapshotAsync(
                cacheKey,
                new DetailPageCacheData(
                    [],
                    _allAlbums.Select(ToCacheAlbum).ToList(),
                    [],
                    Name: Name,
                    Subtitle: Subtitle,
                    Offset: _offset,
                    HasMore: HasMore));
        }

        ReleaseCurrentPageData();
        return snapshot;
    }

    internal void ReleaseCurrentPageData()
    {
        CancelCurrentLoad();
        _ref = null;
        Albums.Clear();
        _allAlbums.Clear();
        Filters.Reset();
        _offset = 0;
        HasAlbums = false;
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
        Albums.Clear();
        _allAlbums.Clear();
        Filters.Reset();
        _offset = 0;
        HasAlbums = false;
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
        HasMore = cached.HasMore;
        foreach (var album in cached.Albums)
            _allAlbums.Add(ToAlbumCard(album));
        HasAlbums = _allAlbums.Count > 0;
        Filters.RestoreState(
            snapshot.SelectedSortIndex, snapshot.SearchText, snapshot.IsFilterExpanded);
        RestorePageScrollState(snapshot.ScrollOffset);
    }

    internal bool HasRetainedPageData => _ref is not null || _allAlbums.Count > 0;

    internal int RetainedAlbumCount => _allAlbums.Count;

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

    private static NavigationPageCacheAlbum ToCacheAlbum(AlbumCardViewModel album) => new(
        album.Id, album.Title, album.CoverUrl, album.Mid, album.PublishTimeMs, album.SongCount);

    private static AlbumCardViewModel ToAlbumCard(NavigationPageCacheAlbum album) =>
        new(album.Id, album.Title, album.CoverUrl, album.Mid, album.PublishTimeMs, album.SongCount);

    private void OnFiltersChanged()
    {
        RefreshVisibleAlbums();
        if (Filters.IsActive)
            _ = EnsureAllLoadedForFilterAsync();
    }

    private void RefreshVisibleAlbums()
    {
        var projection = Filters.ApplyToAlbums(_allAlbums);
        if (Albums.Count <= projection.Count
            && Albums.SequenceEqual(projection.Take(Albums.Count)))
        {
            Albums.AddRange(projection.Skip(Albums.Count).ToList());
            return;
        }

        Albums.ReplaceAll(projection);
    }

    /// <summary>启用排序或搜索后补齐剩余分页，避免筛选结果只覆盖当前视口已加载内容。</summary>
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
}
