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

/// <summary>名单页顶部 Tab(关注 / 粉丝)。Count 取用户页上的计数,同时用于空态判断。</summary>
public sealed class UserRelationTabViewModel
{
    public UserRelationTabViewModel(UserRelationKind kind, string label, int count)
    {
        Kind = kind;
        Label = label;
        Count = count;
    }

    public UserRelationKind Kind { get; }

    public string Label { get; }

    public int Count { get; }

    public string CountText => Count.ToString("N0");
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

/// <summary>用户页"关注 · 粉丝"点进来的名单页:顶部 Tabs 在"关注"/"粉丝"两份名单间切换,
/// 服务端分页流式加载,滚动近底部增量补页,点行进入对方用户页。双音源:
/// 网易云 getfollows/getfolloweds;QQ music.concern.RelationList(需登录,
/// 关注 = 用户 + 歌手两份拼起来,与主页 follownum = followusernum + followsingernum 对齐)。
/// 两份名单在页内各自保留已加载内容与翻页位置,切回时不重复联网。</summary>
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

    /// <summary>两份名单各自的数据/分页状态;页内切换时互不覆盖。</summary>
    private readonly RelationState _followingState = new(UserRelationKind.Following);
    private readonly RelationState _followersState = new(UserRelationKind.Followers);
    private RelationState _activeState;
    private bool _suppressTabSelection;

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
        _activeState = _followingState;
    }

    public RangeObservableCollection<UserRelationItemViewModel> Users { get; } = new();

    /// <summary>顶部两个 Tab(关注 / 粉丝),计数显示在文字后的 Fluent 2 计数徽标里。</summary>
    public RangeObservableCollection<UserRelationTabViewModel> Tabs { get; } = new();

    /// <summary>页内标题(固定为"关注与粉丝";顶部标题栏 CurrentPageTitle 也取它)。</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>"来自 {昵称}"(名单页不再区分单份名单,人数由 Tab 徽标展示)。</summary>
    [ObservableProperty] private string _subtitle = "";

    /// <summary>0 = 关注,1 = 粉丝。</summary>
    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty] private bool _isLoading = true;

    [ObservableProperty] private bool _hasUsers;

    /// <summary>当前 Tab 还有下一页(加载失败也会置 false,避免滚动到底无限重试)。</summary>
    [ObservableProperty] private bool _hasMore;

    /// <summary>增量补页进行中(页尾加载指示)。</summary>
    [ObservableProperty] private bool _isLoadingMore;

    [ObservableProperty] private bool _isEmpty;

    [ObservableProperty] private string _emptyText = "";

    /// <summary>切换 Tab:保留已加载内容,未加载过的名单才联网拉首页。</summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_suppressTabSelection) return;
        var next = value switch
        {
            0 => _followingState,
            1 => _followersState,
            _ => null,
        };
        if (next is null || ReferenceEquals(next, _activeState)) return;
        SwitchTab(next);
    }

    /// <summary>进入页面时调用:清掉上一个人的名单,按 kind 选中对应 Tab 并拉首页。
    /// 两个 total 来自用户页英雄卡,只用于 Tab 徽标与空态判断。</summary>
    public async Task LoadAsync(MusicSource source, long userId, string encryptUin,
        UserRelationKind kind, string ownerName, int followingTotal, int followersTotal)
    {
        BeginPageLoad();
        ResetContent(source, userId, encryptUin, kind, ownerName, followingTotal, followersTotal);
        await LoadMoreAsync();
    }

    /// <summary>增量补一页(单飞:在途时返回同一任务)。滚动近底部由视图调用。</summary>
    public Task LoadMoreAsync()
    {
        var state = _activeState;
        if (_loadCancellation is null || !state.HasMore || _loading)
            return _loadTask ?? Task.CompletedTask;
        _loading = true;
        if (state.Rows.Count > 0) IsLoadingMore = true;
        return _loadTask = LoadPageCoreAsync(state, _loadGeneration, _loadCancellation.Token);
    }

    private async Task LoadPageCoreAsync(RelationState state, int generation, CancellationToken ct)
    {
        var hasMore = false;
        try
        {
            IReadOnlyList<UserRelationUser> page;
            int nextOffset;
            var nextStage = state.QqStage;
            if (_source == MusicSource.QQ)
            {
                (page, hasMore, nextOffset, nextStage) =
                    await LoadQqPageAsync(state, generation, ct).ConfigureAwait(true);
                if (!IsCurrentLoad(generation, ct)) return;
            }
            else
            {
                var (users, more) = await _api.GetUserRelationsAsync(
                    _userId, state.Kind == UserRelationKind.Followers, state.Offset, PageSize, ct)
                    .ConfigureAwait(true);
                if (!IsCurrentLoad(generation, ct)) return;
                page = users;
                hasMore = more;
                nextOffset = state.Offset + users.Count;
            }

            // 先确认仍是当前 Tab 再提交分页位置:切 Tab 会取消在途请求,
            // 不能让被取消的翻页把 offset/stage 推进到没有对应行数据的位置。
            if (!ReferenceEquals(state, _activeState)) return;
            state.Offset = nextOffset;
            state.QqStage = nextStage;

            if (page.Count > 0)
            {
                var rows = page.Select(user => new UserRelationItemViewModel(user)).ToList();
                state.Rows.AddRange(rows);
                Users.AddRange(rows);
                HasUsers = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException ex) when (_source == MusicSource.QQ && ex.Code == 1000)
        {
            hasMore = false;
            if (ReferenceEquals(state, _activeState))
                state.EmptyMessage = "QQ 音乐登录已失效，请重新登录";
        }
        catch
        {
            // 网络失败:停止续拉(已加载内容保留),滚到底部不再重复请求
            hasMore = false;
            if (ReferenceEquals(state, _activeState))
                state.EmptyMessage = "加载失败，请检查网络后重试";
        }
        finally
        {
            if (IsCurrentLoad(generation, ct) && ReferenceEquals(state, _activeState))
            {
                state.Loaded = true;
                state.HasMore = hasMore;
                HasMore = hasMore;
                IsEmpty = state.Rows.Count == 0 && !hasMore;
                EmptyText = IsEmpty ? EmptyTextFor(state) : "";
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
    /// 滚动事件不会发生,歌手段永远拉不到。
    /// 返回值里的下一个 offset/stage 由调用方在确认请求仍然有效后提交。</summary>
    private async Task<(IReadOnlyList<UserRelationUser> Page, bool HasMore, int NextOffset, int NextStage)>
        LoadQqPageAsync(RelationState state, int generation, CancellationToken ct)
    {
        if (state.Kind == UserRelationKind.Followers)
        {
            var fans = await _qqApi.GetFansAsync(_encryptUin, state.Offset, PageSize, ct).ConfigureAwait(true);
            if (!IsCurrentLoad(generation, ct)) return ([], false, state.Offset, 0);
            return (fans.Users, fans.HasMore, state.Offset + fans.Users.Count, 0);
        }

        if (state.QqStage == 1)
        {
            var singers = await _qqApi.GetFollowSingersAsync(_encryptUin, state.Offset, PageSize, ct)
                .ConfigureAwait(true);
            if (!IsCurrentLoad(generation, ct)) return ([], false, state.Offset, 1);
            return (singers.Users, singers.HasMore, state.Offset + singers.Users.Count, 1);
        }

        var users = await _qqApi.GetFollowUsersAsync(_encryptUin, state.Offset, PageSize, ct).ConfigureAwait(true);
        if (!IsCurrentLoad(generation, ct)) return ([], false, state.Offset, 0);
        if (users.HasMore)
            return (users.Users, true, state.Offset + users.Users.Count, 0);

        // 用户段到此为止 → 接着拉歌手段第一页,与这批用户合并返回。
        // stage/offset 只在第二段也成功后才提交,避免取消请求后漏掉用户段。
        var firstSingers = await _qqApi.GetFollowSingersAsync(_encryptUin, 0, PageSize, ct)
            .ConfigureAwait(true);
        if (!IsCurrentLoad(generation, ct)) return ([], false, state.Offset, 0);
        var merged = new List<UserRelationUser>(users.Users.Count + firstSingers.Users.Count);
        merged.AddRange(users.Users);
        merged.AddRange(firstSingers.Users);
        return (merged, firstSingers.HasMore, firstSingers.Users.Count, 1);
    }

    private void SwitchTab(RelationState state)
    {
        BeginPageLoad();
        _activeState = state;
        ApplyActiveState();
        // 切换名单回到顶部(滚动控制器会等布局完成后应用)。
        RestorePageScrollState(0);
        if (!state.Loaded)
            _ = LoadMoreAsync();
    }

    private void ApplyActiveState()
    {
        Users.Clear();
        Users.AddRange(_activeState.Rows);
        HasUsers = _activeState.Rows.Count > 0;
        HasMore = _activeState.HasMore;
        IsLoading = !_activeState.Loaded;
        IsLoadingMore = false;
        IsEmpty = _activeState.Loaded && _activeState.Rows.Count == 0 && !_activeState.HasMore;
        EmptyText = IsEmpty ? EmptyTextFor(_activeState) : "";
    }

    private void ResetContent(MusicSource source, long userId, string encryptUin,
        UserRelationKind kind, string ownerName, int followingTotal, int followersTotal)
    {
        _source = source;
        _userId = userId;
        _encryptUin = encryptUin;
        _ownerName = ownerName;

        _followingState.Reset(followingTotal);
        _followersState.Reset(followersTotal);
        _activeState = kind == UserRelationKind.Followers ? _followersState : _followingState;

        _suppressTabSelection = true;
        // 先把选中清成 -1,填完 Tabs 再写目标值:视图可能早于数据创建,
        // 若目标值恰好等于旧值(默认 0),属性不通知,ListBox 就不会把新条目选中。
        SelectedTabIndex = -1;
        Tabs.Clear();
        Tabs.Add(new UserRelationTabViewModel(
            UserRelationKind.Following, "关注", _followingState.Total));
        Tabs.Add(new UserRelationTabViewModel(
            UserRelationKind.Followers, "粉丝", _followersState.Total));
        SelectedTabIndex = _activeState.Kind == UserRelationKind.Followers ? 1 : 0;
        _suppressTabSelection = false;

        Title = "关注与粉丝";
        Subtitle = BuildSubtitle(ownerName);
        EmptyText = "";
        Users.Clear();
        HasUsers = false;
        IsEmpty = false;
        IsLoading = true;
        HasMore = true;
        IsLoadingMore = false;
    }

    /// <summary>空态文案:Tab 上计数 &gt; 0 但一条都拿不到 = 对方没公开这份名单(隐私设置),
    /// 不要说成"还没有";网络失败则保留具体错误文案。</summary>
    private static string EmptyTextFor(RelationState state)
    {
        if (state.EmptyMessage.Length > 0) return state.EmptyMessage;
        if (state.Total > 0) return "对方没有公开这份名单";
        return state.Kind == UserRelationKind.Following ? "还没有关注任何人" : "还没有粉丝";
    }

    private static string BuildSubtitle(string ownerName)
        => ownerName.Length > 0 ? $"来自 {ownerName}" : "";

    internal DetailNavigationSnapshot? CaptureAndReleaseNavigationSnapshot()
    {
        var state = _activeState;
        if (_userId == 0 && _encryptUin.Length == 0)
        {
            ReleaseCurrentPageData();
            return null;
        }

        var snapshot = new DetailNavigationSnapshot(
            Guid.NewGuid().ToString("N"),
            state.Kind == UserRelationKind.Following ? DetailPageKind.UserFollowing : DetailPageKind.UserFollowers,
            _source,
            _userId,
            _source == MusicSource.QQ ? _encryptUin : "",
            _ownerName,
            0,
            "",
            false,
            PageScrollOffset,
            _followingState.Total,
            _followersState.Total);
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
                Offset: state.Offset,
                HasMore: state.HasMore,
                RelationUsers: state.Rows.Count > 0 ? state.Rows.Select(ToCacheUser).ToList() : null,
                RelationStage: state.QqStage));
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
        _followingState.Reset(0);
        _followersState.Reset(0);
        _activeState = _followingState;

        _suppressTabSelection = true;
        Tabs.Clear();
        SelectedTabIndex = 0;
        _suppressTabSelection = false;

        Title = "";
        Subtitle = "";
        EmptyText = "";
        Users.Clear();
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
        ResetContent(snapshot.Source, snapshot.Id, snapshot.Mid, kind, snapshot.Name,
            snapshot.RelationFollowingTotal, snapshot.RelationFollowersTotal);

        var cached = await _musicCache.TryTakeDetailPageSnapshotAsync(snapshot.CacheKey);
        if (!IsCurrentLoad(generation, ct)) return;
        if (cached?.RelationUsers is { } users)
        {
            var state = _activeState;
            state.Offset = Math.Max(0, cached.Offset);
            state.QqStage = cached.RelationStage;
            state.HasMore = cached.HasMore;
            foreach (var user in users) state.Rows.Add(FromCacheUser(user));
            state.Loaded = true;
            ApplyActiveState();
            RestorePageScrollState(snapshot.ScrollOffset);
            return;
        }

        // 没有可用快照(被淘汰/未完成/首屏失败)时重新拉首页,再还原滚动位置。
        // LoadAsync 同步段就会 BeginPageLoad(换 generation),所以在 await 之前取基准。
        var fallbackLoad = LoadAsync(snapshot.Source, snapshot.Id, snapshot.Mid, kind, snapshot.Name,
            snapshot.RelationFollowingTotal, snapshot.RelationFollowersTotal);
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

    internal bool HasRetainedPageData => _userId != 0 || _encryptUin.Length > 0
        || _followingState.Rows.Count > 0 || _followersState.Rows.Count > 0;

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

    /// <summary>一份名单在页内的完整状态:行数据、分页位置、QQ 关注两段式阶段、空态文案。</summary>
    private sealed class RelationState
    {
        public RelationState(UserRelationKind kind) => Kind = kind;

        public UserRelationKind Kind { get; }

        public List<UserRelationItemViewModel> Rows { get; } = new();

        public int Offset { get; set; }

        /// <summary>QQ 关注名单的翻页段(0 = 关注用户,1 = 关注歌手);网易云不用。</summary>
        public int QqStage { get; set; }

        /// <summary>用户页上的计数(只用于徽标与空态判断;隐私账号的名单可能少于它)。</summary>
        public int Total { get; set; }

        public bool HasMore { get; set; } = true;

        /// <summary>首页已请求过(成功、空或失败),再次切回时不再自动重试。</summary>
        public bool Loaded { get; set; }

        /// <summary>首屏失败的具体文案(网络错误/未登录);成功加载后为空。</summary>
        public string EmptyMessage { get; set; } = "";

        public void Reset(int total)
        {
            Rows.Clear();
            Offset = 0;
            QqStage = 0;
            Total = Math.Max(0, total);
            HasMore = true;
            Loaded = false;
            EmptyMessage = "";
        }
    }
}
