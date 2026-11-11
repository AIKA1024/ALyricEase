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
    private bool _followOperationInFlight;

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

    // ---- 关注歌手(两音源统一两态) ----
    [ObservableProperty] private bool _isFollowed;
    [ObservableProperty] private bool _isFollowBusy;
    [ObservableProperty] private bool _isFollowStateLoaded;
    [ObservableProperty] private bool _hasArtist;

    public string FollowLabel => IsFollowed ? "已关注" : "关注";
    public string FollowToolTip => IsFollowed ? "取消关注该歌手" : "关注该歌手";
    /// <summary>有歌手 + 关注态已查完 + 写操作未进行中,才允许点击。</summary>
    public bool CanFollow => HasArtist && IsFollowStateLoaded && !IsFollowBusy;

    partial void OnIsFollowedChanged(bool value)
    {
        OnPropertyChanged(nameof(FollowLabel));
        OnPropertyChanged(nameof(FollowToolTip));
    }
    partial void OnIsFollowBusyChanged(bool value) => OnPropertyChanged(nameof(CanFollow));
    partial void OnIsFollowStateLoadedChanged(bool value) => OnPropertyChanged(nameof(CanFollow));
    partial void OnHasArtistChanged(bool value) => OnPropertyChanged(nameof(CanFollow));

    /// <summary>歌手公开网页链接(头部"更多"菜单的分享 / 在浏览器中打开 / 复制链接共用):
    /// 网易云按数字 id,QQ 按 singer mid;未加载任何歌手时为 null。</summary>
    public string? ShareUrl => _singerMid.Length > 0
        ? $"https://y.qq.com/n/ryqq/singer/{_singerMid}"
        : _artistId != 0
            ? $"https://music.163.com/#/artist?id={_artistId}"
            : null;

    /// <summary>热门歌曲首次拉取中(视图据此保留网格满列高度并显示加载动画,避免容器塌陷后跳高)。</summary>
    [ObservableProperty] private bool _isLoadingSongs;

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
        StartFollowStateLoad(generation, ct);
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
            IsLoadingSongs = false;

            var albums = await _api.GetArtistAlbumsAsync(artistId, 50, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            foreach (var a in albums)
            {
                var card = new AlbumCardViewModel(a.Id, a.Name, a.PicUrl,
                    publishTimeMs: a.PublishTime, songCount: a.Size);
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
            if (IsCurrentLoad(generation, ct))
            {
                _isLoading = false;
                IsLoadingSongs = false;
            }
        }
    }

    /// <summary>进页面后异步拉关注态:不阻塞头像/歌曲/专辑,换歌手用 generation 作废旧结果。</summary>
    private void StartFollowStateLoad(int generation, CancellationToken ct)
    {
        IsFollowed = false;
        IsFollowStateLoaded = false;
        HasArtist = true;
        var isQq = _singerMid.Length > 0;
        var id = _artistId;
        var mid = _singerMid;
        _ = LoadFollowStateAsync(generation, ct, isQq, id, mid);
    }

    private async Task LoadFollowStateAsync(
        int generation, CancellationToken ct, bool isQq, long id, string mid)
    {
        var followed = false;
        try
        {
            followed = isQq
                ? await _qqApi.IsSingerFollowedAsync(mid, ct).ConfigureAwait(true)
                : await _api.IsArtistFollowedAsync(id, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { /* 查询失败保持"未关注",真实结果由写接口给出,不假装已关注 */ }
        if (generation != _loadGeneration) return;
        IsFollowed = followed;
        IsFollowStateLoaded = true;
    }

    /// <summary>关注/取消关注:乐观更新 + 失败回滚(SongItemViewModel 红心同款);
    /// 未登录或凭证失效时引导登录,不留下假的"已关注"。</summary>
    [RelayCommand]
    private async Task ToggleFollowAsync()
    {
        if (!CanFollow || _followOperationInFlight) return;
        _followOperationInFlight = true;
        IsFollowBusy = true;
        var generation = _loadGeneration;
        var previous = IsFollowed;
        var isQq = _singerMid.Length > 0;
        var id = _artistId;
        var mid = _singerMid;
        IsFollowed = !previous;
        try
        {
            if (isQq) await _qqApi.SetSingerFollowedAsync(mid, !previous).ConfigureAwait(true);
            else await _api.SetArtistFollowedAsync(id, !previous).ConfigureAwait(true);
        }
        catch (ApiException ex) when (NeedsFollowLoginPrompt(isQq, ex))
        {
            if (generation == _loadGeneration) IsFollowed = previous;
            TryOpenFollowLogin(isQq, ex);
        }
        catch
        {
            if (generation == _loadGeneration) IsFollowed = previous;
        }
        finally
        {
            _followOperationInFlight = false;
            IsFollowBusy = false;
        }
    }

    /// <summary>只有"真的需要登录"才弹登录窗。QQ 侧写失败过去统一抛 <c>-1</c>（通用失败码），
    /// 结果任何一次写被拒都弹登录框（2026-11-11 用户实测：关注歌手立刻弹窗，但下载/播放都正常）。
    /// 现在 QQ 按 <see cref="QQMusicApiClient.ShouldPromptRelogin"/> 判定，网易云维持原 <c>-1</c> 语义。</summary>
    private static bool NeedsFollowLoginPrompt(bool isQq, ApiException ex)
        => isQq ? QQMusicApiClient.ShouldPromptRelogin(ex.Code) : ex.Code == -1;

    private static void TryOpenFollowLogin(bool isQq, ApiException ex)
    {
        try
        {
            ServiceLocator.Get<MainViewModel>().OpenLoginDialogFor(
                isQq ? MusicSource.QQ : MusicSource.NetEase,
                isQq && QQMusicApiClient.ShouldPromptRelogin(ex.Code)
                    ? QQMusicApiClient.ReloginHintText : null);
        }
        catch { /* Headless / 无宿主环境 */ }
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
        StartFollowStateLoad(generation, ct);
        try
        {
            AvatarUrl = $"https://y.gtimg.cn/music/photo_new/T001R300x300M000{singerMid}.jpg";

            var songs = await _qqApi.GetArtistSongsAsync(singerMid, 30, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            Name = ResolveSingerName(songs, singerMid);
            foreach (var s in songs)
                Songs.Add(new SongItemViewModel(s, _player.PlayFromList, queue: songs, source: Name));
            IsLoadingSongs = false;

            var albums = await _qqApi.GetArtistAlbumsAsync(singerMid, 50, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            var cards = new List<AlbumCardViewModel>();
            foreach (var a in albums)
            {
                var card = new AlbumCardViewModel(a.Id, a.Name, a.PicUrl, a.Mid,
                    publishTimeMs: a.PublishTime, songCount: a.Size);
                cards.Add(card);
                // QQ albumType:录音室专辑/现场专辑等归专辑,EP/单曲归"单曲与EP"
                var isSingle = a.Type.Contains("EP", StringComparison.OrdinalIgnoreCase)
                               || a.Type.Contains("单曲", StringComparison.Ordinal);
                if (isSingle) Singles.Add(card);
                else Albums.Add(card);
            }
            // 列表接口不带曲数(totalNum 恒 0),批量补拉后就地回填卡片信息行
            _ = FetchQqSongCountsAsync(cards, generation, ct);
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
            if (IsCurrentLoad(generation, ct))
            {
                _isLoading = false;
                IsLoadingSongs = false;
            }
        }
    }

    /// <summary>批量补拉 QQ 专辑曲数并回填卡片(列表接口不带曲数)。锦上添花:失败静默,换歌手即作废。</summary>
    private async Task FetchQqSongCountsAsync(
        IReadOnlyList<AlbumCardViewModel> cards, int generation, CancellationToken ct)
    {
        try
        {
            var mids = cards.Select(c => c.Mid).ToList();
            var counts = await _qqApi.GetAlbumSongCountsAsync(mids, ct).ConfigureAwait(true);
            if (!IsCurrentLoad(generation, ct)) return;
            foreach (var card in cards)
                if (counts.TryGetValue(card.Mid, out var count))
                    card.UpdateSongCount(count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // 曲数补拉失败不影响卡片其余信息
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
        StartFollowStateLoad(generation, ct);

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
        IsLoadingSongs = false;
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
        album.Id, album.Title, album.CoverUrl, album.Mid, album.PublishTimeMs, album.SongCount);

    private static AlbumCardViewModel ToAlbumCard(NavigationPageCacheAlbum album) =>
        new(album.Id, album.Title, album.CoverUrl, album.Mid, album.PublishTimeMs, album.SongCount);

    private static void RestoreSongFlags(NavigationPageCacheTrack track)
    {
        track.Song.IsPlaybackUnavailable = track.IsPlaybackUnavailable;
        track.Song.PreferCachedPlayback = track.PreferCachedPlayback;
    }

    /// <summary>清空上一位歌手的内容。歌手页每次进入都重建视图并立即绑定现有内容,
    /// 不清的话新页面会先渲染出上一位歌手的歌(加载失败时更会整页停在错位数据上)。
    /// 同时置回"热门歌曲拉取中":视图保留满列高度显示加载态,等数据到位再收回。</summary>
    private void ClearContent()
    {
        Name = "";
        AvatarUrl = "";
        Songs.Clear();
        Albums.Clear();
        Singles.Clear();
        HasAlbums = false;
        HasSingles = false;
        IsLoadingSongs = true;
        IsFollowed = false;
        IsFollowBusy = false;
        IsFollowStateLoaded = false;
        HasArtist = false;
        _followOperationInFlight = false;
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
