using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ALyricEase.Infrastructure;
using ALyricEase.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;

namespace ALyricEase.ViewModels;

/// <summary>自己的跨平台音乐主页。沿用资料库中的歌单实例和音源身份，收藏、重命名及退出即时同步。</summary>
public sealed partial class PersonalHomeViewModel : NavigationDetailViewModelBase
{
    private readonly PlaylistViewModel _library;
    private readonly AppStateStore _state;
    private Task? _loadTask;
    private bool _rebuildPending;

    public PersonalHomeViewModel(PlaylistViewModel library, AccountViewModel account, AppStateStore state)
    {
        _library = library;
        _state = state;
        Account = account;
        // 创建/收藏两段的大图/列表显示偏好。默认值与全部专辑页同规则:移动端列表、桌面大图。
        var mobileDefault = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
        _createdIsListMode = state.HomeCreatedPlaylistsListMode ?? mobileDefault;
        _collectedIsListMode = state.HomeCollectedPlaylistsListMode ?? mobileDefault;
        library.Playlists.CollectionChanged += OnLibraryChanged;
        library.NetEaseCollectedPlaylists.CollectionChanged += OnLibraryChanged;
        library.QqPlaylists.CollectionChanged += OnLibraryChanged;
        library.PropertyChanged += OnLibraryPropertyChanged;
        RebuildPlaylists();
    }

    /// <summary>"创建的歌单"区是否列表显示(否 = 大图卡)。切换即持久化。</summary>
    [ObservableProperty] private bool _createdIsListMode;

    /// <summary>"收藏的歌单"区是否列表显示(否 = 大图卡)。切换即持久化。</summary>
    [ObservableProperty] private bool _collectedIsListMode;

    partial void OnCreatedIsListModeChanged(bool value)
    {
        _state.HomeCreatedPlaylistsListMode = value;
        _state.Save();
    }

    partial void OnCollectedIsListModeChanged(bool value)
    {
        _state.HomeCollectedPlaylistsListMode = value;
        _state.Save();
    }

    public AccountViewModel Account { get; }
    public ObservableCollection<PlaylistItemViewModel> TastePlaylists { get; } = new();
    public ObservableCollection<PlaylistItemViewModel> CreatedPlaylists { get; } = new();
    public ObservableCollection<PlaylistItemViewModel> CollectedPlaylists { get; } = new();

    [ObservableProperty] private int _sourceIndex;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string? _errorMessage;

    public string LibraryStatus => string.Join("\n", new[]
        { ErrorMessage, _library.NetEaseLibraryError, _library.QqLibraryError }.Where(s => !string.IsNullOrEmpty(s)));
    public bool HasError => LibraryStatus.Length > 0;
    public bool HasTaste => TastePlaylists.Count > 0;
    public bool HasCreated => CreatedPlaylists.Count > 0;
    public bool HasCollected => CollectedPlaylists.Count > 0;
    public bool IsEmpty => !IsRefreshing && !HasTaste && !HasCreated && !HasCollected;
    public string ConnectionSummary => (_library.IsLoggedIn, _library.IsQqLoggedIn) switch
    {
        (true, true) => "两个帐号，一起发现你的音乐",
        (true, false) => "已连接网易云音乐 · 也可以登录 QQ 音乐",
        (false, true) => "已连接 QQ 音乐 · 也可以登录网易云音乐",
        _ => "连接你的音乐帐号，让喜欢的音乐在这里相遇",
    };
    public string EmptyTitle => _library.HasAnyLogin ? "这个平台下还没有歌单" : "从登录一个音乐帐号开始";
    public string EmptyDescription => _library.HasAnyLogin
        ? "试试切换平台，或刷新以同步最新歌单。"
        : "登录网易云音乐或 QQ 音乐，即可查看喜欢、创建和收藏的歌单。";

    /// <summary>平台筛选为"全部"时列表行封面显示音源角标;选定单一平台后行本身即代表平台,角标隐藏。</summary>
    public bool ShowSourceBadges => SourceIndex == 0;

    partial void OnSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowSourceBadges));
        RebuildPlaylists();
    }
    partial void OnIsRefreshingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(LibraryStatus));
        OnPropertyChanged(nameof(HasError));
    }

    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    internal void RestoreScrollPosition() => RestorePageScrollState(PageScrollOffset);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        _loadTask = LoadAsync(force: true);
        await _loadTask;
    }

    private async Task LoadAsync(bool force = false)
    {
        IsRefreshing = true;
        ErrorMessage = null;
        try
        {
            await _library.EnsureLibraryOverviewAsync();
            if (force)
                await Task.WhenAll(_library.ReloadNetEasePlaylistsAsync(), _library.ReloadQqPlaylistsAsync());
            await Account.RefreshAsync();
        }
        catch (Exception)
        {
            ErrorMessage = "暂时无法同步音乐资料，请稍后刷新重试。";
        }
        finally
        {
            RebuildPlaylists();
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void ManageAccounts() => ServiceLocator.Get<MainViewModel>().GoAccountCommand.Execute(null);

    private void OnLibraryChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueRebuild();

    private void QueueRebuild()
    {
        // 同一批资料库更新只投影一次，保留未变化条目的视图和封面租约。
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildPending = false;
            RebuildPlaylists();
        }, DispatcherPriority.Background);
    }

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistViewModel.LibraryOverviewVersion)) QueueRebuild();
        if (e.PropertyName is nameof(PlaylistViewModel.NetEaseLibraryError) or nameof(PlaylistViewModel.QqLibraryError))
        {
            OnPropertyChanged(nameof(LibraryStatus));
            OnPropertyChanged(nameof(HasError));
        }
        if (e.PropertyName is nameof(PlaylistViewModel.IsLoggedIn) or nameof(PlaylistViewModel.IsQqLoggedIn)
            or nameof(PlaylistViewModel.UserName) or nameof(PlaylistViewModel.QqUserName))
        {
            Account.SyncLoginState();
            // 登录状态变化后，下次进入重新验证；歌单集合仍即时同步。
            if (!IsRefreshing) _loadTask = null;
            QueueRebuild();
        }
    }

    private void RebuildPlaylists()
    {
        var taste = new List<PlaylistItemViewModel>();
        var created = new List<PlaylistItemViewModel>();
        var collected = new List<PlaylistItemViewModel>();
        // 分页实化（每段 12 张 + 显示更多）做过一版，应用户要求撤回：全量显示下内存与滚动占用可接受。
        // 若资料库规模再涨，考虑 SongGridView 式按列分组虚拟化，别再回退到按钮分页。
        if (SourceIndex != 2)
        {
            foreach (var item in _library.Playlists)
                (_library.IsLikedPlaylist(item.Playlist) ? taste : created).Add(item);
            foreach (var item in _library.NetEaseCollectedPlaylists) collected.Add(item);
        }
        if (SourceIndex != 1)
        {
            foreach (var item in _library.QqPlaylists)
                (_library.IsLikedPlaylist(item.Playlist) ? taste
                    : item.Playlist.DirId != 0 ? created : collected).Add(item);
        }
        CollectionSync.Apply(TastePlaylists, taste);
        CollectionSync.Apply(CreatedPlaylists, created);
        CollectionSync.Apply(CollectedPlaylists, collected);
        foreach (var name in new[] { nameof(HasTaste), nameof(HasCreated), nameof(HasCollected),
                     nameof(IsEmpty), nameof(ConnectionSummary), nameof(EmptyTitle), nameof(EmptyDescription) })
            OnPropertyChanged(name);
    }
}
