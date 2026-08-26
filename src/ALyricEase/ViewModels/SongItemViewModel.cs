using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>搜索结果单行:展示歌曲信息 + 双击播放。封面/红心状态后台加载。</summary>
public sealed partial class SongItemViewModel : ViewModelBase
{
    private readonly Func<Song, IReadOnlyList<Song>?, string?, Task> _playSong;
    private readonly IReadOnlyList<Song>? _queue;
    private readonly string? _source;
    private readonly NetEaseApiClient? _api;

    private bool _coverRequested;
    private bool _likedRequested;

    /// <summary>红心/歌手专辑跳转为网易云能力;QQ 等其他音源的行不提供。</summary>
    private bool IsNetEase => Song.Source == Services.MusicSource.NetEase;

    private static IImage? s_neBadge;
    private static IImage? s_qqBadge;

    /// <summary>音源角标(hover 时封面左下角显示):取自两站点浏览器标签页 favicon,
    /// 静态缓存按音源共享一份位图。资源缺失返回 null(Image 空源不渲染)。</summary>
    public IImage? SourceBadge
    {
        get
        {
            if (IsNetEase) return s_neBadge ??= LoadBadge("avares://ALyricEase/Assets/TrackTags/SourceNetease.png");
            return s_qqBadge ??= LoadBadge("avares://ALyricEase/Assets/TrackTags/SourceQQ.png");
        }
    }

    private static IImage? LoadBadge(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public SongItemViewModel(Song song, Func<Song, Task> playSong, int index = 0, NetEaseApiClient? api = null)
        : this(song, (s, _, _) => playSong(s), index, api: api) { }

    public SongItemViewModel(Song song, Func<Song, IReadOnlyList<Song>?, string?, Task> playSong, int index = 0, IReadOnlyList<Song>? queue = null, NetEaseApiClient? api = null, string? source = null)
    {
        Song = song;
        _playSong = playSong;
        _queue = queue;
        _source = source;
        _api = api;
        Index = index;
        // 歌手子菜单项:按 id/mid 与名字一一配对(数量不一致时取短的)
        var artists = new List<ArtistNavItem>();
        var isQq = song.Source == Services.MusicSource.QQ;
        var keys = isQq ? song.ArtistMids.Count : song.ArtistIds.Count;
        var n = Math.Min(song.ArtistNames.Count, keys);
        for (var i = 0; i < n; i++)
            artists.Add(isQq
                ? new ArtistNavItem(0, song.ArtistNames[i], song.ArtistMids[i])
                : new ArtistNavItem(song.ArtistIds[i], song.ArtistNames[i]));
        Artists = artists;
        // 封面懒加载:列表项可见(容器 realized)时才拉,配合虚拟化,避免上千首并发下载
    }

    /// <summary>歌手子菜单项(多歌手时用)。</summary>
    public IReadOnlyList<ArtistNavItem> Artists { get; }

    public bool HasMultipleArtists => Artists.Count > 1;

    /// <summary>容器 realized 时调用:首次才真正拉取封面(幂等)。</summary>
    public void EnsureCoverLoaded()
    {
        if (_coverRequested || Cover is not null) return;
        _coverRequested = true;
        _ = LoadCoverAsync();
    }

    /// <summary>容器 realized 时调用:首次才拉取红心状态(幂等)。未登录/未识别到喜欢歌单则保持未喜欢。</summary>
    public void EnsureLikedLoaded()
    {
        if (_likedRequested || _api is null || !IsNetEase) return;
        _likedRequested = true;
        _ = LoadLikedAsync();
    }

    public Song Song { get; }

    public string Name => Song.Name;

    public string Artist => Song.Artist;

    public string Album => Song.Album;

    /// <summary>歌手 & 专辑 组合文本(每日行歌名下方按钮用):多歌手以 & 拼接,与专辑以 · 分隔。</summary>
    public string ArtistsAndAlbumText
    {
        get
        {
            var artists = Song.ArtistNames is { Count: > 0 } names ? string.Join("&", names) : Artist;
            var hasArtist = !string.IsNullOrEmpty(artists);
            var hasAlbum = !string.IsNullOrEmpty(Album);
            if (hasArtist && hasAlbum) return $"{artists} · {Album}";
            return hasArtist ? artists : Album;
        }
    }

    /// <summary>有可跳转的歌手:网易云按数字 id,QQ 按 singer mid。</summary>
    public bool HasArtist => (IsNetEase && Song.ArtistIds.Count > 0) || Song.ArtistMids.Count > 0;

    /// <summary>有歌手名(展示用):与 HasArtist(可跳转)区分——云盘等无版权歌曲有名字无 id。</summary>
    public bool HasArtistName => !string.IsNullOrEmpty(Song.Artist);

    /// <summary>有可跳转的专辑:网易云按数字 id,QQ 按 album mid。</summary>
    public bool HasAlbum => (IsNetEase && Song.AlbumId != 0) || Song.AlbumMid.Length > 0;

    /// <summary>有专辑名(展示用):与 HasAlbum(可跳转)区分——同上。</summary>
    public bool HasAlbumName => !string.IsNullOrEmpty(Song.Album);

    /// <summary>歌手或专辑至少有名(每日行歌名下组合链接按钮的显示条件:两者皆无则整个隐藏)。</summary>
    public bool HasArtistOrAlbumName => HasArtistName || HasAlbumName;

    /// <summary>歌单内序号(1 起);非歌单场景为 0。</summary>
    public int Index { get; }

    public string DisplayIndex => Index > 0 ? Index.ToString() : "";

    public string DurationText => FormatDuration(Song.DurationMs);

    /// <summary>VIP/付费标记,UI 用。</summary>
    public bool IsVip => Song.Fee != 0;

    [ObservableProperty]
    private IImage? _cover;

    /// <summary>是否已红心(在"我喜欢的音乐"里)。</summary>
    [ObservableProperty]
    private bool _isInLikelist;

    [RelayCommand]
    private async Task PlayAsync() => await _playSong(Song, _queue, _source);

    /// <summary>切换红心:乐观更新,失败回滚。仅网易云曲目(QQ 暂无红心能力)。</summary>
    [RelayCommand]
    private async Task LikeAsync()
    {
        if (_api is null || !IsNetEase) return;
        var prev = IsInLikelist;
        IsInLikelist = !prev; // 乐观更新,立即反馈
        try
        {
            IsInLikelist = await _api.LikeToggleAsync(Song.Id);
        }
        catch
        {
            IsInLikelist = prev; // 请求失败回滚
        }
    }

    private async Task LoadCoverAsync() => Cover = await CoverLoader.LoadAsync(Song.CoverUrl, 100);

    /// <summary>点击歌手 → 歌手页(经服务定位器避免把导航回调穿遍所有创建处)。QQ 按 mid 路由。</summary>
    [RelayCommand]
    private async Task OpenArtistAsync()
    {
        // FirstOrDefault 无匹配时返回 null(而非 ""),必须用 IsNullOrEmpty 判断
        var mid = Song.ArtistMids.FirstOrDefault(m => !string.IsNullOrEmpty(m));
        if (!string.IsNullOrEmpty(mid))
        {
            try { await ServiceLocator.Get<MainViewModel>().OpenQqArtistCommand.ExecuteAsync(mid); }
            catch { /* 未初始化/导航失败:忽略 */ }
            return;
        }
        var id = Song.ArtistIds.FirstOrDefault();
        if (id == 0) return;
        try { await ServiceLocator.Get<MainViewModel>().OpenArtistCommand.ExecuteAsync(id); }
        catch { /* 未初始化/导航失败:忽略 */ }
    }

    /// <summary>多歌手子菜单:点击某个歌手 → 该歌手页。QQ 项 id 为 0,按 mid 路由。</summary>
    [RelayCommand]
    private async Task OpenArtistByIdAsync(long? id)
    {
        if (id is null or 0) return;
        try { await ServiceLocator.Get<MainViewModel>().OpenArtistCommand.ExecuteAsync(id.Value); }
        catch { /* 未初始化/导航失败:忽略 */ }
    }

    /// <summary>点击专辑 → 专辑页(网易云按 id,QQ 按 mid)。</summary>
    [RelayCommand]
    private async Task OpenAlbumAsync()
    {
        if (Song.AlbumMid.Length > 0 && !IsNetEase)
        {
            try { await ServiceLocator.Get<MainViewModel>().OpenQqAlbumCommand.ExecuteAsync(Song.AlbumMid); }
            catch { /* 未初始化/导航失败:忽略 */ }
            return;
        }
        if (Song.AlbumId == 0) return;
        try { await ServiceLocator.Get<MainViewModel>().OpenAlbumCommand.ExecuteAsync(Song.AlbumId); }
        catch { /* 未初始化/导航失败:忽略 */ }
    }

    private async Task LoadLikedAsync()
    {
        if (!IsNetEase) return;
        try
        {
            await _api!.EnsureLikedIdsAsync();
            IsInLikelist = _api.IsLiked(Song.Id);
        }
        catch
        {
            // 保持未喜欢
        }
    }

    private static string FormatDuration(int ms)
    {
        if (ms <= 0) return "--:--";
        var t = TimeSpan.FromMilliseconds(ms);
        // 首段不加前导 0(仿原版 "3:45");超过 1 小时 "1:03:45" 保持分/秒补零
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }
}
