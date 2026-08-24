using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>搜索 VM:登录页(大标题 + 热门关键词)→ 结果页两态;双击结果 → PlayerViewModel 播放。</summary>
public sealed partial class SearchViewModel : ViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly PlayerViewModel _player;

    public SearchViewModel(NetEaseApiClient api, PlayerViewModel player)
    {
        _api = api;
        _player = player;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _keyword = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _isSearching;

    /// <summary>是否已发起过搜索(决定登录页/结果页)。</summary>
    [ObservableProperty] private bool _hasSearched;

    [ObservableProperty] private string? _message;
    [ObservableProperty] private SongItemViewModel? _selectedItem;

    public ObservableCollection<SongItemViewModel> Results { get; } = new();

    /// <summary>热门搜索关键词(登录页 chips,点按直接搜索)。</summary>
    public IReadOnlyList<string> TrendingKeywords { get; } =
        ["周杰伦", "晴天", "林俊杰", "陈奕迅", "稻香", "网易云热歌榜"];

    public bool ShowLanding => !HasSearched && !IsSearching;
    public bool ShowResults => HasSearched;

    /// <summary>结果页空态/错误:显示居中大号 Message(没有搜索结果 20px)。</summary>
    public bool ShowEmpty => HasSearched && !IsSearching && Message is not null;

    partial void OnHasSearchedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLanding));
        OnPropertyChanged(nameof(ShowResults));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLanding));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(ShowEmpty));

    [RelayCommand]
    private void SearchKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        Keyword = keyword.Trim();
        SearchCommand.Execute(null);
    }

    /// <summary>回到搜索登录页(清空结果)。</summary>
    [RelayCommand]
    private void BackToLanding()
    {
        HasSearched = false;
        Keyword = "";
        Message = null;
        Results.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var keyword = Keyword.Trim();
        if (keyword.Length == 0) return;

        HasSearched = true;
        IsSearching = true;
        Message = null;
        try
        {
            var songs = await _api.SearchAsync(keyword, 30);
            Results.Clear();
            foreach (var song in songs)
                Results.Add(new SongItemViewModel(song, _player.PlayFromList, queue: songs, api: _api, source: "搜索结果"));
            if (songs.Count == 0)
                Message = "没有搜索结果";
        }
        catch (ApiException ex)
        {
            Message = $"搜索失败:{ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private bool CanSearch() => !IsSearching && Keyword.Trim().Length > 0;
}
