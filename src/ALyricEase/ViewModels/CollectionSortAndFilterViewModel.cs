using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

public enum CollectionSortMode
{
    Original,
    Title,
    Artist,
    Album,
}

/// <summary>
/// 原版 TrackCollectionSortAndFilterControl 的共享状态：排序、即时文本筛选、重置与展开状态。
/// 页面只负责提供原始集合；筛选后的投影不会改写播放队列或服务端原始顺序。
/// </summary>
public sealed partial class CollectionSortAndFilterViewModel : ViewModelBase
{
    private readonly IReadOnlyList<CollectionSortMode> _sortModes;
    private bool _suppressChanged;

    private CollectionSortAndFilterViewModel(
        IReadOnlyList<CollectionSortMode> sortModes,
        IReadOnlyList<string> sortOptions,
        string searchWatermark)
    {
        _sortModes = sortModes;
        SortOptions = sortOptions;
        SearchWatermark = searchWatermark;
    }

    public static CollectionSortAndFilterViewModel ForTracks(string searchWatermark) => new(
        [
            CollectionSortMode.Original,
            CollectionSortMode.Title,
            CollectionSortMode.Artist,
            CollectionSortMode.Album,
        ],
        [
            "原始顺序",
            "按标题排序（升序）",
            "按表演者排序（升序）",
            "按专辑排序（升序）",
        ],
        searchWatermark);

    public static CollectionSortAndFilterViewModel ForAlbums(string searchWatermark) => new(
        [CollectionSortMode.Original, CollectionSortMode.Title],
        ["原始顺序", "按标题排序（升序）"],
        searchWatermark);

    public IReadOnlyList<string> SortOptions { get; }

    public string SearchWatermark { get; }

    public CollectionSortMode SortMode =>
        SelectedSortIndex >= 0 && SelectedSortIndex < _sortModes.Count
            ? _sortModes[SelectedSortIndex]
            : CollectionSortMode.Original;

    public bool IsActive => SelectedSortIndex != 0 || !string.IsNullOrWhiteSpace(SearchText);

    public bool CanReset => IsActive;

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private int _selectedSortIndex;

    [ObservableProperty] private string _searchText = "";

    public event Action? FilterChanged;

    partial void OnSelectedSortIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SortMode));
        NotifyFilterChanged();
    }

    partial void OnSearchTextChanged(string value) => NotifyFilterChanged();

    /// <summary>切换页面内容时恢复原始顺序与空查询，但保留用户当前的展开状态。</summary>
    public void Reset()
    {
        if (!IsActive) return;
        _suppressChanged = true;
        SelectedSortIndex = 0;
        SearchText = "";
        _suppressChanged = false;
        RaiseFilterChanged();
    }

    /// <summary>导航返回时原子恢复筛选 UI，只重建一次集合投影。</summary>
    internal void RestoreState(int selectedSortIndex, string? searchText, bool isExpanded)
    {
        _suppressChanged = true;
        SelectedSortIndex = Math.Clamp(selectedSortIndex, 0, _sortModes.Count - 1);
        SearchText = searchText ?? "";
        IsExpanded = isExpanded;
        _suppressChanged = false;
        RaiseFilterChanged();
    }

    [RelayCommand]
    private void ResetFilters() => Reset();

    public IReadOnlyList<SongItemViewModel> ApplyToTracks(IEnumerable<SongItemViewModel> source)
    {
        var query = SearchText.Trim();
        var filtered = query.Length == 0
            ? source
            : source.Where(item =>
                Contains(item.Name, query)
                || Contains(item.Artist, query)
                || Contains(item.Album, query));

        return SortMode switch
        {
            CollectionSortMode.Title => filtered
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Index)
                .ToList(),
            CollectionSortMode.Artist => filtered
                .OrderBy(item => item.Artist, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Index)
                .ToList(),
            CollectionSortMode.Album => filtered
                .OrderBy(item => item.Album, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Index)
                .ToList(),
            _ => filtered.ToList(),
        };
    }

    public IReadOnlyList<AlbumCardViewModel> ApplyToAlbums(IEnumerable<AlbumCardViewModel> source)
    {
        var query = SearchText.Trim();
        var filtered = query.Length == 0
            ? source
            : source.Where(item => Contains(item.Title, query));

        return SortMode == CollectionSortMode.Title
            ? filtered.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList()
            : filtered.ToList();
    }

    private void NotifyFilterChanged()
    {
        if (_suppressChanged) return;
        RaiseFilterChanged();
    }

    private void RaiseFilterChanged()
    {
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanReset));
        FilterChanged?.Invoke();
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
