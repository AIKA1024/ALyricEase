using System;
using System.Threading.Tasks;
using ALyricEase.Models;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>播放详情页"接下来播放"列表行:封面 + 歌名 + 歌手·专辑 + 右侧移除。
/// 单击行播放该曲;封面由视图按可见性加载并在离树时释放。</summary>
public sealed partial class QueueItemViewModel : ViewModelBase
{
    private readonly Func<QueueItemViewModel, Task> _play;
    private readonly Action<QueueItemViewModel> _remove;
    public QueueItemViewModel(Song song, Func<QueueItemViewModel, Task> play, Action<QueueItemViewModel> remove)
    {
        Song = song;
        _play = play;
        _remove = remove;
    }

    public Song Song { get; }

    public string Name => Song.Name;

    /// <summary>副标题:歌手 · 专辑(原版 QueueTrackItem 格式;无专辑时只显示歌手)。</summary>
    public string ArtistAlbumText => string.IsNullOrEmpty(Song.Album) ? Song.Artist : $"{Song.Artist} · {Song.Album}";

    [RelayCommand]
    private Task PlayAsync() => _play(this);

    [RelayCommand]
    private void Remove() => _remove(this);

}
