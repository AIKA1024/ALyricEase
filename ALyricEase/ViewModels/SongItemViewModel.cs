using System;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>搜索结果单行:展示歌曲信息 + 双击播放。封面后台加载。</summary>
public sealed partial class SongItemViewModel : ViewModelBase
{
    private readonly Func<Song, Task> _playSong;

    private bool _coverRequested;

    public SongItemViewModel(Song song, Func<Song, Task> playSong)
    {
        Song = song;
        _playSong = playSong;
        // 封面懒加载:列表项可见(容器 realized)时才拉,配合虚拟化,避免上千首并发下载
    }

    /// <summary>容器 realized 时调用:首次才真正拉取封面(幂等)。</summary>
    public void EnsureCoverLoaded()
    {
        if (_coverRequested || Cover is not null) return;
        _coverRequested = true;
        _ = LoadCoverAsync();
    }

    public Song Song { get; }

    public string Name => Song.Name;

    public string Artist => Song.Artist;

    public string DurationText => FormatDuration(Song.DurationMs);

    /// <summary>VIP/付费标记,UI 用。</summary>
    public bool IsVip => Song.Fee != 0;

    [ObservableProperty]
    private IImage? _cover;

    [RelayCommand]
    private async Task PlayAsync() => await _playSong(Song);

    private async Task LoadCoverAsync() => Cover = await CoverLoader.LoadAsync(Song.CoverUrl, 100);

    private static string FormatDuration(int ms)
    {
        if (ms <= 0) return "--:--";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
