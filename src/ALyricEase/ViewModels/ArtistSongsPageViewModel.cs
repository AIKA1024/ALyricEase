using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手全部歌曲页("热门歌曲·查看更多"入口):服务端分页流式加载,滚动近底部增量补页,
/// 歌曲行封面/红心由视图容器 realized 时懒加载。网易云 /api/v1/artist/songs 与 QQ
/// GetSingerSongList 两套分页在此统一。</summary>
public sealed partial class ArtistSongsPageViewModel : ViewModelBase
{
    private const int PageSize = 50;

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly PlayerViewModel _player;

    private ArtistPageRef? _ref;
    private int _offset;
    private int _total = -1; // 服务端总数;-1 = 未知(按页长判断是否还有下一页)
    private readonly List<Song> _loaded = new();
    private readonly List<SongItemViewModel> _allSongRows = new();
    private bool _loading;
    private Task? _loadTask;
    private Task? _loadAllForFilterTask;

    public ArtistSongsPageViewModel(NetEaseApiClient api, QQMusicApiClient qqApi, PlayerViewModel player)
    {
        _api = api;
        _qqApi = qqApi;
        _player = player;
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
            IReadOnlyList<Song> page;
            if (_ref!.IsQq)
            {
                var (qqPage, qqTotal) = await _qqApi.GetArtistSongPageAsync(_ref.QqMid, _offset, PageSize);
                if (qqTotal > 0) _total = qqTotal;
                page = qqPage;
                // QQ 总数不下发时按满页判断还有下一页
                hasMore = page.Count > 0 && _offset + page.Count < (_total > 0 ? _total : int.MaxValue);
            }
            else
            {
                var (nePage, neTotal, neMore) = await _api.GetArtistSongPageAsync(_ref.NetEaseId, PageSize, _offset);
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

    [RelayCommand]
    private async Task PlayAllAsync()
    {
        var firstPlayable = Songs.FirstOrDefault(song => song.IsPlayable);
        if (firstPlayable is not null)
            await firstPlayable.PlayCommand.ExecuteAsync(null);
    }
}
