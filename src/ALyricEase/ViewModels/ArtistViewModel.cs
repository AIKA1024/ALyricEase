using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>歌手"查看更多"页(全部歌曲/全部专辑)的定位参数:
/// 网易云按数字 id、QQ 按 singer mid,两音源统一由此记录描述。</summary>
public sealed record ArtistPageRef(MusicSource Source, long NetEaseId, string QqMid, string Name)
{
    public bool IsQq => Source == MusicSource.QQ;
}

/// <summary>歌手页:头部(圆形头像 + 名字)+ 热门歌曲横向网格 + 专辑/单曲卡片网格。从歌单行/专辑行歌手入口进入。
/// 网易云按数字 id、QQ 按 singer mid 取数(两套 Load)。</summary>
public sealed partial class ArtistViewModel : NavigationDetailViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly PlayerViewModel _player;
    private readonly MusicCacheService _musicCache;
    private long _artistId;
    private string _singerMid = "";
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;
    private bool _isLoading;

    public ArtistViewModel(
        NetEaseApiClient api,
        QQMusicApiClient qqApi,
        PlayerViewModel player,
        MusicCacheService musicCache)
    {
        _api = api;
        _qqApi = qqApi;
        _player = player;
        _musicCache = musicCache;
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _hasAlbums;
    [ObservableProperty] private bool _hasSingles;
    [ObservableProperty] private string _avatarUrl = "";

    /// <summary>热门歌曲(横向换行网格,每行 400px TrackRow)。</summary>
    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    /// <summary>专辑(横向卡片)。</summary>
    public ObservableCollection<AlbumCardViewModel> Albums { get; } = new();

    /// <summary>单曲与EP(横向卡片)。</summary>
    public ObservableCollection<AlbumCardViewModel> Singles { get; } = new();

    /// <summary>进入歌手页时调用:先清掉上一位歌手的内容,再拉歌手信息 + 热门歌曲 + 专辑/单曲。
    /// 失败静默(页面停在空态,而不是上一位歌手的数据)。</summary>
    public async Task LoadAsync(long artistId)
    {
        var (generation, ct) = BeginLoad();
        ClearContent();
        _artistId = artistId;
        _singerMid = "";
        try
        {
            var info = await _api.GetArtistAsync(artistId, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            Name = info.Name;
            if (info.Avatar is { Length: > 0 } pic) AvatarUrl = pic;

            var songs = await _api.GetArtistSongsAsync(artistId, 30, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            var queue = songs;
            foreach (var s in songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, api: _api, queue: queue, source: info.Name));

            var albums = await _api.GetArtistAlbumsAsync(artistId, 50, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            foreach (var a in albums)
            {
                var card = new AlbumCardViewModel(a.Id, a.Name, a.PicUrl);
                // 接口返回的 type 是字符串:专辑 / Single / EP
                if (string.Equals(a.Type, "专辑", StringComparison.Ordinal)) Albums.Add(card);
                else Singles.Add(card);
            }
            HasAlbums = Albums.Count > 0;
            HasSingles = Singles.Count > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // 网络失败静默:停在空态,不崩
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) _isLoading = false;
        }
    }

    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Songs.Count == 0) return;
        var firstPlayable = Songs.FirstOrDefault(song => song.IsPlayable);
        if (firstPlayable is not null)
            await firstPlayable.PlayCommand.ExecuteAsync(null);
    }

    /// <summary>"热门歌曲"区查看更多 → 全部歌曲页(流式分页)。</summary>
    [RelayCommand]
    private async Task OpenAllSongsAsync()
        => await OpenMoreAsync(isSongsPage: true).ConfigureAwait(true);

    /// <summary>"专辑"区查看更多 → 全部专辑页(流式分页)。</summary>
    [RelayCommand]
    private async Task OpenAllAlbumsAsync()
        => await OpenMoreAsync(isSongsPage: false).ConfigureAwait(true);

    /// <summary>两音源统一跳转;未加载过歌手(无 id/mid)时不响应。</summary>
    private async Task OpenMoreAsync(bool isSongsPage)
    {
        if (_singerMid.Length == 0 && _artistId == 0) return;
        var artistRef = _singerMid.Length > 0
            ? new ArtistPageRef(Services.MusicSource.QQ, 0, _singerMid, Name)
            : new ArtistPageRef(Services.MusicSource.NetEase, _artistId, "", Name);
        try
        {
            var main = ServiceLocator.Get<MainViewModel>();
            if (isSongsPage) await main.OpenArtistSongsPageCommand.ExecuteAsync(artistRef);
            else await main.OpenArtistAlbumsPageCommand.ExecuteAsync(artistRef);
        }
        catch { /* SelfTest/Headless 等无宿主环境 */ }
    }

    /// <summary>QQ 音乐歌手页(按 singer mid):头像用 T001 图床模板;名字从命中 mid 的曲目取;
    /// 专辑/单曲按 albumType 粗分(EP/单曲 → 单曲与EP,其余 → 专辑)。失败静默。</summary>
    public async Task LoadQqAsync(string singerMid)
    {
        var (generation, ct) = BeginLoad();
        ClearContent();
        _artistId = 0;
        _singerMid = singerMid;
        try
        {
            AvatarUrl = $"https://y.gtimg.cn/music/photo_new/T001R300x300M000{singerMid}.jpg";

            var songs = await _qqApi.GetArtistSongsAsync(singerMid, 30, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            Name = ResolveSingerName(songs, singerMid);
            foreach (var s in songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, queue: songs, source: Name));

            var albums = await _qqApi.GetArtistAlbumsAsync(singerMid, 50, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            foreach (var a in albums)
            {
                var card = new AlbumCardViewModel(a.Id, a.Name, a.PicUrl, a.Mid);
                // QQ albumType:录音室专辑/现场专辑等归专辑,EP/单曲归"单曲与EP"
                var isSingle = a.Type.Contains("EP", StringComparison.OrdinalIgnoreCase)
                               || a.Type.Contains("单曲", StringComparison.Ordinal);
                if (isSingle) Singles.Add(card);
                else Albums.Add(card);
            }
            HasAlbums = Albums.Count > 0;
            HasSingles = Singles.Count > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // 网络失败静默:停在空态,不崩
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) _isLoading = false;
        }
    }

    internal DetailNavigationSnapshot? CaptureAndReleaseNavigationSnapshot()
    {
        if (_artistId == 0 && _singerMid.Length == 0)
        {
            ReleaseCurrentPageData();
            return null;
        }

        var wasLoading = _isLoading;
        var cacheKey = Guid.NewGuid().ToString("N");
        var source = _singerMid.Length > 0 ? MusicSource.QQ : MusicSource.NetEase;
        var snapshot = new DetailNavigationSnapshot(
            cacheKey,
            DetailPageKind.Artist,
            source,
            _artistId,
            _singerMid,
            Name,
            0,
            "",
            false,
            PageScrollOffset);
        CancelCurrentLoad();

        if (!wasLoading)
        {
            var tracks = Songs.Select(ToCacheTrack).ToList();
            _ = _musicCache.CacheDetailPageSnapshotAsync(
                cacheKey,
                new DetailPageCacheData(
                    tracks,
                    Albums.Select(ToCacheAlbum).ToList(),
                    Singles.Select(ToCacheAlbum).ToList(),
                    Name: Name,
                    AvatarUrl: AvatarUrl));
        }

        ReleaseCurrentPageData();
        return snapshot;
    }

    internal void ReleaseCurrentPageData()
    {
        CancelCurrentLoad();
        ClearContent();
        _artistId = 0;
        _singerMid = "";
        ResetPageScrollState();
    }

    internal async Task RestoreNavigationSnapshotAsync(DetailNavigationSnapshot snapshot)
    {
        var (generation, ct) = BeginLoad();
        ClearContent();
        _artistId = snapshot.Id;
        _singerMid = snapshot.Mid;

        var cached = await _musicCache.TryTakeDetailPageSnapshotAsync(snapshot.CacheKey);
        if (!IsCurrentLoad(generation, ct)) return;
        if (cached is null)
        {
            _isLoading = false;
            var fallbackLoad = snapshot.Source == MusicSource.QQ
                ? LoadQqAsync(snapshot.Mid)
                : LoadAsync(snapshot.Id);
            var fallbackGeneration = _loadGeneration;
            await fallbackLoad;
            if (fallbackGeneration != _loadGeneration) return;
            RestorePageScrollState(snapshot.ScrollOffset);
            return;
        }

        Name = cached.Name;
        AvatarUrl = cached.AvatarUrl;
        var queue = cached.Tracks.Where(track => track.IsQueued).Select(track => track.Song).ToList();
        foreach (var track in cached.Tracks)
        {
            RestoreSongFlags(track);
            var row = new SongItemViewModel(
                track.Song,
                _player.PlayFromList,
                queue: queue,
                api: track.Song.Source == MusicSource.NetEase ? _api : null,
                source: Name);
            row.IsPlayable = track.IsPlayable;
            Songs.Add(row);
        }
        foreach (var album in cached.Albums) Albums.Add(ToAlbumCard(album));
        foreach (var album in cached.Singles) Singles.Add(ToAlbumCard(album));
        HasAlbums = Albums.Count > 0;
        HasSingles = Singles.Count > 0;
        _isLoading = false;
        RestorePageScrollState(snapshot.ScrollOffset);
    }

    internal bool HasRetainedPageData => _artistId != 0 || _singerMid.Length > 0
        || Songs.Count > 0 || Albums.Count > 0 || Singles.Count > 0;

    internal int RetainedItemCount => Songs.Count + Albums.Count + Singles.Count;

    internal void DiscardNavigationSnapshot(DetailNavigationSnapshot? snapshot)
    {
        if (snapshot is not null)
            _ = _musicCache.DiscardDetailPageSnapshotAsync(snapshot.CacheKey);
    }

    private (int Generation, CancellationToken Token) BeginLoad()
    {
        CancelCurrentLoad();
        _loadCancellation = new CancellationTokenSource();
        _isLoading = true;
        ResetPageScrollState();
        return (_loadGeneration, _loadCancellation.Token);
    }

    private void CancelCurrentLoad()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _isLoading = false;
    }

    private bool IsCurrentLoad(int generation, CancellationToken token)
        => generation == _loadGeneration && !token.IsCancellationRequested;

    private static NavigationPageCacheTrack ToCacheTrack(SongItemViewModel row) => new(
        row.Song,
        row.IsPlayable,
        true,
        row.Song.IsPlaybackUnavailable,
        row.Song.PreferCachedPlayback);

    private static NavigationPageCacheAlbum ToCacheAlbum(AlbumCardViewModel album) => new(
        album.Id, album.Title, album.CoverUrl, album.Mid);

    private static AlbumCardViewModel ToAlbumCard(NavigationPageCacheAlbum album) =>
        new(album.Id, album.Title, album.CoverUrl, album.Mid);

    private static void RestoreSongFlags(NavigationPageCacheTrack track)
    {
        track.Song.IsPlaybackUnavailable = track.IsPlaybackUnavailable;
        track.Song.PreferCachedPlayback = track.PreferCachedPlayback;
    }

    /// <summary>清空上一位歌手的内容。歌手页每次进入都重建视图并立即绑定现有内容,
    /// 不清的话新页面会先渲染出上一位歌手的歌(加载失败时更会整页停在错位数据上)。</summary>
    private void ClearContent()
    {
        Name = "";
        AvatarUrl = "";
        Songs.Clear();
        Albums.Clear();
        Singles.Clear();
        HasAlbums = false;
        HasSingles = false;
    }

    /// <summary>从热门歌曲里取该 mid 对应的歌手展示名(取不到退化为第一首的 Artist)。</summary>
    private static string ResolveSingerName(List<Song> songs, string singerMid)
    {
        foreach (var s in songs)
            for (var i = 0; i < s.ArtistMids.Count && i < s.ArtistNames.Count; i++)
                if (s.ArtistMids[i] == singerMid && s.ArtistNames[i].Length > 0)
                    return s.ArtistNames[i];
        return songs.FirstOrDefault()?.Artist ?? "";
    }
}
