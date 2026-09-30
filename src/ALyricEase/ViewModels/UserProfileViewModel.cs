using System.Collections.ObjectModel;
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
/// 数据轻量,导航快照只记 source/uid/昵称/滚动位,恢复时重新拉取。</summary>
public sealed partial class UserProfileViewModel : NavigationDetailViewModelBase
{
    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly PlayerViewModel _player;
    private MusicSource _source = MusicSource.NetEase;
    private long _userId;
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;

    public UserProfileViewModel(NetEaseApiClient api, QQMusicApiClient qqApi, PlayerViewModel player)
    {
        _api = api;
        _qqApi = qqApi;
        _player = player;
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
    public ObservableCollection<UserTasteRowViewModel> TasteRows { get; } = new();

    /// <summary>参与创作的歌单(创建者 = 该用户)。</summary>
    public ObservableCollection<PlaylistItemViewModel> CreatedPlaylists { get; } = new();

    /// <summary>收藏的歌单(创建者 ≠ 该用户)。</summary>
    public ObservableCollection<PlaylistItemViewModel> CollectedPlaylists { get; } = new();

    private (int Generation, CancellationToken Token) BeginLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        return (++_loadGeneration, _loadCancellation.Token);
    }

    private bool IsCurrentLoad(int generation, CancellationToken token)
        => generation == _loadGeneration && !token.IsCancellationRequested;

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
                        TasteRows.Add(new UserTasteRowViewModel(pvm, ShowHeart: true));
                        break;
                    case 20:
                        TasteRows.Add(new UserTasteRowViewModel(pvm, ShowHeart: false));
                        break;
                    default:
                        if (item.Creator?.UserId == uid) CreatedPlaylists.Add(pvm);
                        else CollectedPlaylists.Add(pvm);
                        break;
                }
            }
            HasTaste = TasteRows.Count > 0;
            HasCreated = CreatedPlaylists.Count > 0;
            HasCollected = CollectedPlaylists.Count > 0;
            IsLoading = false;
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
            foreach (var p in playlists)
            {
                var pvm = new PlaylistItemViewModel(p);
                if (p.DirId == QQMusicApiClient.LikedDirId)
                    TasteRows.Add(new UserTasteRowViewModel(pvm, ShowHeart: true));
                else if (p.DirId != 0)
                    CreatedPlaylists.Add(pvm);
                else
                    CollectedPlaylists.Add(pvm);
            }
            HasTaste = TasteRows.Count > 0;
            HasCreated = CreatedPlaylists.Count > 0;
            HasCollected = CollectedPlaylists.Count > 0;
            IsLoading = false;
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

    private void ClearContent()
    {
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
        // 用户页数据轻量:不落音乐缓存,恢复时按 source/uid 重新拉取
        if (snapshot.Source == MusicSource.QQ)
            await LoadQqAsync();
        else
            await LoadAsync(snapshot.Id);
        RestorePageScrollState(snapshot.ScrollOffset);
    }

    internal void DiscardNavigationSnapshot(DetailNavigationSnapshot? snapshot)
    {
        // 用户页快照只记 uid,无 DetailPageSnapshot 缓存可清理
    }

    /// <summary>是否有保留的页面数据(被其他详情页顶掉时决定是否值得进导航历史)。</summary>
    internal bool HasRetainedPageData => _userId != 0 || TasteRows.Count > 0 || CreatedPlaylists.Count > 0
        || CollectedPlaylists.Count > 0 || Nickname.Length > 0;
}
