using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models.Dtos;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
namespace ALyricEase.ViewModels;

/// <summary>歌手全部专辑页("专辑·查看更多"入口):服务端分页流式加载,滚动近底部增量补页,
/// 封面由视图容器 realized 时懒加载(AlbumGrid 分块虚拟化)。QQ GetAlbumList 与网易云
/// /api/artist/albums 两套分页在此统一。</summary>
public sealed partial class ArtistAlbumsPageViewModel : ViewModelBase
{
    private const int PageSize = 30;

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;

    private ArtistPageRef? _ref;
    private int _offset;
    private readonly List<AlbumCardViewModel> _allAlbums = new();
    private bool _loading;
    private Task? _loadTask;
    private Task? _loadAllForFilterTask;

    public ArtistAlbumsPageViewModel(NetEaseApiClient api, QQMusicApiClient qqApi)
    {
        _api = api;
        _qqApi = qqApi;
        Filters = CollectionSortAndFilterViewModel.ForAlbums("在专辑中搜索");
        Filters.FilterChanged += OnFiltersChanged;
    }

    public RangeObservableCollection<AlbumCardViewModel> Albums { get; } = new();

    public CollectionSortAndFilterViewModel Filters { get; }

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
        if (_ref is null || !HasMore || _loading) return _loadTask ?? Task.CompletedTask;
        _loading = true;
        IsLoadingMore = true;
        return _loadTask = LoadPageCoreAsync();
    }

    private async Task LoadPageCoreAsync()
    {
        var hasMore = false;
        try
        {
            IReadOnlyList<ArtistAlbumItem> page;
            if (_ref!.IsQq)
            {
                page = await _qqApi.GetArtistAlbumPageAsync(_ref.QqMid, _offset, PageSize);
                // QQ 不下发总数,满页视为可能还有下一页
                hasMore = page.Count >= PageSize;
            }
            else
            {
                var (nePage, neMore) = await _api.GetArtistAlbumPageAsync(_ref.NetEaseId, PageSize, _offset);
                page = nePage;
                hasMore = neMore;
            }

            if (page.Count > 0)
            {
                _offset += page.Count;
                foreach (var a in page)
                    _allAlbums.Add(_ref.IsQq
                        ? new AlbumCardViewModel(a.Id, a.Name, a.PicUrl, a.Mid)
                        : new AlbumCardViewModel(a.Id, a.Name, a.PicUrl));
                RefreshVisibleAlbums();
                HasAlbums = true;
            }
            Subtitle = $"已加载 {_allAlbums.Count} 张专辑";
        }
        catch
        {
            // 网络失败:停止续拉(已加载内容保留),用户滚到底部不再重复请求
        }
        finally
        {
            HasMore = hasMore;
            _loading = false;
            IsLoadingMore = false;
            _loadTask = null;
        }
    }

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
    private Task EnsureAllLoadedForFilterAsync() =>
        _loadAllForFilterTask ??= LoadAllForFilterCoreAsync();

    private async Task LoadAllForFilterCoreAsync()
    {
        await Task.Yield();
        try
        {
            while (Filters.IsActive && HasMore)
            {
                var before = _offset;
                await LoadMoreAsync();
                if (_offset == before) break;
            }
        }
        finally
        {
            _loadAllForFilterTask = null;
        }
    }
}
