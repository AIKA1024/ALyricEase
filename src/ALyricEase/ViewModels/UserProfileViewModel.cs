using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>音乐品味行:特殊歌单以瓦片展示。ShowHeart=true = 喜欢的音乐(封面 + 大♥叠加);
/// false = 年度歌单(封面瓦片)。</summary>
public sealed record UserTasteRowViewModel(PlaylistItemViewModel Playlist, bool ShowHeart)
{
    public string TrackCountText => $"{Playlist.Playlist.TrackCount}首歌曲";
}

/// <summary>用户页(原版 UWP 结构):英雄卡(头像采样背景/昵称/关注粉丝)+ 三个分组
/// —— 音乐品味(喜欢的音乐/年度歌单,图标瓦片行)、参与创作的歌单、收藏的歌单(封面行)。
/// 双音源:网易云 uid=0 表示"自己"(按登录态解析),创建者按钮带创建者 id 跳入;
/// QQ 仅"自己"(QQ 歌单创建者恒为登录账号,协议无他人主页跳转入口),按登录态解析。
/// 导航历史只保留定位参数；页面数据走有界的一次性内存快照，返回无需重复联网。</summary>
public sealed partial class UserProfileViewModel : NavigationDetailViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly PlayerViewModel _player;
    private readonly MusicCacheService _musicCache;
    private bool _contentLoaded;
    private int _accountGeneration;
    private int _playlistRevision;
    private MusicSource _source = MusicSource.NetEase;
    private long _userId;
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;

    internal event Action<Exception>? QqLoadFailed;

    public UserProfileViewModel(NetEaseApiClient api, QQMusicApiClient qqApi, PlayerViewModel player, MusicCacheService musicCache)
    {
        _api = api;
        _qqApi = qqApi;
        _player = player;
        _musicCache = musicCache;
    }

    [ObservableProperty] private string _nickname = "";
    [ObservableProperty] private string _avatarUrl = "";
    [ObservableProperty] private string _signature = "";
    [ObservableProperty] private int _level;
    [ObservableProperty] private int _follows;
    [ObservableProperty] private int _followeds;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _hasTaste;
    [ObservableProperty] private bool _hasCreated;
    [ObservableProperty] private bool _hasCollected;

    /// <summary>等级徽标文本("Lv.8";0 级为空)。</summary>
    public string LevelText => Level > 0 ? $"Lv.{Level}" : "";

    partial void OnLevelChanged(int value) => OnPropertyChanged(nameof(LevelText));

    /// <summary>音乐品味:喜欢的音乐(specialType 5)+ 年度歌单(specialType 20)。
    /// 听歌排行未收录:play/record 的 allData 上限 100 首,拿不到真实"累计播放"总数。</summary>
    public RangeObservableCollection<UserTasteRowViewModel> TasteRows { get; } = new();

    /// <summary>参与创作的歌单(创建者 = 该用户)。</summary>
    public RangeObservableCollection<PlaylistItemViewModel> CreatedPlaylists { get; } = new();

    /// <summary>收藏的歌单(创建者 ≠ 该用户)。</summary>
    public RangeObservableCollection<PlaylistItemViewModel> CollectedPlaylists { get; } = new();

    /// <summary>歌单被重命名/删除后同步"参与创作"卡片(管理入口只对本人歌单出现,
    /// 收藏行与音乐品味行不涉及):fresh 非空 = 换侧栏刷新出的新实例(Playlist init-only,
    /// 名字/封面随实例更新);null = 移除该行。歌单不在本页时无操作。</summary>
    internal void SyncCreatedPlaylist(Models.Playlist target, PlaylistItemViewModel? fresh)
    {
        _playlistRevision++;
        for (var i = 0; i < CreatedPlaylists.Count; i++)
        {
            var p = CreatedPlaylists[i].Playlist;
            if (p.Id != target.Id || p.Source != target.Source) continue;
            if (fresh is not null) CreatedPlaylists[i] = fresh;
            else CreatedPlaylists.RemoveAt(i);
            return;
        }
    }

    private (int Generation, CancellationToken Token) BeginLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        _accountGeneration = CurrentAccountGeneration;
        IsLoading = true;
        return (++_loadGeneration, _loadCancellation.Token);
    }

    private bool IsCurrentLoad(int generation, CancellationToken token)
        => generation == _loadGeneration && !token.IsCancellationRequested;

    private int CurrentAccountGeneration => _source == MusicSource.QQ ? _qqApi.AccountGeneration : _api.AccountGeneration;

    private void CancelCurrentLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    /// <summary>打开用户页:uid=0 视为"自己"(按登录态解析;未登录时页面停在空态)。</summary>
    public async Task LoadAsync(long uid)
    {
        _source = MusicSource.NetEase;
        var (generation, ct) = BeginLoad();
        ClearContent();
        try
        {
            if (uid == 0)
            {
                var self = await _api.GetUserProfileAsync(ct);
                if (!IsCurrentLoad(generation, ct)) return;
                uid = self.UserId;
            }
            _userId = uid;

            var detail = await _api.GetUserDetailAsync(uid, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            Nickname = detail.Profile?.Nickname ?? "";
            AvatarUrl = detail.Profile?.AvatarUrl ?? "";
            Signature = detail.Profile?.Signature ?? "";
            Level = detail.Level;
            Follows = detail.Profile?.Follows ?? 0;
            Followeds = detail.Profile?.Followeds ?? 0;

            // user/playlist 一次拉全(含特殊歌单),按 specialType/创建者分三组:
            // specialType 5 = 喜欢的音乐,20 = 年度歌单 → 音乐品味;
            // 其余按 creator.userId 归入 参与创作 / 收藏。
            var items = await _api.GetUserPlaylistItemsAsync(uid, 1000, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            var taste = new List<UserTasteRowViewModel>();
            var created = new List<PlaylistItemViewModel>();
            var collected = new List<PlaylistItemViewModel>();
            foreach (var item in items)
            {
                var pvm = new PlaylistItemViewModel(new Playlist
                {
                    Id = item.Id,
                    Name = item.Name,
                    CoverUrl = item.CoverUrl,
                    TrackCount = item.TrackCount,
                    PlayCount = item.PlayCount,
                    Source = MusicSource.NetEase,
                })
                {
                    CreatorName = item.Creator?.Nickname,
                    CreatorId = item.Creator?.UserId ?? 0,
                };
                switch (item.SpecialType)
                {
                    case 5:
                        taste.Add(new UserTasteRowViewModel(pvm, ShowHeart: true));
                        break;
                    case 20:
                        taste.Add(new UserTasteRowViewModel(pvm, ShowHeart: false));
                        break;
                    default:
                        if (item.Creator?.UserId == uid) created.Add(pvm);
                        else collected.Add(pvm);
                        break;
                }
            }
            ApplyContent(taste, created, collected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // 网络失败静默:停在空态,不崩
            if (IsCurrentLoad(generation, ct)) IsLoading = false;
        }
    }

    /// <summary>打开 QQ 自己的用户页:头像/昵称/等级走账号摘要,歌单与侧栏同源。
    /// 分组口径:我喜欢(dirId=201)进音乐品味行;有 dirId = 自建;无 dirId = 收藏的他人歌单。
    /// 关注/粉丝 QQ 侧无轻量协议,保持 0(页面照常渲染)。</summary>
    public async Task LoadQqAsync()
    {
        _source = MusicSource.QQ;
        var (generation, ct) = BeginLoad();
        ClearContent();
        try
        {
            var summary = await _qqApi.GetAccountSummaryAsync(ct);
            if (!IsCurrentLoad(generation, ct)) return;
            _userId = summary.UserId;
            Nickname = summary.Nickname;
            AvatarUrl = summary.AvatarUrl;
            Level = summary.AccountLevel; // QQ 音乐等级,0 级徽标自动隐藏

            var playlists = await _qqApi.GetUserPlaylistsAsync(ct);
            if (!IsCurrentLoad(generation, ct)) return;
            var taste = new List<UserTasteRowViewModel>();
            var created = new List<PlaylistItemViewModel>();
            var collected = new List<PlaylistItemViewModel>();
            foreach (var p in playlists)
            {
                var pvm = new PlaylistItemViewModel(p);
                if (p.DirId == QQMusicApiClient.LikedDirId)
                    taste.Add(new UserTasteRowViewModel(pvm, ShowHeart: true));
                else if (p.DirId != 0)
                    created.Add(pvm);
                else
                    collected.Add(pvm);
            }
            ApplyContent(taste, created, collected);

            // 播放量补拉:QQ asset 列表通道 play_cnt 恒 0 不下发(网易云 user/playlist 天然带),
            // 详情通道才有真实值;后台静默按块补,完成逐个原地更新角标
            _ = FillQqPlayCountsAsync(playlists, generation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentLoad(generation, ct)) return;
            IsLoading = false;
            QqLoadFailed?.Invoke(ex);
        }
    }

    /// <summary>QQ 歌单播放量后台补拉:按块多模块请求,完成后逐行原地更新(不改集合结构)。</summary>
    private async Task FillQqPlayCountsAsync(List<Playlist> playlists, int generation, CancellationToken ct)
    {
        try
        {
            var counts = await _qqApi.GetPlaylistPlayCountsAsync(playlists.Select(p => p.Id), ct)
                .ConfigureAwait(true);
            if (!IsCurrentLoad(generation, ct)) return;
            foreach (var pvm in TasteRows.Select(t => t.Playlist)
                         .Concat(CreatedPlaylists)
                         .Concat(CollectedPlaylists))
            {
                if (counts.TryGetValue(pvm.Id, out var n) && n != pvm.Playlist.PlayCount)
                    pvm.UpdatePlayCount(n);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // 播放量缺失不影响页面
        }
    }

    private void ApplyContent(IReadOnlyList<UserTasteRowViewModel> taste,
        IReadOnlyList<PlaylistItemViewModel> created, IReadOnlyList<PlaylistItemViewModel> collected)
    {
        TasteRows.AddRange(taste);
        CreatedPlaylists.AddRange(created);
        CollectedPlaylists.AddRange(collected);
        HasTaste = TasteRows.Count > 0;
        HasCreated = CreatedPlaylists.Count > 0;
        HasCollected = CollectedPlaylists.Count > 0;
        _contentLoaded = true;
        IsLoading = false;
    }

    private void ClearContent()
    {
        _contentLoaded = false;
        Nickname = "";
        AvatarUrl = "";
        Signature = "";
        Level = 0;
        Follows = 0;
        Followeds = 0;
        HasTaste = HasCreated = HasCollected = false;
        TasteRows.Clear();
        CreatedPlaylists.Clear();
        CollectedPlaylists.Clear();
    }

    internal DetailNavigationSnapshot? CaptureAndReleaseNavigationSnapshot()
    {
        if (_userId == 0)
        {
            ReleaseCurrentPageData();
            return null;
        }

        var snapshot = new DetailNavigationSnapshot(
            Guid.NewGuid().ToString("N"),
            DetailPageKind.User,
            _source,
            _userId,
            "",
            Nickname,
            0,
            "",
            false,
            PageScrollOffset);
        if (_contentLoaded)
            _ = _musicCache.CacheDetailPageSnapshotAsync(snapshot.CacheKey, new DetailPageCacheData([], [], [],
                UserProfile: new UserProfilePageCacheData(Nickname, AvatarUrl, Signature, Level, Follows, Followeds,
                    TasteRows.Select(row => ToCacheItem(row.Playlist, row.ShowHeart)).ToArray(),
                    CreatedPlaylists.Select(row => ToCacheItem(row)).ToArray(),
                    CollectedPlaylists.Select(row => ToCacheItem(row)).ToArray(),
                    _accountGeneration, _playlistRevision)));
        ReleaseCurrentPageData();
        return snapshot;
    }

    internal void ReleaseCurrentPageData()
    {
        CancelCurrentLoad();
        ClearContent();
        _userId = 0;
        _source = MusicSource.NetEase;
        ResetPageScrollState();
    }

    internal async Task RestoreNavigationSnapshotAsync(DetailNavigationSnapshot snapshot)
    {
        _source = snapshot.Source;
        var (generation, ct) = BeginLoad();
        ClearContent();
        _userId = snapshot.Id;
        var cached = (await _musicCache.TryTakeDetailPageSnapshotAsync(snapshot.CacheKey))?.UserProfile;
        if (!IsCurrentLoad(generation, ct)) return;
        if (cached is not null && cached.AccountGeneration == CurrentAccountGeneration
            && cached.PlaylistRevision == _playlistRevision)
        {
            Nickname = cached.Nickname;
            AvatarUrl = cached.AvatarUrl;
            Signature = cached.Signature;
            Level = cached.Level;
            Follows = cached.Follows;
            Followeds = cached.Followeds;
            ApplyContent(cached.Taste.Select(row => new UserTasteRowViewModel(FromCacheItem(row), row.ShowHeart)).ToArray(),
                cached.Created.Select(FromCacheItem).ToArray(), cached.Collected.Select(FromCacheItem).ToArray());
            RestorePageScrollState(snapshot.ScrollOffset);
            return;
        }
        // 未完成的页面、被淘汰的快照或账号/歌单已改变时才重新请求。
        Task reload;
        if (snapshot.Source == MusicSource.QQ)
            reload = LoadQqAsync();
        else
            reload = LoadAsync(snapshot.Id);
        var fallbackGeneration = _loadGeneration;
        await reload;
        if (fallbackGeneration != _loadGeneration || _loadCancellation is null) return;
        RestorePageScrollState(snapshot.ScrollOffset);
    }

    internal void DiscardNavigationSnapshot(DetailNavigationSnapshot? snapshot)
    {
        if (snapshot is not null) _ = _musicCache.DiscardDetailPageSnapshotAsync(snapshot.CacheKey);
    }

    private static UserPlaylistCacheItem ToCacheItem(PlaylistItemViewModel item, bool heart = false)
        => new(item.Playlist, item.CreatorName, item.CreatorId, item.CoverUrl, item.TrackCount, item.CurrentPlayCount, heart);

    private static PlaylistItemViewModel FromCacheItem(UserPlaylistCacheItem item)
    {
        var vm = new PlaylistItemViewModel(item.Playlist) { CreatorName = item.CreatorName, CreatorId = item.CreatorId };
        vm.RefreshCover(item.CoverUrl);
        vm.UpdateTrackCount(item.TrackCount);
        vm.UpdatePlayCount(item.PlayCount);
        return vm;
    }

    /// <summary>是否有保留的页面数据(被其他详情页顶掉时决定是否值得进导航历史)。</summary>
    internal bool HasRetainedPageData => _userId != 0 || TasteRows.Count > 0 || CreatedPlaylists.Count > 0
        || CollectedPlaylists.Count > 0 || Nickname.Length > 0;
}
