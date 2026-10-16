using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ALyricEase.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ALyricEase.ViewModels;

/// <summary>跨平台收藏投影，复用资料库中的歌单实例，随登录、刷新和退出实时更新。</summary>
public sealed partial class CollectedPlaylistsViewModel : ViewModelBase
{
    private readonly PlaylistViewModel _playlist;

    public CollectedPlaylistsViewModel(PlaylistViewModel playlist)
    {
        _playlist = playlist;
        playlist.NetEaseCollectedPlaylists.CollectionChanged += OnCollectionChanged;
        playlist.QqPlaylists.CollectionChanged += OnCollectionChanged;
        playlist.PropertyChanged += OnLibraryPropertyChanged;
        RebuildPlaylists();
    }

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    [ObservableProperty] private int _sourceIndex;

    public bool IsEmpty => Playlists.Count == 0;
    public string CountSummary => $"{Playlists.Count} 个收藏歌单";
    public string EmptyTitle => SourceIndex switch
    {
        1 => "还没有网易云音乐的收藏歌单",
        2 => "还没有 QQ 音乐的收藏歌单",
        _ => "还没有收藏的歌单",
    };
    public string EmptyDescription => "登录对应的音乐账号，收藏的歌单会同步到这里。";
    public string LibraryStatus => string.Join("\n", new[]
    {
        SourceIndex != 2 ? _playlist.NetEaseLibraryError : null,
        SourceIndex != 1 ? _playlist.QqLibraryError : null,
    }.Where(message => !string.IsNullOrEmpty(message)));
    public bool HasError => LibraryStatus.Length > 0;

    partial void OnSourceIndexChanged(int value) => RebuildPlaylists();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildPlaylists();

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 原地更新的元数据也可能改变歌单归属，不一定触发 CollectionChanged。
        if (e.PropertyName == nameof(PlaylistViewModel.LibraryOverviewVersion)) RebuildPlaylists();
        if (e.PropertyName is nameof(PlaylistViewModel.NetEaseLibraryError) or nameof(PlaylistViewModel.QqLibraryError))
            NotifyStatus();
    }

    private void RebuildPlaylists()
    {
        var items = new List<PlaylistItemViewModel>();
        if (SourceIndex != 2) items.AddRange(_playlist.NetEaseCollectedPlaylists);
        if (SourceIndex != 1)
        {
            // 与个人主页同口径：QQ 自建歌单带资产目录 id，收藏歌单没有目录 id。
            // 排除“我喜欢”与可写歌单，保留原 VM 的平台身份供打开、播放和菜单路由。
            items.AddRange(_playlist.QqPlaylists.Where(item => item.Playlist.DirId == 0
                && !item.Playlist.CanAddTracks && !_playlist.IsLikedPlaylist(item.Playlist)));
        }
        CollectionSync.Apply(Playlists, items);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountSummary));
        OnPropertyChanged(nameof(EmptyTitle));
        NotifyStatus();
    }

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(LibraryStatus));
        OnPropertyChanged(nameof(HasError));
    }
}
