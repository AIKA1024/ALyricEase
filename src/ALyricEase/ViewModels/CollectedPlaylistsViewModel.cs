using System.Collections.ObjectModel;
using ALyricEase.ViewModels;

namespace ALyricEase.ViewModels;

/// <summary>「我的收藏」页:展示网易云里收藏的他人歌单(PlaylistViewModel.NetEaseCollectedPlaylists,
/// 按 Subscribed 拆分)。侧栏网易云分组只保留自己创建的歌单,收藏的都收在这里。
/// 不直接重新拉取 —— 数据随登录恢复/侧栏刷新自动更新。</summary>
public sealed class CollectedPlaylistsViewModel
{
    private readonly PlaylistViewModel _playlist;

    public CollectedPlaylistsViewModel(PlaylistViewModel playlist) => _playlist = playlist;

    /// <summary>收藏的他人歌单(实时转发 PlaylistViewModel 的集合)。</summary>
    public ObservableCollection<PlaylistItemViewModel> Playlists => _playlist.NetEaseCollectedPlaylists;
}
