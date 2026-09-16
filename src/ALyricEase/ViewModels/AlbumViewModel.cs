using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>专辑页:封面 + 名字 + 歌手 + 全量曲目。从歌单行/歌手页的专辑入口进入。
/// 网易云按数字 id、QQ 按 album mid 取数(两套 Load)。</summary>
public sealed partial class AlbumViewModel : NavigationDetailViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly PlayerViewModel _player;
    private readonly MusicCacheService _musicCache;
    private long _albumId;
    private string _albumMid = "";
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;
    private bool _isLoading;

    public AlbumViewModel(
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
    [ObservableProperty] private string _artistName = "";
    [ObservableProperty] private string _trackCountText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublishDateText))]
    private long _publishTimeMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string _description = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtist))]
    private ArtistNavItem? _primaryArtist;

    [ObservableProperty] private string _coverUrl = "";

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    public bool HasArtist => PrimaryArtist is not null;

    /// <summary>发行日期(如 2026-08-13);未知为空。</summary>
    public string PublishDateText => PublishTimeMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(PublishTimeMs).ToLocalTime().ToString("yyyy-MM-dd")
        : "";

    public ObservableCollection<SongItemViewModel> Songs { get; } = new();

    /// <summary>进入专辑页时调用:先清掉上一位(上一张专辑)的内容,再拉专辑信息 + 全量曲目。
    /// 失败静默(页面停在空态,而不是上一张专辑的数据)。</summary>
    public async Task LoadAsync(long albumId)
    {
        var (generation, ct) = BeginLoad();
        ClearContent();
        _albumId = albumId;
        _albumMid = "";
        try
        {
            var album = await _api.GetAlbumAsync(albumId, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            Name = album.Info.Name;
            var artists = album.Info.Artists?
                .Where(artist => !string.IsNullOrWhiteSpace(artist.Name))
                .ToList() ?? [];
            ArtistName = string.Join("/", artists.Select(artist => artist.Name));
            var primaryArtist = artists.FirstOrDefault(artist => artist.Id != 0);
            PrimaryArtist = primaryArtist is null
                ? null
                : new ArtistNavItem(primaryArtist.Id, primaryArtist.Name);
            TrackCountText = $"{album.Songs.Count} 首";
            PublishTimeMs = album.Info.PublishTime;
            Description = album.Info.Description ?? "";
            if (album.Info.PicUrl is { Length: > 0 } pic) CoverUrl = pic;

            var queue = album.Songs;
            var i = 1;
            foreach (var s in album.Songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, i++, queue, _api, album.Info.Name));
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

    /// <summary>点击专辑头部歌手名，按音源进入主歌手详情页。</summary>
    [RelayCommand]
    private async Task OpenArtistAsync()
    {
        if (PrimaryArtist is not null)
            await PrimaryArtist.OpenCommand.ExecuteAsync(null);
    }

    /// <summary>QQ 音乐专辑页(按 album mid):信息 + 曲目并行拉;发行日期为 "yyyy-MM-dd" 文本。
    /// 歌手名取自曲目(详情接口的 singer 结构不稳定)。失败静默(停在空态)。</summary>
    public async Task LoadQqAsync(string albumMid)
    {
        var (generation, ct) = BeginLoad();
        ClearContent();
        _albumId = 0;
        _albumMid = albumMid;
        try
        {
            var infoTask = _qqApi.GetAlbumInfoByMidAsync(albumMid, ct);
            var songsTask = _qqApi.GetAlbumSongsByMidAsync(albumMid, ct: ct);
            await Task.WhenAll(infoTask, songsTask).ConfigureAwait(true);
            if (!IsCurrentLoad(generation, ct)) return;
            var info = infoTask.Result;
            var songs = songsTask.Result;

            Name = info.Name;
            var leadSong = songs.FirstOrDefault();
            ArtistName = leadSong?.Artist ?? "";
            var primaryArtist = leadSong?.ArtistMids
                .Zip(leadSong.ArtistNames, (mid, name) => (Mid: mid, Name: name))
                .FirstOrDefault(artist => !string.IsNullOrWhiteSpace(artist.Mid));
            PrimaryArtist = string.IsNullOrWhiteSpace(primaryArtist?.Mid)
                ? null
                : new ArtistNavItem(0, primaryArtist.Value.Name, primaryArtist.Value.Mid);
            TrackCountText = $"{songs.Count} 首";
            PublishTimeMs = DateTimeOffset.TryParse(info.PublishDate, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d.ToUnixTimeMilliseconds() : 0;
            Description = info.Description;
            CoverUrl = $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albumMid}.jpg";

            var i = 1;
            foreach (var s in songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, i++, songs, source: info.Name));
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
        if (_albumId == 0 && _albumMid.Length == 0)
        {
            ReleaseCurrentPageData();
            return null;
        }

        var wasLoading = _isLoading;
        var cacheKey = Guid.NewGuid().ToString("N");
        var source = _albumMid.Length > 0 ? MusicSource.QQ : MusicSource.NetEase;
        var snapshot = new DetailNavigationSnapshot(
            cacheKey,
            DetailPageKind.Album,
            source,
            _albumId,
            _albumMid,
            Name,
            0,
            "",
            false,
            PageScrollOffset);
        CancelCurrentLoad();

        if (!wasLoading)
        {
            var primaryArtist = PrimaryArtist is null
                ? null
                : new NavigationPageCacheArtist(
                    PrimaryArtist.IsQq ? MusicSource.QQ : MusicSource.NetEase,
                    PrimaryArtist.Id,
                    PrimaryArtist.Mid,
                    PrimaryArtist.Name);
            _ = _musicCache.CacheDetailPageSnapshotAsync(
                cacheKey,
                new DetailPageCacheData(
                    Songs.Select(ToCacheTrack).ToList(),
                    [],
                    [],
                    Name: Name,
                    ArtistName: ArtistName,
                    TrackCountText: TrackCountText,
                    PublishTimeMs: PublishTimeMs,
                    Description: Description,
                    CoverUrl: CoverUrl,
                    PrimaryArtist: primaryArtist));
        }

        ReleaseCurrentPageData();
        return snapshot;
    }

    internal void ReleaseCurrentPageData()
    {
        CancelCurrentLoad();
        ClearContent();
        _albumId = 0;
        _albumMid = "";
        ResetPageScrollState();
    }

    internal async Task RestoreNavigationSnapshotAsync(DetailNavigationSnapshot snapshot)
    {
        var (generation, ct) = BeginLoad();
        ClearContent();
        _albumId = snapshot.Id;
        _albumMid = snapshot.Mid;

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
        ArtistName = cached.ArtistName;
        TrackCountText = cached.TrackCountText;
        PublishTimeMs = cached.PublishTimeMs;
        Description = cached.Description;
        CoverUrl = cached.CoverUrl;
        PrimaryArtist = cached.PrimaryArtist is null
            ? null
            : new ArtistNavItem(
                cached.PrimaryArtist.NetEaseId,
                cached.PrimaryArtist.Name,
                cached.PrimaryArtist.QqMid);
        var queue = cached.Tracks.Where(track => track.IsQueued).Select(track => track.Song).ToList();
        var index = 1;
        foreach (var track in cached.Tracks)
        {
            RestoreSongFlags(track);
            var row = new SongItemViewModel(
                track.Song,
                _player.PlayFromList,
                index++,
                queue,
                track.Song.Source == MusicSource.NetEase ? _api : null,
                Name);
            row.IsPlayable = track.IsPlayable;
            Songs.Add(row);
        }
        _isLoading = false;
        RestorePageScrollState(snapshot.ScrollOffset);
    }

    internal bool HasRetainedPageData => _albumId != 0 || _albumMid.Length > 0 || Songs.Count > 0;

    internal int RetainedTrackCount => Songs.Count;

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

    private static void RestoreSongFlags(NavigationPageCacheTrack track)
    {
        track.Song.IsPlaybackUnavailable = track.IsPlaybackUnavailable;
        track.Song.PreferCachedPlayback = track.PreferCachedPlayback;
    }

    /// <summary>清空上一张专辑的内容。专辑页每次进入都重建视图并立即绑定现有内容,
    /// 不清的话新页面会先渲染出上一张专辑的曲目(加载失败时更会整页停在错位数据上)。</summary>
    private void ClearContent()
    {
        Name = "";
        ArtistName = "";
        TrackCountText = "";
        PublishTimeMs = 0;
        Description = "";
        PrimaryArtist = null;
        CoverUrl = "";
        Songs.Clear();
    }
}
