using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ALyricEase.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;

namespace ALyricEase.ViewModels;

/// <summary>自己的跨平台音乐主页。沿用资料库中的歌单实例和音源身份，收藏、重命名及退出即时同步。</summary>
public sealed partial class PersonalHomeViewModel : NavigationDetailViewModelBase
{
    private readonly PlaylistViewModel _library;
    private Task? _loadTask;
    private bool _rebuildPending;

    public PersonalHomeViewModel(PlaylistViewModel library, AccountViewModel account)
    {
        _library = library;
        Account = account;
        library.Playlists.CollectionChanged += OnLibraryChanged;
        library.NetEaseCollectedPlaylists.CollectionChanged += OnLibraryChanged;
        library.QqPlaylists.CollectionChanged += OnLibraryChanged;
        library.PropertyChanged += OnLibraryPropertyChanged;
        RebuildPlaylists();
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

    partial void OnSourceIndexChanged(int value)
    {
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
    {
        // 全量刷新会连续 Clear/Add；一次 UI 调度仅重建一次，避免大资料库逐项重建视图。
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
            RebuildPlaylists();
        }
    }

    private void RebuildPlaylists()
    {
        TastePlaylists.Clear();
        CreatedPlaylists.Clear();
        CollectedPlaylists.Clear();
        if (SourceIndex != 2)
        {
            foreach (var item in _library.Playlists)
                (_library.IsLikedPlaylist(item.Playlist) ? TastePlaylists : CreatedPlaylists).Add(item);
            foreach (var item in _library.NetEaseCollectedPlaylists) CollectedPlaylists.Add(item);
        }
        if (SourceIndex != 1)
        {
            foreach (var item in _library.QqPlaylists)
                (_library.IsLikedPlaylist(item.Playlist) ? TastePlaylists
                    : item.Playlist.DirId != 0 ? CreatedPlaylists : CollectedPlaylists).Add(item);
        }
        foreach (var name in new[] { nameof(HasTaste), nameof(HasCreated), nameof(HasCollected),
                     nameof(IsEmpty), nameof(ConnectionSummary), nameof(EmptyTitle), nameof(EmptyDescription) })
            OnPropertyChanged(name);
    }
}
