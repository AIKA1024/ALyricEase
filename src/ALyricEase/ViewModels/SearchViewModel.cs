using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>搜索 VM:登录页(大标题 + 音源切换 + 热门关键词)→ 结果页两态;双击结果 → PlayerViewModel 播放。
/// 音源经 MusicApiProvider 选择(网易云/QQ 音乐),请求走 IMusicApi 抽象,结果统一为 Song(Source 自动标记)。</summary>
public sealed partial class SearchViewModel : ViewModelBase
{
    private readonly PlayerViewModel _player;

    /// <summary>红心状态用网易云客户端(QQ 曲目在 SongItemViewModel 内跳过)。</summary>
    private readonly NetEaseApiClient? _neteaseApi;

    public SearchViewModel(MusicApiProvider sources, PlayerViewModel player)
    {
        Sources = sources.All;
        _neteaseApi = sources.All.FirstOrDefault(a => a.Source == MusicSource.NetEase) as NetEaseApiClient;
        _selectedSource = Sources.Count > 0 ? Sources[0] : sources.Default;
        _player = player;
    }

    /// <summary>全部已注册音源(UI 切换按钮列表)。</summary>
    public IReadOnlyList<IMusicApi> Sources { get; }

    private IMusicApi _selectedSource;

    /// <summary>当前选中的音源。</summary>
    public IMusicApi SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                OnPropertyChanged(nameof(IsNetEaseSource));
                OnPropertyChanged(nameof(IsQQSource));
                OnPropertyChanged(nameof(TrendingKeywords));
            }
        }
    }

    /// <summary>音源切换按钮高亮态(Avalonia Classes.active 绑定)。</summary>
    public bool IsNetEaseSource => SelectedSource.Source == MusicSource.NetEase;

    public bool IsQQSource => SelectedSource.Source == MusicSource.QQ;

    [RelayCommand]
    private void SelectNetEaseSource()
        => SelectedSource = Sources.First(s => s.Source == MusicSource.NetEase);

    [RelayCommand]
    private void SelectQQSource()
        => SelectedSource = Sources.First(s => s.Source == MusicSource.QQ);

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

    /// <summary>热门搜索关键词(登录页 chips,点按直接搜索);随音源切换刷新。</summary>
    public IReadOnlyList<string> TrendingKeywords => IsQQSource
        ? ["周杰伦", "晴天", "林俊杰", "陈奕迅", "稻香", "QQ音乐热歌"]
        : ["周杰伦", "晴天", "林俊杰", "陈奕迅", "稻香", "网易云热歌榜"];

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
            var songs = await SelectedSource.SearchAsync(keyword, 30);
            Results.Clear();
            foreach (var song in songs)
                Results.Add(new SongItemViewModel(song, _player.PlayFromList, queue: songs, api: _neteaseApi, source: $"{SelectedSource.DisplayName}·搜索"));
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
