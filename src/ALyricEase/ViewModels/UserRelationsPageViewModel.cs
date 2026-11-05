using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ALyricEase.ViewModels;

/// <summary>关系列表类型:关注 / 粉丝。</summary>
public enum UserRelationKind
{
    Following,
    Followers,
}

/// <summary>关注/粉丝名单里的一行(只读展示:头像 + 昵称 + 签名 + 关注/粉丝数)。
/// 点行进对方用户页由视图发起(用户页自己的 uid=0 语义不适用于这里)。
/// QQ 的"关注"里混有歌手(没有用户页,只有歌手页),用 <see cref="SingerMid"/> 区分。</summary>
public sealed class UserRelationItemViewModel : ViewModelBase
{
    public UserRelationItemViewModel(UserRelationUser user)
    {
        User = user;
        Nickname = user.Nickname.Length > 0 ? user.Nickname : $"用户 {user.UserId}";
        MetaText = user.SingerMid.Length > 0
            ? $"歌手 · 粉丝 {user.Followeds}"
            : user.Source == MusicSource.QQ
                ? $"粉丝 {user.Followeds}"
                : $"关注 {user.Follows} · 粉丝 {user.Followeds}";
    }

    /// <summary>原始条目(缓存回写用)。</summary>
    public UserRelationUser User { get; }

    public long UserId => User.UserId;

    public string Nickname { get; }

    public string AvatarUrl => User.AvatarUrl;

    public bool HasAvatar => AvatarUrl.Length > 0;

    public string Signature => User.Signature;

    public MusicSource Source => User.Source;

    /// <summary>QQ 用户条目:点行打开对方用户页要用的 encrypt_uin。</summary>
    public string EncryptUin => User.EncryptUin;

    /// <summary>QQ 歌手条目:点行打开歌手页要用的 singer mid。</summary>
    public string SingerMid => User.SingerMid;

    public bool IsSinger => SingerMid.Length > 0;

    /// <summary>右侧次要信息(对方的关注/粉丝数;QQ 只有粉丝数)。</summary>
    public string MetaText { get; }

    public bool HasSignature => Signature.Length > 0;
}

/// <summary>用户页"关注 / 粉丝"点进来的名单页:服务端分页流式加载,滚动近底部增量补页,
/// 点行进入对方用户页。双音源:
/// 网易云 getfollows/getfolloweds;QQ music.concern.RelationList(需登录,
/// 关注 = 用户 + 歌手两份拼起来,与主页 follownum = followusernum + followsingernum 对齐)。</summary>
public sealed partial class UserRelationsPageViewModel : NavigationDetailViewModelBase
{
    private const int PageSize = 30;

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly MusicCacheService _musicCache;

    private MusicSource _source = MusicSource.NetEase;
    /// <summary>网易云是数字 uid;QQ 是名单所属者的 encrypt_uin(空 = 自己)。</summary>
    private long _userId;
    private string _encryptUin = "";
    private string _ownerName = "";
    private UserRelationKind _kind;
    /// <summary>QQ 关注名单的第二段:0 = 关注用户,1 = 关注歌手(拉完用户自动转歌手)。</summary>
    private int _qqStage;
    /// <summary>用户页上的计数(只用于副标题与空态判断:计数 &gt; 0 但名单为空 = 对方没公开)。</summary>
    private int _total;
    private int _offset;
    private readonly List<UserRelationItemViewModel> _all = new();
    private bool _loading;
    private Task? _loadTask;
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;

    public UserRelationsPageViewModel(
        NetEaseApiClient api, QQMusicApiClient qqApi, MusicCacheService musicCache)
    {
        _api = api;
        _qqApi = qqApi;
        _musicCache = musicCache;
    }

    public RangeObservableCollection<UserRelationItemViewModel> Users { get; } = new();

    /// <summary>页内标题(关注 / 粉丝;顶部标题栏 CurrentPageTitle 也取它)。</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>"来自 {昵称} · 共 N 人"(N 取用户页上的计数;隐私账号的名单可能少于此数)。</summary>
    [ObservableProperty] private string _subtitle = "";

    [ObservableProperty] private bool _isLoading = true;

    [ObservableProperty] private bool _hasUsers;

    /// <summary>还有下一页(加载失败也会置 false,避免滚动到底无限重试)。</summary>
    [ObservableProperty] private bool _hasMore;

    /// <summary>增量补页进行中(页尾加载指示)。</summary>
    [ObservableProperty] private bool _isLoadingMore;

    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty] private string _emptyText = "";

    /// <summary>进入页面时调用:清掉上一个人的名单,拉首页。total 只用于副标题展示。</summary>
    public async Task LoadAsync(MusicSource source, long userId, string encryptUin,
        UserRelationKind kind, string ownerName, int total)
    {
        BeginPageLoad();
        ResetContent(source, userId, encryptUin, kind, ownerName, total);
        await LoadMoreAsync();
    }

    /// <summary>增量补一页(单飞:在途时返回同一任务)。滚动近底部由视图调用。</summary>
    public Task LoadMoreAsync()
    {
        if (_loadCancellation is null || !HasMore || _loading)
            return _loadTask ?? Task.CompletedTask;
        _loading = true;
        IsLoadingMore = true;
        return _loadTask = LoadPageCoreAsync(_loadGeneration, _loadCancellation.Token);
    }

    private async Task LoadPageCoreAsync(int generation, CancellationToken ct)
    {
        var hasMore = false;
        try
        {
            IReadOnlyList<UserRelationUser> page;
            if (_source == MusicSource.QQ)
            {
                (page, hasMore) = await LoadQqPageAsync(generation, ct).ConfigureAwait(true);
                if (!IsCurrentLoad(generation, ct)) return;
            }
            else
            {
                var (users, more) = await _api.GetUserRelationsAsync(
                    _userId, _kind == UserRelationKind.Followers, _offset, PageSize, ct).ConfigureAwait(true);
                if (!IsCurrentLoad(generation, ct)) return;
                page = users;
                hasMore = more;
                _offset += users.Count;
            }

            if (page.Count > 0)
            {
                var rows = page.Select(user => new UserRelationItemViewModel(user)).ToList();
                _all.AddRange(rows);
                Users.AddRange(rows);
                HasUsers = true;
            }
            IsEmpty = !HasUsers && !hasMore;
            if (IsEmpty) EmptyText = EmptyTextFor(_kind, _total);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException ex) when (_source == MusicSource.QQ && ex.Code == 1000)
        {
            IsEmpty = !HasUsers;
            if (IsEmpty) EmptyText = "QQ 音乐登录已失效，请重新登录";
        }
        catch
        {
            // 网络失败:停止续拉(已加载内容保留),滚到底部不再重复请求
            IsEmpty = !HasUsers;
            if (IsEmpty) EmptyText = "加载失败，请检查网络后重试";
        }
        finally
        {
            if (IsCurrentLoad(generation, ct))
            {
                HasMore = hasMore;
                _loading = false;
                IsLoading = false;
                IsLoadingMore = false;
                _loadTask = null;
            }
        }
    }

    /// <summary>QQ 关系名单一页。关注 = 先"关注用户"再"关注歌手"两段拼起来
    /// (主页 follownum 就是两者之和,只取一份会让页面人数对不上)。
    /// 用户段最后一页会和歌手段第一页合并返回 —— 否则用户很少时页面不足一屏,
    /// 滚动事件不会发生,歌手段永远拉不到。</summary>
    private async Task<(IReadOnlyList<UserRelationUser> Page, bool HasMore)> LoadQqPageAsync(
        int generation, CancellationToken ct)
    {
        if (_kind == UserRelationKind.Followers)
        {
            var fans = await _qqApi.GetFansAsync(_encryptUin, _offset, PageSize, ct).ConfigureAwait(true);
            if (!IsCurrentLoad(generation, ct)) return ([], false);
            _offset += fans.Users.Count;
            return (fans.Users, fans.HasMore);
        }

        if (_qqStage == 1)
        {
            var singers = await _qqApi.GetFollowSingersAsync(_encryptUin, _offset, PageSize, ct).ConfigureAwait(true);
            if (!IsCurrentLoad(generation, ct)) return ([], false);
            _offset += singers.Users.Count;
            return (singers.Users, singers.HasMore);
        }

        var users = await _qqApi.GetFollowUsersAsync(_encryptUin, _offset, PageSize, ct).ConfigureAwait(true);
        if (!IsCurrentLoad(generation, ct)) return ([], false);
        _offset += users.Users.Count;
        if (users.HasMore) return (users.Users, true);

        // 用户段到此为止 → 接着拉歌手段第一页,与这批用户合并返回
        _qqStage = 1;
        _offset = 0;
        var firstSingers = await _qqApi.GetFollowSingersAsync(_encryptUin, 0, PageSize, ct).ConfigureAwait(true);
        if (!IsCurrentLoad(generation, ct)) return ([], false);
        _offset = firstSingers.Users.Count;
        var merged = new List<UserRelationUser>(users.Users.Count + firstSingers.Users.Count);
        merged.AddRange(users.Users);
        merged.AddRange(firstSingers.Users);
        return (merged, firstSingers.HasMore);
    }

    private void ResetContent(MusicSource source, long userId, string encryptUin,
        UserRelationKind kind, string ownerName, int total)
    {
        _source = source;
        _userId = userId;
        _encryptUin = encryptUin;
        _kind = kind;
        _ownerName = ownerName;
        _total = total;
        _qqStage = 0;
        Title = kind == UserRelationKind.Following ? "关注" : "粉丝";
        Subtitle = BuildSubtitle(ownerName, total);
        EmptyText = EmptyTextFor(kind, total);
        Users.Clear();
        _all.Clear();
        _offset = 0;
        HasUsers = false;
        IsEmpty = false;
        IsLoading = true;
        HasMore = true;
        IsLoadingMore = false;
    }

    /// <summary>空态文案:计数 &gt; 0 但一条都拿不到 = 对方没公开这份名单(隐私设置),不要说成"还没有"。</summary>
    private static string EmptyTextFor(UserRelationKind kind, int total)
    {
        if (total > 0) return "对方没有公开这份名单";
        return kind == UserRelationKind.Following ? "还没有关注任何人" : "还没有粉丝";
    }

    private static string BuildSubtitle(string ownerName, int total)
    {
        var who = ownerName.Length > 0 ? $"来自 {ownerName}" : "";
        if (total <= 0) return who;
        return who.Length > 0 ? $"{who} · 共 {total} 人" : $"共 {total} 人";
    }

    internal DetailNavigationSnapshot? CaptureAndReleaseNavigationSnapshot()
    {
        if (_userId == 0 && _encryptUin.Length == 0)
        {
            ReleaseCurrentPageData();
            return null;
        }

        var snapshot = new DetailNavigationSnapshot(
            Guid.NewGuid().ToString("N"),
            _kind == UserRelationKind.Following ? DetailPageKind.UserFollowing : DetailPageKind.UserFollowers,
            _source,
            _userId,
            _source == MusicSource.QQ ? _encryptUin : "",
            _ownerName,
            0,
            "",
            false,
            PageScrollOffset);
        CancelCurrentLoad();
        // 名单为空(首屏失败/确实没人)时不写快照:restore 侧会因为没有 RelationUsers 重新拉一次
        _ = _musicCache.CacheDetailPageSnapshotAsync(
            snapshot.CacheKey,
            new DetailPageCacheData(
                [],
                [],
                [],
                Name: _ownerName,
                Subtitle: Subtitle,
                Offset: _offset,
                HasMore: HasMore,
                RelationUsers: _all.Count > 0 ? _all.Select(ToCacheUser).ToList() : null,
                RelationStage: _qqStage));
        ReleaseCurrentPageData();
        return snapshot;
    }

    internal void ReleaseCurrentPageData()
    {
        CancelCurrentLoad();
        _source = MusicSource.NetEase;
        _userId = 0;
        _encryptUin = "";
        _ownerName = "";
        _kind = UserRelationKind.Following;
        _total = 0;
        _qqStage = 0;
        _all.Clear();
        Users.Clear();
        _offset = 0;
        Title = "";
        Subtitle = "";
        EmptyText = "";
        HasUsers = false;
        IsEmpty = false;
        IsLoading = true;
        HasMore = false;
        IsLoadingMore = false;
        ResetPageScrollState();
    }

    internal async Task RestoreNavigationSnapshotAsync(DetailNavigationSnapshot snapshot)
    {
        var kind = snapshot.Kind == DetailPageKind.UserFollowers
            ? UserRelationKind.Followers
            : UserRelationKind.Following;
        BeginPageLoad();
        var generation = _loadGeneration;
        var ct = _loadCancellation!.Token;
        ResetContent(snapshot.Source, snapshot.Id, snapshot.Mid, kind, snapshot.Name, 0);

        var cached = await _musicCache.TryTakeDetailPageSnapshotAsync(snapshot.CacheKey);
        if (!IsCurrentLoad(generation, ct)) return;
        if (cached?.RelationUsers is { } users)
        {
            _offset = Math.Max(0, cached.Offset);
            _qqStage = cached.RelationStage;
            Subtitle = cached.Subtitle;
            HasMore = cached.HasMore;
            foreach (var user in users) _all.Add(FromCacheUser(user));
            Users.AddRange(_all.ToList());
            HasUsers = _all.Count > 0;
            IsEmpty = _all.Count == 0 && !HasMore;
            IsLoading = false;
            RestorePageScrollState(snapshot.ScrollOffset);
            return;
        }

        // 没有可用快照(被淘汰/未完成/首屏失败)时重新拉首页,再还原滚动位置。
        // LoadAsync 同步段就会 BeginPageLoad(换 generation),所以在 await 之前取基准。
        var fallbackLoad = LoadAsync(snapshot.Source, snapshot.Id, snapshot.Mid, kind, snapshot.Name, 0);
        var fallbackGeneration = _loadGeneration;
        await fallbackLoad;
        if (fallbackGeneration != _loadGeneration) return;
        RestorePageScrollState(snapshot.ScrollOffset);
    }

    internal void DiscardNavigationSnapshot(DetailNavigationSnapshot? snapshot)
    {
        if (snapshot is not null)
            _ = _musicCache.DiscardDetailPageSnapshotAsync(snapshot.CacheKey);
    }

    internal bool HasRetainedPageData => _userId != 0 || _encryptUin.Length > 0 || _all.Count > 0;

    private void BeginPageLoad()
    {
        CancelCurrentLoad();
        _loadCancellation = new CancellationTokenSource();
        ResetPageScrollState();
    }

    private void CancelCurrentLoad()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _loading = false;
        _loadTask = null;
        IsLoadingMore = false;
    }

    private bool IsCurrentLoad(int generation, CancellationToken token)
        => generation == _loadGeneration && !token.IsCancellationRequested;

    private static NavigationPageCacheUser ToCacheUser(UserRelationItemViewModel user)
        => new(user.UserId, user.Nickname, user.AvatarUrl, user.Signature,
            user.User.Follows, user.User.Followeds, user.Source, user.EncryptUin, user.SingerMid);

    private static UserRelationItemViewModel FromCacheUser(NavigationPageCacheUser user)
        => new(new UserRelationUser(user.UserId, user.Nickname, user.AvatarUrl, user.Signature,
            user.Follows, user.Followeds, user.Source, user.EncryptUin, user.SingerMid));
}
