using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

public enum QqLoginMethod
{
    Qr,
    Phone,
    Cookie,
}

/// <summary>网易云登录方式:官方客户端代理登录(捕获客户端登录请求,Windows)与 MUSIC_U 粘贴并列。</summary>
public enum NetEaseLoginMethod
{
    Cookie,
    Proxy,
}

/// <summary>歌单 VM:MUSIC_U 粘贴登录 → 用户歌单 → 点开歌单看曲目(双击播放)。
/// 未登录显示登录卡片;已登录显示用户信息 + 歌单列表 + 选中歌单的曲目。
/// 登录对话框支持双音源切换:网易云(MUSIC_U)与 QQ 音乐(uin+qqmusic_key cookie,解锁 VIP 音质)。</summary>
public sealed partial class PlaylistViewModel : ViewModelBase
{
    internal const int AggregateNetEaseBatchSize = 100;
    internal const int AggregateQqPageSize = 300;
    private const int AggregateUiBatchSize = 50;

    /// <summary>诊断钩子(默认 null,零开销):离页捕获与返回恢复的各阶段耗时。
    /// 由探针挂载后才会创建 Stopwatch,生产路径不付任何成本。</summary>
    internal static Action<string>? RestoreTimingTrace;

    /// <summary>登录恢复完成后"自动打开我喜欢的音乐"的守卫(由 MainViewModel 注入):
    /// 返回 false 时跳过自动打开 —— 启动页改为用户页后,恢复流程不得把用户顶离用户页。</summary>
    public Func<bool>? AutoOpenFavoritesGuard { get; set; }

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qqApi;
    private readonly CookieStore _cookie;
    private readonly PlayerViewModel _player;
    private readonly MusicCacheService _musicCache;
    private bool _netEaseRestoredFromCache;
    private bool _qqRestoredFromCache;

    public PlaylistViewModel(
        NetEaseApiClient api,
        QQMusicApiClient qqApi,
        CookieStore cookie,
        PlayerViewModel player,
        MusicCacheService musicCache)
    {
        _api = api;
        _qqApi = qqApi;
        _cookie = cookie;
        _player = player;
        _musicCache = musicCache;
        Filters = CollectionSortAndFilterViewModel.ForTracks("在歌单中搜索");
        Filters.FilterChanged += OnTrackFiltersChanged;
        RestoreCachedLibraries();
    }

    [ObservableProperty] private string _musicUInput = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _avatarUrl = "";
    [ObservableProperty] private string _playlistTitle = "";
    [ObservableProperty] private PlaylistItemViewModel? _selectedPlaylist;
    [ObservableProperty] private SongItemViewModel? _selectedTrack;

    // ---- 登录对话框双音源 ----

    /// <summary>登录弹窗当前选中的音源页签:false=网易云(默认),true=QQ音乐。</summary>
    [ObservableProperty] private bool _isQQLoginTab;

    /// <summary>网易云登录方式:默认粘贴 MUSIC_U;官方客户端代理登录需 Windows。</summary>
    [ObservableProperty] private NetEaseLoginMethod _netEaseLoginMethod = NetEaseLoginMethod.Cookie;

    /// <summary>官方客户端代理登录:代理是否在监听、监听端口与状态文案。</summary>
    [ObservableProperty] private bool _isNetEaseProxyLoginActive;
    [ObservableProperty] private int _netEaseProxyPort;
    [ObservableProperty] private string _netEaseProxyStatus = "在网易云音乐客户端中设置下方代理并登录";
    private NetEaseProxyLoginService? _netEaseProxyLogin;

    public bool IsNetEaseProxyMethod => NetEaseLoginMethod == NetEaseLoginMethod.Proxy;
    public bool IsNetEaseCookieMethod => NetEaseLoginMethod == NetEaseLoginMethod.Cookie;
    public bool CanUseNetEaseProxyLogin => NetEaseProxyLoginService.IsSupported;
    /// <summary>代理端口框内容:IP 由相邻只读框固定显示 127.0.0.1,这里只出端口号,避免重复。</summary>
    public string NetEaseProxyAddress => NetEaseProxyPort > 0 ? NetEaseProxyPort.ToString() : "—";

    /// <summary>QQ音乐登录输入:y.qq.com 的整段完整 Cookie(客户端解析 uin/qqmusic_key 并原文保存;
    /// 账号接口依赖完整字段,精简两项过不了服务端校验)。</summary>
    [ObservableProperty] private string _qqCookieInput = "";

    /// <summary>QQ 登录方式。默认使用原生扫码，手机号验证码与浏览器 Cookie 作为并列入口。</summary>
    [ObservableProperty] private QqLoginMethod _qqLoginMethod;

    [ObservableProperty] private string _qqPhoneCountryCode = "86";
    [ObservableProperty] private string _qqPhoneNumber = "";
    [ObservableProperty] private string _qqPhoneCode = "";
    [ObservableProperty] private string _qqPhoneStatus = "验证码将发送到你的手机";
    [ObservableProperty] private int _qqPhoneCountdown;
    [ObservableProperty] private bool _isQqPhoneCodeSent;
    private CancellationTokenSource? _qqPhoneOperationCancellation;
    private CancellationTokenSource? _qqPhoneCountdownCancellation;

    /// <summary>QQ 音乐客户端原生登录二维码与当前扫码状态。</summary>
    [ObservableProperty] private IImage? _qqQrImage;
    [ObservableProperty] private bool _isQqQrLoginActive;
    [ObservableProperty] private string _qqQrStatus = "使用手机 QQ 音乐扫描二维码";
    private CancellationTokenSource? _qqQrLoginCancellation;

    public bool HasQqQrImage => QqQrImage is not null;
    public bool IsQqQrLoginMethod => QqLoginMethod == QqLoginMethod.Qr;
    public bool IsQqPhoneLoginMethod => QqLoginMethod == QqLoginMethod.Phone;
    public bool IsQqCookieLoginMethod => QqLoginMethod == QqLoginMethod.Cookie;
    public string QqPhoneSendButtonText => QqPhoneCountdown > 0 ? $"{QqPhoneCountdown} 秒后重试" : "发送验证码";
    public bool CanSendQqPhoneCode => !IsBusy && QqPhoneCountdown == 0;

    partial void OnQqQrImageChanged(IImage? value) => OnPropertyChanged(nameof(HasQqQrImage));

    partial void OnQqLoginMethodChanged(QqLoginMethod value)
    {
        OnPropertyChanged(nameof(IsQqQrLoginMethod));
        OnPropertyChanged(nameof(IsQqPhoneLoginMethod));
        OnPropertyChanged(nameof(IsQqCookieLoginMethod));
        Message = null;
        if (value != QqLoginMethod.Qr) CancelQqQrLogin();
        if (value != QqLoginMethod.Phone)
        {
            var operation = _qqPhoneOperationCancellation;
            _qqPhoneOperationCancellation = null;
            operation?.Cancel();
            IsBusy = false;
        }
    }

    partial void OnQqPhoneCountdownChanged(int value)
    {
        OnPropertyChanged(nameof(QqPhoneSendButtonText));
        OnPropertyChanged(nameof(CanSendQqPhoneCode));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSendQqPhoneCode));

    /// <summary>QQ音乐已登录(本地 cookie 有效;解锁 VIP/320k 播放)。</summary>
    [ObservableProperty] private bool _isQqLoggedIn;

    /// <summary>QQ 登录用户昵称(歌单详情页创建者显示用;拉歌单时顺带缓存)。</summary>
    [ObservableProperty] private string _qqUserName = "";

    /// <summary>歌单详情页 hero 显示的创建者:网易云歌单 = 网易云昵称,QQ 歌单 = QQ 昵称。</summary>
    [ObservableProperty] private string _creatorName = "";

    /// <summary>当前展示歌单的创建者用户 id(v6 接口返回;0 = 未知/QQ 歌单,创建者按钮禁用)。</summary>
    [ObservableProperty] private long _creatorId;

    /// <summary>创建者按钮可点(有可跳转的用户 id)。</summary>
    public bool CanOpenCreator => CreatorId > 0;

    partial void OnCreatorIdChanged(long value) => OnPropertyChanged(nameof(CanOpenCreator));

    /// <summary>创建者按钮 → 用户页(经 MainViewModel 统一导航;无头/探针环境静默)。</summary>
    [RelayCommand]
    private void OpenCreator()
    {
        if (CreatorId <= 0) return;
        try { ServiceLocator.Get<MainViewModel>().OpenUserCommand.Execute(CreatorId); }
        catch { /* SelfTest/Headless 等无宿主环境 */ }
    }

    public bool IsNetEaseLoginTab => !IsQQLoginTab;
    partial void OnIsQQLoginTabChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNetEaseLoginTab));
        if (!value) CancelLoginActivities();
        else CancelNetEaseProxyLogin(); // 切到 QQ 页签时网易云代理一并停止
    }

    [RelayCommand] private void SelectNetEaseLoginTab() => IsQQLoginTab = false;

    [RelayCommand] private void SelectQQLoginTab() => IsQQLoginTab = true;

    [RelayCommand] private void SelectQqQrLoginMethod() => QqLoginMethod = QqLoginMethod.Qr;

    [RelayCommand] private void SelectQqPhoneLoginMethod() => QqLoginMethod = QqLoginMethod.Phone;

    [RelayCommand] private void SelectQqCookieLoginMethod() => QqLoginMethod = QqLoginMethod.Cookie;

    [RelayCommand] private void SelectNetEaseProxyMethod() => NetEaseLoginMethod = NetEaseLoginMethod.Proxy;

    [RelayCommand] private void SelectNetEaseCookieMethod() => NetEaseLoginMethod = NetEaseLoginMethod.Cookie;

    partial void OnNetEaseLoginMethodChanged(NetEaseLoginMethod value)
    {
        OnPropertyChanged(nameof(IsNetEaseProxyMethod));
        OnPropertyChanged(nameof(IsNetEaseCookieMethod));
        Message = null;
        if (value != NetEaseLoginMethod.Proxy)
        {
            CancelNetEaseProxyLogin();
            return;
        }
        // 对齐 Cirrus 的体验:切到代理方式即自动起代理,端口立刻可见,
        // 不留"地址显示 — 还得再点一下启动"的空档。失败时按钮仍在,可手动重试。
        if (CanUseNetEaseProxyLogin && !IsNetEaseProxyLoginActive)
            StartNetEaseProxyLogin();
    }

    /// <summary>启动本地 MITM 代理开始监听官方客户端登录;端口与状态经属性呈现给引导 UI。</summary>
    [RelayCommand]
    private void StartNetEaseProxyLogin()
    {
        if (!CanUseNetEaseProxyLogin || IsNetEaseProxyLoginActive) return;
        Message = null;
        try
        {
            IProgress<NetEaseProxyLoginUpdate> progress =
                new Progress<NetEaseProxyLoginUpdate>(OnNetEaseProxyLoginUpdate);
            _netEaseProxyLogin?.Dispose();
            _netEaseProxyLogin = new NetEaseProxyLoginService(_api);
            NetEaseProxyPort = _netEaseProxyLogin.Start(progress.Report);
            OnPropertyChanged(nameof(NetEaseProxyAddress));
        }
        catch (Exception ex)
        {
            _netEaseProxyLogin?.Dispose();
            _netEaseProxyLogin = null;
            NetEaseProxyStatus = "代理启动失败";
            Message = $"代理登录启动失败:{ex.Message}";
        }
    }

    /// <summary>代理登录状态推进(Progress 已封送到 UI 线程)。
    /// Ready 时登录态已由服务持久化,这里复用粘贴登录的完整加载链路 —— IsLoggedIn 翻 true
    /// 后 AppShell 自动关闭登录弹窗,与 Cookie 登录同一出口。</summary>
    private void OnNetEaseProxyLoginUpdate(NetEaseProxyLoginUpdate update)
    {
        switch (update.Stage)
        {
            case NetEaseProxyLoginStage.Listening:
                IsNetEaseProxyLoginActive = true;
                NetEaseProxyStatus = "代理已就绪,请在网易云音乐客户端设置代理并登录";
                break;
            case NetEaseProxyLoginStage.Verifying:
                NetEaseProxyStatus = "已捕获登录凭证,正在验证…";
                break;
            case NetEaseProxyLoginStage.Ready:
                IsNetEaseProxyLoginActive = false;
                NetEaseProxyStatus = $"已登录:{update.Nickname}";
                break;
            case NetEaseProxyLoginStage.Rejected:
                NetEaseProxyStatus = "凭证验证未通过,继续等待客户端请求…";
                break;
        }
        if (update.Error is { } error)
            Message = update.Stage == NetEaseProxyLoginStage.Rejected
                ? $"捕获的凭证无效:{error}"
                : $"代理登录异常:{error}";
        if (update.Stage == NetEaseProxyLoginStage.Ready)
        {
            CompleteNetEaseProxyLogin();
            _ = LoadProfileAndPlaylistsAsync(); // 网络失败时此方法自己兜底提示,不吞登录态
        }
    }

    /// <summary>登录成功后收尾:停掉代理,提醒用户还原官方客户端的代理设置。</summary>
    private void CompleteNetEaseProxyLogin()
    {
        CancelNetEaseProxyLogin();
        NetEaseProxyStatus = "完成!请记得关闭网易云音乐客户端中的代理设置";
    }

    /// <summary>关闭弹层/切换登录方式时停止代理监听;再次启动会分配新端口。</summary>
    public void CancelNetEaseProxyLogin()
    {
        var service = _netEaseProxyLogin;
        _netEaseProxyLogin = null;
        service?.Dispose();
        IsNetEaseProxyLoginActive = false;
        NetEaseProxyPort = 0;
        OnPropertyChanged(nameof(NetEaseProxyAddress));
        NetEaseProxyStatus = "在网易云音乐客户端中设置下方代理并登录";
    }

    public bool ShowLogin => !IsLoggedIn;

    /// <summary>歌单详情页内容可见性:任一音源登录即可(只登 QQ 时网易云未登录也要能看 QQ 歌单)。</summary>
    public bool HasAnyLogin => IsLoggedIn || IsQqLoggedIn;

    /// <summary>本地是否存有任一音源的登录凭证(CookieStore 在构造函数同步读盘,构造期即可用)。
    /// 区别于 HasAnyLogin(异步恢复后的在线登录态):决定启动页 —— 有凭证走用户页(等异步恢复),
    /// 完全没有则启动即打开账号页+登录弹层。</summary>
    public bool HasStoredCredentials =>
        _cookie.MusicU is { Length: > 0 } || _cookie.QQCookieRaw is { Length: > 0 };

    /// <summary>登录后保留歌单页的“请选择”空态；未登录时只要从推荐/搜索打开了公共歌单也应显示详情。</summary>
    public bool ShowPlaylistContent => HasAnyLogin || SelectedPlaylist is not null;

    public ObservableCollection<PlaylistItemViewModel> Playlists { get; } = new();

    /// <summary>网易云里收藏的他人歌单(Subscribed=true):不进侧栏分组,「我的收藏」页消费。
    /// 离线缓存无创建者信息,恢复期间收藏歌单暂留侧栏,在线刷新后纠正。</summary>
    public ObservableCollection<PlaylistItemViewModel> NetEaseCollectedPlaylists { get; } = new();

    /// <summary>按 Subscribed 拆分网易云用户歌单:自己创建的进侧栏(Playlists),
    /// 收藏的他人歌单进「我的收藏」页。替换两个集合的全部内容。</summary>
    private void SplitNetEasePlaylists(System.Collections.Generic.IEnumerable<Models.Dtos.LegacyPlaylistItem> items)
    {
        Playlists.Clear();
        NetEaseCollectedPlaylists.Clear();
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
                CanAddTracks = !item.Subscribed,
            })
            {
                CreatorName = item.Creator?.Nickname,
                CreatorId = item.Creator?.UserId ?? 0,
            };
            if (item.Subscribed) NetEaseCollectedPlaylists.Add(pvm);
            else Playlists.Add(pvm);
        }
    }

    /// <summary>QQ 登录用户的歌单(侧边栏"QQ音乐"分组;一次全量拉取,失败静默可重试)。</summary>
    public ObservableCollection<PlaylistItemViewModel> QqPlaylists { get; } = new();

    /// <summary>把歌曲追加到选定歌单，按歌单音源路由至对应账号客户端。</summary>
    public Task AddSongToPlaylistAsync(Playlist playlist, Song song)
        => playlist.Source == MusicSource.QQ
            ? _qqApi.AddSongToPlaylistAsync(playlist, song)
            : _api.AddSongToPlaylistAsync(playlist, song);

    /// <summary>歌曲行菜单"从歌单中移除"(仅自己的歌单注入行,见 AttachRemoveCommandIfNeeded):
    /// 服务端删曲成功后原地摘行。行列表是 _trackIds 的前缀窗口,同步摘除 trackId 并回退 _materialized;
    /// 播放队列整体重建(懒队列持 _trackIds 同一引用,原地改会让缓存索引错位),
    /// 删除点之后的行原地改绑播放委托并重排序号 —— 不重建行容器,避免封面重载(同 ApplyFreshRows 口径)。</summary>
    [RelayCommand]
    private async Task RemoveSongFromPlaylistAsync(SongItemViewModel row)
    {
        if (SelectedPlaylist is null || !CanRemoveTracksNow) return;
        var playlist = SelectedPlaylist.Playlist;
        try
        {
            if (playlist.Source == MusicSource.QQ)
                await _qqApi.RemoveSongFromPlaylistAsync(playlist, row.Song);
            else
                await _api.RemoveSongFromPlaylistAsync(playlist, row.Song);
        }
        catch (Exception ex) when (ex is ApiException || IsConnectivityFailure(ex))
        {
            Message = ex is ApiException { Code: NetEaseApiClient.ThrottledCode }
                ? "网易云限速中,请稍后再试"
                : $"移除失败:{ex.Message}";
            return;
        }

        var rowIndex = _allTrackRows.IndexOf(row);
        if (rowIndex < 0) return;
        _allTrackRows.RemoveAt(rowIndex);
        _queueSongs.Remove(row.Song);
        _known.Remove(row.Song.Id);
        if (rowIndex < _trackIds.Count && _trackIds[rowIndex] == row.Song.Id)
        {
            _trackIds.RemoveAt(rowIndex);
            if (rowIndex < _materialized) _materialized--;
        }
        if (_playbackQueue is not null)
            _playbackQueue = new IndexedSongQueue(_trackIds, _queueSongs, _api.GetSongsByIdsAsync);

        // 删除点之后的行:序号 -1,播放委托改绑到新队列/新下标(删除点之前的行下标未变,不用动)
        var lazyQueue = _playbackQueue;
        for (var i = rowIndex; i < _allTrackRows.Count; i++)
        {
            var remaining = _allTrackRows[i];
            remaining.Renumber(i + 1);
            if (lazyQueue is not null)
            {
                var queue = lazyQueue;
                var lazyIndex = i;
                remaining.RebindPlayback(
                    (candidate, _, _) => _player.PlayFromLazyList(
                        candidate, queue, lazyIndex, _queueSongs, PlaylistTitle),
                    null, PlaylistTitle);
            }
            else
            {
                remaining.RebindPlayback(
                    (candidate, _, source) => _player.PlayFromList(candidate, _queueSongs, source),
                    _queueSongs, PlaylistTitle);
            }
        }
        RefreshVisibleTracks();
        Message = $"已从「{playlist.Name}」移除「{row.Name}」";
    }

    private bool _qqPlaylistsLoaded;

    public RangeObservableCollection<SongItemViewModel> Tracks { get; } = new();

    private readonly List<SongItemViewModel> _allTrackRows = new();
    private Task? _loadAllForFilterTask;
    private double _pageScrollOffset;
    private double _pendingScrollRestoreOffset;

    /// <summary>递增即表示视图应在下一次布局后恢复滚动位置。</summary>
    [ObservableProperty] private int _scrollRestoreVersion;

    public CollectionSortAndFilterViewModel Filters { get; }

    /// <summary>登录态变化后重算各曲目行可播性(登录成会员后 VIP 歌曲行恢复可点)。</summary>
    public void RefreshPlayability()
    {
        foreach (var t in _allTrackRows) t.RefreshPlayability();
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLogin));
        OnPropertyChanged(nameof(HasAnyLogin));
        OnPropertyChanged(nameof(ShowPlaylistContent));
    }

    partial void OnIsQqLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAnyLogin));
        OnPropertyChanged(nameof(ShowPlaylistContent));
    }

    public bool CanShareCurrentPlaylist => SelectedPlaylist?.Id > 0;

    partial void OnSelectedPlaylistChanged(PlaylistItemViewModel? value)
    {
        OnPropertyChanged(nameof(ShowPlaylistContent));
        OnPropertyChanged(nameof(CanShareCurrentPlaylist));
    }

    /// <summary>进入页面时调用:恢复本地登录态(网易云拉资料,不阻塞 UI,失败静默),QQ 侧由 EnsureQqLoadedAsync 处理。</summary>
    public async Task EnsureLoadedAsync()
    {
        await EnsureQqLoadedAsync();
        // QQ 单独登录且未选中歌单时,自动打开"我喜欢"——"我的收藏"页直出喜欢列表
        // (与网易云登录后自动打开"我喜欢的音乐"行为对齐;双登录时网易云分支优先)
        if (IsQqLoggedIn && !IsLoggedIn && SelectedPlaylist is null)
        {
            var liked = QqPlaylists.FirstOrDefault(p => p.Playlist.DirId == QQMusicApiClient.LikedDirId)
                        ?? QqPlaylists.FirstOrDefault(p => p.Playlist.Name == "我喜欢")
                        ?? QqPlaylists.FirstOrDefault();
            if (liked is not null)
                await OpenQqPlaylistAsync(liked);
        }
        if ((!IsLoggedIn || _netEaseRestoredFromCache) && _cookie.MusicU is not null)
            await LoadProfileAndPlaylistsAsync();
    }

    /// <summary>恢复 QQ 登录态时先让服务端验证凭证；过期则由客户端最多刷新一次并持久化，
    /// 验证通过后才呈现已登录并拉取用户歌单。</summary>
    public async Task EnsureQqLoadedAsync()
    {
        if ((!IsQqLoggedIn || _qqRestoredFromCache) && _cookie.QQCookieRaw is { Length: > 0 })
        {
            try
            {
                await _qqApi.EnsureCredentialValidAsync();
                IsQqLoggedIn = true;
                _qqRestoredFromCache = false;
            }
            catch (ApiException ex) when (QQMusicApiClient.RequiresFreshLogin(ex.Code))
            {
                _qqApi.ClearCookie();
                IsQqLoggedIn = false;
                return;
            }
            catch (ApiException)
            {
                // 网络/服务端瞬时异常保留本地 Cookie 与离线快照，下次进入再验证。
                return;
            }
            catch (Exception ex) when (IsConnectivityFailure(ex))
            {
                // HttpClient 在完全断网时不会包装成 ApiException；保留已恢复的离线账号。
                return;
            }
        }
        if (!IsQqLoggedIn || _qqPlaylistsLoaded) return;
        await LoadQqPlaylistsAsync();
    }

    /// <summary>拉取 QQ 用户歌单填入 QqPlaylists(一次全量),昵称一并缓存。失败静默且不标记已加载。</summary>
    private async Task LoadQqPlaylistsAsync()
    {
        try
        {
            var profile = await _qqApi.GetUserProfileAsync();
            QqUserName = profile.Nickname;
            var playlists = await _qqApi.GetUserPlaylistsAsync();
            QqPlaylists.Clear();
            foreach (var p in playlists)
                QqPlaylists.Add(new PlaylistItemViewModel(p));
            _qqPlaylistsLoaded = true;
            _qqRestoredFromCache = false;
            await _musicCache.CachePlaylistListAsync(MusicSource.QQ, QqUserName, playlists);
        }
        catch (Exception ex) when (ex is ApiException || IsConnectivityFailure(ex))
        {
            // Cookie 失效/网络失败:分组保持空,下次进入或重启重试
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsQQLoginTab)
        {
            await LoginQQAsync();
            return;
        }
        var raw = MusicUInput.Trim();
        if (raw.Length == 0) { Message = "请粘贴 MUSIC_U cookie"; return; }

        IsBusy = true;
        Message = null;
        try
        {
            _api.SetMusicUCookie(raw); // 解析并持久化(容错:容忍贴整段 cookie)
            await LoadProfileAndPlaylistsAsync();
            MusicUInput = "";
        }
        catch (ApiException ex)
        {
            Message = $"登录失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>QQ音乐 Cookie 登录:粘贴 y.qq.com 的整段完整 Cookie(含 uin/qqmusic_key/p_skey 等)。
    /// 本地解析通过后立即调一次服务端资料接口验证 —— 现网对不完整/失效的 Cookie 会"静默保存成登录态",
    /// 不验证会出现 UI 已登录而账号接口全 401 型假象;验证失败回滚本地与持久化 Cookie。
    /// AppShell 监听 IsQqLoggedIn 自动关弹窗。</summary>
    private async Task LoginQQAsync()
    {
        var raw = QqCookieInput.Trim();
        if (raw.Length == 0) { Message = "请粘贴 y.qq.com 的整段 Cookie"; return; }

        IsBusy = true;
        Message = null;
        try
        {
            _qqApi.SetCookie(raw);
            await _qqApi.EnsureCredentialValidAsync();
            var profile = await _qqApi.GetUserProfileAsync();
            IsQqLoggedIn = true;
            QqUserName = profile.Nickname;
            NotifyAccountChanged(); // 换号后播放条红心按新账号重判
            _ = LoadQqPlaylistsAsync(); // 侧边栏"QQ音乐"分组随后出现(MainViewModel 监听集合变化)
            QqCookieInput = "";
        }
        catch (ApiException ex)
        {
            _qqApi.ClearCookie(); // 服务端不认:不留半残登录态(内存 + 存档一并清)
            IsQqLoggedIn = false;
            Message = $"登录失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendQqPhoneCodeAsync()
    {
        if (!CanSendQqPhoneCode) return;
        var countryCode = NormalizeCountryCode(QqPhoneCountryCode);
        var phone = NormalizePhoneNumber(QqPhoneNumber);
        QqPhoneCountryCode = countryCode;
        QqPhoneNumber = phone;

        var cancellation = ReplaceQqPhoneOperation();
        IsBusy = true;
        Message = null;
        QqPhoneStatus = "正在安全发送验证码…";
        try
        {
            var result = await _qqApi.SendPhoneCodeAsync(phone, countryCode, cancellation.Token);
            if (!ReferenceEquals(_qqPhoneOperationCancellation, cancellation)) return;
            switch (result.Stage)
            {
                case QQMusicPhoneCodeStage.Sent:
                    IsQqPhoneCodeSent = true;
                    QqPhoneStatus = $"验证码已发送至 +{countryCode} {MaskPhone(phone)}";
                    StartQqPhoneCountdown();
                    break;
                case QQMusicPhoneCodeStage.CaptchaRequired:
                    IsQqPhoneCodeSent = false;
                    QqPhoneStatus = "当前号码触发了安全验证，请改用扫码登录";
                    Message = "QQ 音乐要求额外安全验证，手机客户端扫码登录更稳妥。";
                    break;
                case QQMusicPhoneCodeStage.FrequencyLimited:
                    IsQqPhoneCodeSent = false;
                    QqPhoneStatus = "请求过于频繁，请稍后再试";
                    Message = "验证码发送次数受限，请稍后重试或改用扫码登录。";
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ApiException ex)
        {
            QqPhoneStatus = "验证码发送失败";
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            QqPhoneStatus = "验证码发送失败";
            Message = $"网络连接失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_qqPhoneOperationCancellation, cancellation))
            {
                _qqPhoneOperationCancellation = null;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private async Task LoginQqPhoneAsync()
    {
        var countryCode = NormalizeCountryCode(QqPhoneCountryCode);
        var phone = NormalizePhoneNumber(QqPhoneNumber);
        var code = QqPhoneCode.Trim();
        QqPhoneCountryCode = countryCode;
        QqPhoneNumber = phone;
        QqPhoneCode = code;
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            Message = "请输入短信中的 6 位验证码";
            return;
        }

        var cancellation = ReplaceQqPhoneOperation();
        IsBusy = true;
        Message = null;
        QqPhoneStatus = "正在验证并登录…";
        try
        {
            var credential = await _qqApi.LoginWithPhoneCodeAsync(phone, countryCode, code, cancellation.Token);
            if (!ReferenceEquals(_qqPhoneOperationCancellation, cancellation)) return;
            QqUserName = credential.Nickname.Length > 0 ? credential.Nickname : $"QQ {credential.MusicId}";
            IsQqLoggedIn = true;
            _qqPlaylistsLoaded = false;
            RefreshPlayability();
            QqPhoneNumber = "";
            QqPhoneCode = "";
            QqPhoneStatus = "登录成功";
            _ = LoadQqPlaylistsAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (ApiException ex)
        {
            QqPhoneStatus = "登录未完成";
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            QqPhoneStatus = "登录未完成";
            Message = $"网络连接失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_qqPhoneOperationCancellation, cancellation))
            {
                _qqPhoneOperationCancellation = null;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    private CancellationTokenSource ReplaceQqPhoneOperation()
    {
        _qqPhoneOperationCancellation?.Cancel();
        var next = new CancellationTokenSource();
        _qqPhoneOperationCancellation = next;
        return next;
    }

    private void StartQqPhoneCountdown() => _ = RunQqPhoneCountdownAsync();

    private async Task RunQqPhoneCountdownAsync()
    {
        _qqPhoneCountdownCancellation?.Cancel();
        _qqPhoneCountdownCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _qqPhoneCountdownCancellation = cancellation;
        try
        {
            for (QqPhoneCountdown = 60; QqPhoneCountdown > 0; QqPhoneCountdown--)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_qqPhoneCountdownCancellation, cancellation))
            {
                _qqPhoneCountdownCancellation = null;
                QqPhoneCountdown = 0;
            }
            cancellation.Dispose();
        }
    }

    private static string NormalizeCountryCode(string value) => value.Trim().TrimStart('+');

    private static string NormalizePhoneNumber(string value)
        => new(value.Where(char.IsDigit).ToArray());

    private static string MaskPhone(string phone)
        => phone.Length >= 7 ? $"{phone[..3]}****{phone[^4..]}" : phone;

    /// <summary>创建 QQ 音乐客户端二维码，持续监听扫码/确认事件，成功后落入现有登录态。</summary>
    [RelayCommand]
    private async Task StartQqQrLoginAsync()
    {
        CancelQqQrLogin();
        var cancellation = new CancellationTokenSource();
        _qqQrLoginCancellation = cancellation;
        IsQqQrLoginActive = true;
        IsBusy = true;
        Message = null;
        QqQrStatus = "正在准备安全登录环境…";
        try
        {
            var progress = new Progress<QQMusicQrLoginUpdate>(update =>
            {
                if (!ReferenceEquals(_qqQrLoginCancellation, cancellation)) return;
                if (update.QrPng is { Length: > 0 }) SetQqQrImage(update.QrPng);
                QqQrStatus = update.Stage switch
                {
                    QQMusicQrLoginStage.Preparing => "正在准备安全登录环境…",
                    QQMusicQrLoginStage.Waiting => "请使用手机 QQ 音乐扫码并确认",
                    QQMusicQrLoginStage.Scanned => "已扫码，请在手机上确认登录",
                    QQMusicQrLoginStage.Exchanging => "已确认，正在获取账号凭证…",
                    _ => QqQrStatus,
                };
            });
            var credential = await _qqApi.LoginWithQrAsync(progress, cancellation.Token);
            if (!ReferenceEquals(_qqQrLoginCancellation, cancellation)) return;
            QqUserName = credential.Nickname.Length > 0 ? credential.Nickname : $"QQ {credential.MusicId}";
            IsQqLoggedIn = true;
            _qqPlaylistsLoaded = false;
            RefreshPlayability();
            _ = LoadQqPlaylistsAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 用户关闭弹窗或切换页签，属于正常取消。
        }
        catch (ApiException ex)
        {
            if (ReferenceEquals(_qqQrLoginCancellation, cancellation))
            {
                Message = $"扫码登录失败:{ex.Message}";
                QqQrStatus = "二维码不可用，请点击重新获取";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_qqQrLoginCancellation, cancellation))
            {
                Message = $"扫码登录失败:{ex.Message}";
                QqQrStatus = "连接失败，请点击重新获取";
            }
        }
        finally
        {
            if (ReferenceEquals(_qqQrLoginCancellation, cancellation))
            {
                _qqQrLoginCancellation = null;
                IsQqQrLoginActive = false;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    private void SetQqQrImage(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        var next = new Bitmap(stream);
        var previous = QqQrImage as IDisposable;
        QqQrImage = next;
        previous?.Dispose();
    }

    /// <summary>关闭登录弹层/离开 QQ 页签时释放 MQTT 连接；再次打开会生成新二维码。</summary>
    public void CancelQqQrLogin()
    {
        var cancellation = _qqQrLoginCancellation;
        _qqQrLoginCancellation = null;
        cancellation?.Cancel();
        IsQqQrLoginActive = false;
        IsBusy = false;
        (QqQrImage as IDisposable)?.Dispose();
        QqQrImage = null;
        QqQrStatus = "使用手机 QQ 音乐扫描二维码";
    }

    public void CancelLoginActivities()
    {
        CancelQqQrLogin();
        CancelNetEaseProxyLogin();
        var operation = _qqPhoneOperationCancellation;
        _qqPhoneOperationCancellation = null;
        operation?.Cancel();
        _qqPhoneCountdownCancellation?.Cancel();
        QqPhoneNumber = "";
        QqPhoneCode = "";
        IsQqPhoneCodeSent = false;
        QqPhoneStatus = "验证码将发送到你的手机";
        IsBusy = false;
    }

    [RelayCommand]
    public void LogoutNetEase()
    {
        _api.ClearCookie();
        IsLoggedIn = false;
        UserName = "";
        AvatarUrl = "";
        Playlists.Clear();
        if (SelectedPlaylist?.Playlist.Source == MusicSource.NetEase)
            ClearCurrentPlaylistContent();
        NotifyAccountChanged();
    }

    /// <summary>登录/登出后让播放器重新判定当前曲红心。
    /// 红心状态是按账号缓存的,而播放条不属于任何页面、不随页面重载刷新 —— 不通知就会显示旧账号的红心。</summary>
    private static void NotifyAccountChanged()
    {
        try { ServiceLocator.Get<PlayerViewModel>().RefreshCurrentLiked(); }
        catch { /* 宿主未初始化(探针/自检环境):忽略 */ }
    }

    [RelayCommand]
    public void LogoutQq()
    {
        _qqApi.ClearCookie();
        IsQqLoggedIn = false;
        QqUserName = "";
        QqPlaylists.Clear();
        _qqPlaylistsLoaded = false;
        if (SelectedPlaylist?.Playlist.Source == MusicSource.QQ)
            ClearCurrentPlaylistContent();
        NotifyAccountChanged();
    }

    /// <summary>兼容旧入口：同时退出两个平台。</summary>
    [RelayCommand]
    public void Logout()
    {
        CancelLoginActivities();
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        IsLoadingMore = false;
        LogoutNetEase();
        LogoutQq();
        PlaylistTitle = "";
        CreatorName = "";
        ClearCurrentPlaylistContent();
    }

    private void ClearCurrentPlaylistContent()
    {
        PlaylistTitle = "";
        CreatorName = "";
        SelectedPlaylist = null;
        ClearTrackRows();
        Filters.Reset();
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _aggregateLoad = null;
        _materialized = 0;
        _isCloud = false;
        _currentAggregate = null;
        IsAggregate = false;
    }

    // 增量加载状态:trackIds 是全量权威顺序;仅已解析的歌曲会物化进 Tracks。
    private List<long> _trackIds = new();
    private readonly Dictionary<long, Song> _known = new();
    // 当前页已物化歌曲窗口：供“接下来播放”直接展示；完整的随机/顺序范围由 _playbackQueue
    // 保存轻量 trackId/成员分页描述，命中未显示下标时再解析 Song。
    private readonly List<Song> _queueSongs = new();
    private ILazySongQueue? _playbackQueue;
    private AggregateSongQueue? _aggregatePlaybackQueue;
    private int _materialized;          // 已物化进 Tracks 的曲目数(按 trackIds 顺序)
    [ObservableProperty] private bool _isLoadingMore;

    /// <summary>补页指示条文案:正常"正在加载更多歌曲…",命中网易云账号限速(405)时改为退避提示。</summary>
    [ObservableProperty] private string _loadingMoreText = "正在加载更多歌曲…";
    private int _loadGeneration;        // 打开新歌单时自增,使旧歌单的加载失效
    private CancellationTokenSource? _loadCancellation;
    private bool _isCloud;              // 当前展示的是音乐云盘(而非用户歌单)

    private sealed class AggregateLoadState
    {
        public required IReadOnlyList<AggregatePlaylistMember> Members { get; init; }
        public int MemberIndex { get; set; }
        public IReadOnlyList<long>? NetEaseTrackIds { get; set; }
        public Dictionary<long, Song> NetEaseKnown { get; } = new();
        public int NetEaseCursor { get; set; }
        public int QqBegin { get; set; }
        public int FailedCount { get; set; }
        public bool CoverSet { get; set; }
        public bool HasMore => MemberIndex < Members.Count;

        public void AdvanceMember()
        {
            MemberIndex++;
            NetEaseTrackIds = null;
            NetEaseKnown.Clear();
            NetEaseCursor = 0;
            QqBegin = 0;
        }
    }

    private sealed record AggregateSongBatch(MusicSource Source, IReadOnlyList<Song> Songs);
    private AggregateLoadState? _aggregateLoad;

    /// <summary>当前展示的聚合歌单(null = 非聚合页)。齿轮设置按钮按它显隐/定位。</summary>
    private Models.AggregatePlaylist? _currentAggregate;

    /// <summary>当前页是否为聚合歌单(驱动右上角齿轮设置按钮显隐)。</summary>
    [ObservableProperty] private bool _isAggregate;

    /// <summary>当前展示的聚合歌单(供设置弹窗引用;null = 非聚合页)。</summary>
    public Models.AggregatePlaylist? CurrentAggregate => _currentAggregate;

    /// <summary>聚合歌单页右上角齿轮:打开排列顺序设置弹窗(经宿主 MainViewModel 弹窗状态)。</summary>
    [RelayCommand]
    private void OpenAggregateSettings()
    {
        if (_currentAggregate is null) return;
        try { ServiceLocator.Get<MainViewModel>().OpenAggregateSettingsCommand.Execute(_currentAggregate); }
        catch { /* SelfTest/Headless 等无宿主环境 */ }
    }

    /// <summary>开始新的详情页加载并取消旧页面仍在进行的 HTTP 请求。</summary>
    private (int Generation, CancellationToken Token) BeginLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        // 旧代次的 finally 不会再改新页面状态，必须在这里解除它持有的单飞标记。
        IsLoadingMore = false;
        _pageScrollOffset = 0;
        return (++_loadGeneration, _loadCancellation.Token);
    }

    /// <summary>页面离开时停止当前网络与增量任务；导航恢复对应入口时会建立新的加载代次。</summary>
    public void CancelCurrentLoad()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _isCloud = false;
        IsBusy = false;
        IsLoadingMore = false;
    }

    /// <summary>由 PlaylistView 记录当前位置；只保存一个 double，不持有视图或容器。</summary>
    internal void UpdatePageScrollOffset(double offset)
        => _pageScrollOffset = double.IsFinite(offset) ? Math.Max(0, offset) : 0;

    internal bool TryGetPendingScrollRestore(
        int appliedVersion, out int version, out double offset)
    {
        version = ScrollRestoreVersion;
        offset = _pendingScrollRestoreOffset;
        return version > 0 && version != appliedVersion;
    }

    /// <summary>导航前冻结重数据到磁盘并立即断开页面引用；返回历史仅保留本方法返回的小对象。</summary>
    internal PlaylistNavigationSnapshot? CaptureAndReleaseNavigationSnapshot()
    {
        var watch = RestoreTimingTrace is null ? null : System.Diagnostics.Stopwatch.StartNew();
        void Mark(string stage) =>
            RestoreTimingTrace?.Invoke($"[歌单计时] {stage}: {watch!.ElapsedMilliseconds}ms");

        if (SelectedPlaylist is null)
        {
            ReleaseCurrentPageData();
            return null;
        }

        var kind = _isCloud
            ? PlaylistPageKind.Cloud
            : IsAggregate
                ? PlaylistPageKind.Aggregate
                : SelectedPlaylist.Playlist.Source == MusicSource.QQ
                    ? PlaylistPageKind.Qq
                    : PlaylistPageKind.NetEase;
        var wasBusy = IsBusy;
        CancelCurrentLoad();
        var cacheKey = Guid.NewGuid().ToString("N");
        var snapshot = new PlaylistNavigationSnapshot(
            cacheKey,
            kind,
            CopyCurrentHeader(SelectedPlaylist),
            _currentAggregate,
            PlaylistTitle,
            CreatorName,
            Filters.SelectedSortIndex,
            Filters.SearchText,
            Filters.IsExpanded,
            _pageScrollOffset);

        var queued = new HashSet<Song>(_queueSongs);
        var tracks = _allTrackRows.Select(row => new NavigationPageCacheTrack(
            row.Song,
            row.IsPlayable,
            queued.Contains(row.Song),
            row.Song.IsPlaybackUnavailable,
            row.Song.PreferCachedPlayback)).ToList();
        Mark($"构建导航快照(rows={tracks.Count})");
        var aggregateState = _aggregateLoad is null
            ? null
            : new AggregatePageCacheState(
                _aggregateLoad.MemberIndex,
                _aggregateLoad.NetEaseTrackIds?.ToArray(),
                _aggregateLoad.NetEaseKnown.Values.ToList(),
                _aggregateLoad.NetEaseCursor,
                _aggregateLoad.QqBegin,
                _aggregateLoad.FailedCount,
                _aggregateLoad.CoverSet);
        // 尚未拿到首批内容时不保存“空的加载中页面”，返回时让缺失快照自然触发网络回退。
        // 云盘没有独立续页游标，只有完整加载结束后才可安全快照。
        if ((tracks.Count > 0 || !wasBusy) && (kind != PlaylistPageKind.Cloud || !wasBusy))
            _ = _musicCache.CachePlaylistPageSnapshotAsync(
                cacheKey,
                new PlaylistPageCacheData(
                    tracks, _trackIds.ToArray(), _materialized, aggregateState));

        Mark("驻留内存快照");
        ReleaseCurrentPageData();
        Mark("释放页面数据");
        return snapshot;
    }

    /// <summary>后退离开且不需要前进历史时直接释放，不驻留无消费者的快照。</summary>
    internal void ReleaseCurrentPageData()
    {
        CancelCurrentLoad();
        ClearTrackRows();
        SelectedTrack = null;
        _trackIds = [];
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _aggregateLoad = null;
        _materialized = 0;
        _loadAllForFilterTask = null;
        _currentAggregate = null;
        _isCloud = false;
        IsAggregate = false;
        SelectedPlaylist = null;
        PlaylistTitle = "";
        CreatorName = "";
        Message = null;
        Filters.Reset();
        _pageScrollOffset = 0;
    }

    /// <summary>先从一次性内存快照恢复精确页面状态；快照失效时回退原有缓存/网络打开流程。</summary>
    internal async Task RestoreNavigationSnapshotAsync(PlaylistNavigationSnapshot snapshot)
    {
        var watch = RestoreTimingTrace is null ? null : System.Diagnostics.Stopwatch.StartNew();
        void Mark(string stage) =>
            RestoreTimingTrace?.Invoke($"[歌单计时] {stage}: {watch!.ElapsedMilliseconds}ms");

        var (generation, ct) = BeginLoad();
        _isCloud = snapshot.Kind == PlaylistPageKind.Cloud;
        _currentAggregate = snapshot.Aggregate;
        IsAggregate = snapshot.Kind == PlaylistPageKind.Aggregate;
        _aggregateLoad = null;
        SelectedPlaylist = new PlaylistItemViewModel(snapshot.Header);
        ClearTrackRows();
        SelectedTrack = null;
        Filters.Reset();
        PlaylistTitle = snapshot.PlaylistTitle;
        CreatorName = snapshot.CreatorName;
        _trackIds = [];
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = true;
        Message = null;
        Mark("清场(同步段)");

        var cached = await _musicCache.TryTakePlaylistPageSnapshotAsync(snapshot.CacheKey);
        Mark("取回内存快照");
        if (!IsCurrentLoad(generation, ct)) return;
        if (cached is null)
        {
            Mark("快照缺失,回退常规打开流程");
            IsBusy = false;
            _isCloud = false;
            switch (snapshot.Kind)
            {
                case PlaylistPageKind.Cloud:
                    await OpenCloudAsync();
                    break;
                case PlaylistPageKind.Aggregate:
                    await OpenAggregateAsync(snapshot.Aggregate);
                    break;
                case PlaylistPageKind.Qq:
                    await OpenQqPlaylistAsync(new PlaylistItemViewModel(snapshot.Header));
                    break;
                default:
                    await OpenPlaylistAsync(new PlaylistItemViewModel(snapshot.Header));
                    break;
            }
            RestoreNavigationUiState(snapshot);
            return;
        }

        var pageSongs = cached.Tracks.Select(track => track.Song).ToList();
        _trackIds = cached.TrackIds.ToList();
        _materialized = Math.Max(0, cached.Materialized);
        _queueSongs.AddRange(cached.Tracks.Where(track => track.IsQueued).Select(track => track.Song));

        if (snapshot.Kind == PlaylistPageKind.NetEase && _trackIds.Count > 0)
            _playbackQueue = new IndexedSongQueue(_trackIds, pageSongs, _api.GetSongsByIdsAsync);
        else if (snapshot.Kind == PlaylistPageKind.Aggregate && snapshot.Aggregate is { } aggregate)
        {
            var members = OrderAggregateMembers(aggregate);
            _aggregatePlaybackQueue = CreateAggregatePlaybackQueue(members);
            _playbackQueue = _aggregatePlaybackQueue;
            var saved = cached.AggregateLoad;
            _aggregateLoad = new AggregateLoadState
            {
                Members = members,
                MemberIndex = Math.Clamp(saved?.MemberIndex ?? members.Count, 0, members.Count),
                NetEaseTrackIds = saved?.NetEaseTrackIds,
                NetEaseCursor = saved?.NetEaseCursor ?? 0,
                QqBegin = saved?.QqBegin ?? 0,
                FailedCount = saved?.FailedCount ?? 0,
                CoverSet = saved?.CoverSet ?? true,
            };
            if (saved is not null)
                foreach (var song in saved.NetEaseKnown)
                    if (song.Id != 0) _aggregateLoad.NetEaseKnown[song.Id] = song;
        }

        for (var index = 0; index < cached.Tracks.Count; index++)
        {
            var saved = cached.Tracks[index];
            saved.Song.IsPlaybackUnavailable = saved.IsPlaybackUnavailable;
            saved.Song.PreferCachedPlayback = saved.PreferCachedPlayback;
            var api = saved.Song.Source == MusicSource.NetEase ? _api : null;
            var row = CreateTrackRow(saved.Song, index, api);
            row.IsPlayable = saved.IsPlayable;
            _allTrackRows.Add(row);
        }
        Mark($"重建歌曲行(rows={_allTrackRows.Count})");
        IsBusy = false;
        RestoreNavigationUiState(snapshot);
        Mark($"恢复筛选与列表投影(Tracks={Tracks.Count})");
    }

    private void RestoreNavigationUiState(PlaylistNavigationSnapshot snapshot)
    {
        Filters.RestoreState(
            snapshot.SelectedSortIndex, snapshot.SearchText, snapshot.IsFilterExpanded);
        _pendingScrollRestoreOffset = Math.Max(0, snapshot.ScrollOffset);
        ScrollRestoreVersion++;
    }

    private static Playlist CopyCurrentHeader(PlaylistItemViewModel item) => new()
    {
        Id = item.Playlist.Id,
        DirId = item.Playlist.DirId,
        Source = item.Playlist.Source,
        Name = item.Name,
        Description = item.Description,
        TrackCount = item.TrackCount,
        CoverUrl = item.CoverUrl,
        CanAddTracks = item.Playlist.CanAddTracks,
    };

    internal int RetainedTrackRowCount => _allTrackRows.Count;
    internal int RetainedQueueSongCount => _queueSongs.Count;
    internal int RetainedTrackIdCount => _trackIds.Count;
    internal bool HasRetainedPageData => SelectedPlaylist is not null
                                         || _allTrackRows.Count > 0
                                         || _trackIds.Count > 0;

    internal void DiscardNavigationSnapshot(PlaylistNavigationSnapshot? snapshot)
    {
        if (snapshot is not null)
            _ = _musicCache.DiscardPlaylistPageSnapshotAsync(snapshot.CacheKey);
    }

    private bool IsCurrentLoad(int generation, CancellationToken token)
        => generation == _loadGeneration && !token.IsCancellationRequested;

    /// <summary>音乐云盘:复用歌单页展示。分页拉全量云盘曲目(500/页,跟随 hasMore),
    /// 行队列共享 → 播放全部/上一曲/下一曲都在云盘列表内。已在云盘页时跳过(保留现有内容)。</summary>
    [RelayCommand]
    private async Task OpenCloudAsync()
    {
        if (!IsLoggedIn || _isCloud) return;
        var (generation, ct) = BeginLoad();
        _isCloud = true;
        _currentAggregate = null;
        _aggregateLoad = null;
        IsAggregate = false;
        SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = "音乐云盘" });
        ClearTrackRows();
        Filters.Reset();
        PlaylistTitle = "音乐云盘";
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = true;
        Message = null;
        try
        {
            var offset = 0;
            var hasMore = true;
            while (hasMore)
            {
                var (songs, totalCount, more) = await _api.GetCloudListAsync(500, offset, ct);
                if (!IsCurrentLoad(generation, ct)) return;
                if (offset == 0)
                {
                    // 首页拿到总数后重建头部(TrackCount/CoverUrl init-only);封面用第一首有封面的歌(仿歌单页)
                    var cover = songs.FirstOrDefault(s => !string.IsNullOrEmpty(s.CoverUrl))?.CoverUrl ?? "";
                    SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = "音乐云盘", TrackCount = totalCount, CoverUrl = cover });
                }
                foreach (var s in songs)
                {
                    _allTrackRows.Add(new SongItemViewModel(
                        s, _player.PlayFromList, _allTrackRows.Count + 1, _queueSongs, _api, "音乐云盘"));
                    _queueSongs.Add(s);
                }
                RefreshVisibleTracks();
                offset += songs.Count;
                hasMore = more && songs.Count > 0 && offset < 3000; // 3000 首兜底,防接口异常时死循环
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException ex)
        {
            if (generation == _loadGeneration) Message = $"加载云盘失败:{ex.Message}";
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) IsBusy = false;
        }
    }

    /// <summary>点开歌单：只取 v6 一次 → 立即把前段曲目上屏；后续按滚动增量补齐。
    /// 首屏不清零、不阻塞；大歌单(如 1000+ 首“我喜欢的音乐”)不会因全量请求而假死。</summary>
    [RelayCommand]
    private async Task OpenPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        var (generation, ct) = BeginLoad();
        _isCloud = false;
        _currentAggregate = null;
        _aggregateLoad = null;
        IsAggregate = false;
        SelectedPlaylist = playlist;
        ClearTrackRows();
        Filters.Reset();
        PlaylistTitle = playlist.Name;
        CreatorName = UserName;
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = true;
        var hasCachedTracks = false;
        try
        {
            hasCachedTracks = await RestoreCachedTracksAsync(playlist, generation, ct);
            if (!IsCurrentLoad(generation, ct))
            {
                return;
            }
            Message = hasCachedTracks ? "已显示缓存内容，正在刷新…" : null;
            var overview = await _api.GetPlaylistTrackOverviewAsync(playlist.Id, ct);
            if (!IsCurrentLoad(generation, ct))
            {
                return;
            }
            playlist.RefreshCover(overview.CoverUrl); // 封面随曲目变化(如"我喜欢的音乐"),URL 变了才重载
            // 创建者显示真实昵称(v6 响应自带);自己的歌单该值与 UserName 一致,离线缓存失败时保留 UserName 兜底
            // 创建者显示真实昵称(v6 响应自带);自己的歌单该值与 UserName 一致,为空时保留占位兜底
            if (overview.CreatorNickname.Length > 0)
                CreatorName = overview.CreatorNickname;
            CreatorId = overview.CreatorId;
            _trackIds = overview.TrackIds.ToList();
            foreach (var s in overview.PrefixTracks)
                if (s.Id != 0) _known[s.Id] = s;
            _playbackQueue = new IndexedSongQueue(
                _trackIds, overview.PrefixTracks, _api.GetSongsByIdsAsync);

            // 目标表 = trackIds 顺序里一直取到第一个未知 id 为止(与 AppendKnownTracks 同口径)
            var prefix = new List<Song>();
            for (var i = 0; i < _trackIds.Count; i++)
            {
                if (!_known.TryGetValue(_trackIds[i], out var song)) break;
                prefix.Add(song);
            }
            _queueSongs.Clear();
            foreach (var song in prefix) _queueSongs.Add(song);

            // 网络结果落地:缓存行能原地升级的就别重建 —— 重建会让已上屏的封面重新异步加载,
            // 用户看到"图片又加载了一遍"(见 SongItemViewModel.RebindPlayback)。
            ApplyFreshRows(prefix, (song, index) => CreateTrackRow(song, index, _api));
            IsBusy = false;

            // 后台静默补充到 ~200 首,让首屏滚动不断档(至多一次 song/detail 请求,不阻塞 UI)
            await LoadMoreAsync(fillTo: 200, generation: generation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ApiException || IsConnectivityFailure(ex))
        {
            if (generation == _loadGeneration)
                Message = hasCachedTracks
                    ? "当前网络不可用，正在使用离线缓存"
                    : $"加载歌单失败:{ex.Message}";
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) IsBusy = false;
        }
    }

    /// <summary>点开 QQ 歌单:一次拉全量曲目(QQ 无 trackIds 增量协议,fcg 按歌单 id 整页返回)。
    /// 复用网易云歌单页 UI:hero/曲目列表/共享队列;_trackIds 留空即无增量补充,播放全部直接用已物化列表。</summary>
    [RelayCommand]
    private async Task OpenQqPlaylistAsync(PlaylistItemViewModel? playlist)
    {
        if (playlist is null) return;
        var (generation, ct) = BeginLoad();
        _isCloud = false;
        _currentAggregate = null;
        _aggregateLoad = null;
        IsAggregate = false;
        SelectedPlaylist = playlist;
        ClearTrackRows();
        Filters.Reset();
        PlaylistTitle = playlist.Name;
        CreatorName = QqUserName;
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = true;
        var hasCachedTracks = false;
        try
        {
            hasCachedTracks = await RestoreCachedTracksAsync(playlist, generation, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            Message = hasCachedTracks ? "已显示缓存内容，正在刷新…" : null;
            var songs = await _qqApi.GetPlaylistTracksAsync(playlist.Id, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            _queueSongs.Clear();
            foreach (var song in songs) _queueSongs.Add(song);
            // 与网易云同理:缓存行能原地升级就别重建(重建会让已上屏的封面重新异步加载)
            ApplyFreshRows(songs, (song, index) =>
            {
                var row = new SongItemViewModel(
                    song, _player.PlayFromList, index + 1, _queueSongs, null, playlist.Name);
                AttachRemoveCommandIfNeeded(row);
                return row;
            });
            await _musicCache.CachePlaylistTracksAsync(playlist.Playlist, songs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ApiException || IsConnectivityFailure(ex))
        {
            if (generation == _loadGeneration)
                Message = hasCachedTracks
                    ? "当前网络不可用，正在使用离线缓存"
                    : $"加载歌单失败:{ex.Message}";
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) IsBusy = false;
        }
    }

    /// <summary>点开聚合歌单:按 SourceOrder 稳定排序成员并建立流式游标。
    /// 首批立即上屏；网易云按 100 个 trackId 补详情，QQ 按服务端 300 首分页；
    /// 当前成员耗尽后才推进下一成员，从而在动态加载时仍严格保持聚合顺序。</summary>
    [RelayCommand]
    private async Task OpenAggregateAsync(Models.AggregatePlaylist? aggregate)
    {
        if (aggregate is null) return;
        var (generation, ct) = BeginLoad();
        _isCloud = false;
        _currentAggregate = aggregate;
        IsAggregate = true;
        var members = OrderAggregateMembers(aggregate);
        _aggregateLoad = new AggregateLoadState { Members = members };
        SelectedPlaylist = new PlaylistItemViewModel(new Playlist
        {
            Name = aggregate.Name,
            Description = string.Join(" · ", members.Select(m => m.PlaylistName)),
        });
        ClearTrackRows();
        Filters.Reset();
        PlaylistTitle = aggregate.Name;
        CreatorName = $"聚合歌单 · {aggregate.Members.Count} 个歌单";
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _aggregatePlaybackQueue = CreateAggregatePlaybackQueue(members);
        _playbackQueue = _aggregatePlaybackQueue;
        _materialized = 0;
        IsBusy = true;
        Message = null;

        try
        {
            // 第一批优先完成并解除页面忙碌态；随后静默补到约 200 首，避免首屏刚出现就断档。
            await LoadMoreAggregateAsync(generation, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            IsBusy = false;
            while (_allTrackRows.Count < 200 && _aggregateLoad is { HasMore: true })
            {
                var before = _allTrackRows.Count;
                await LoadMoreAggregateAsync(generation, ct);
                if (!IsCurrentLoad(generation, ct) || _allTrackRows.Count == before) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (IsCurrentLoad(generation, ct)) IsBusy = false;
        }
    }

    private async Task LoadMoreAggregateAsync(int generation, CancellationToken ct)
    {
        if (IsLoadingMore || _aggregateLoad is not { HasMore: true } state) return;
        IsLoadingMore = true;
        try
        {
            var batch = await ReadNextAggregateBatchAsync(state, ct);
            if (!IsCurrentLoad(generation, ct)) return;
            if (batch is null)
            {
                if (state.FailedCount > 0)
                    Message = $"{state.FailedCount} 个歌单拉取失败,已展示其余成员";
                return;
            }
            await AppendAggregateBatchAsync(batch, state, generation, ct);

            if (!state.HasMore && state.FailedCount > 0)
                Message = $"{state.FailedCount} 个歌单拉取失败,已展示其余成员";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == _loadGeneration) IsLoadingMore = false;
        }
    }

    /// <summary>读取当前成员的下一网络批次；空歌单/失败成员会在同一次请求中跳过，直到拿到数据或全部结束。</summary>
    private async Task<AggregateSongBatch?> ReadNextAggregateBatchAsync(AggregateLoadState state, CancellationToken ct)
    {
        while (state.HasMore)
        {
            ct.ThrowIfCancellationRequested();
            var member = state.Members[state.MemberIndex];
            try
            {
                var memberIndex = state.MemberIndex;
                if (member.Source == MusicSource.QQ)
                {
                    var begin = state.QqBegin;
                    var page = await _qqApi.GetPlaylistTrackPageAsync(
                        member.PlaylistId, begin, AggregateQqPageSize, ct);
                    _aggregatePlaybackQueue?.ConfigureQqMember(memberIndex, page.TotalCount);
                    state.QqBegin += AggregateQqPageSize;
                    if (!page.HasMore) state.AdvanceMember();
                    if (page.Songs.Count > 0)
                        return new AggregateSongBatch(MusicSource.QQ, page.Songs);
                    continue;
                }

                if (state.NetEaseTrackIds is null)
                {
                    var overview = await _api.GetPlaylistTrackOverviewAsync(member.PlaylistId, ct);
                    state.NetEaseTrackIds = overview.TrackIds;
                    _aggregatePlaybackQueue?.ConfigureNetEaseMember(memberIndex, overview);
                    foreach (var song in overview.PrefixTracks)
                        if (song.Id != 0) state.NetEaseKnown[song.Id] = song;
                }

                if (state.NetEaseCursor >= state.NetEaseTrackIds.Count)
                {
                    state.AdvanceMember();
                    continue;
                }

                var memberOffset = state.NetEaseCursor;
                var ids = state.NetEaseTrackIds
                    .Skip(memberOffset)
                    .Take(AggregateNetEaseBatchSize)
                    .ToList();
                var missing = ids.Where(id => !state.NetEaseKnown.ContainsKey(id)).ToList();
                if (missing.Count > 0)
                {
                    var details = await _api.GetSongsByIdsAsync(missing, ct);
                    foreach (var song in details)
                        if (song.Id != 0) state.NetEaseKnown[song.Id] = song;
                }

                var songs = ids
                    .Where(state.NetEaseKnown.ContainsKey)
                    .Select(id => state.NetEaseKnown[id])
                    .ToList();
                // 已物化歌曲由 Tracks/_queueSongs 持有；游标字典只保留后续批次，避免再重复保活整份成员歌单。
                foreach (var id in ids)
                    state.NetEaseKnown.Remove(id);
                state.NetEaseCursor += ids.Count;
                if (state.NetEaseCursor >= state.NetEaseTrackIds.Count)
                    state.AdvanceMember();
                if (songs.Count > 0)
                    return new AggregateSongBatch(MusicSource.NetEase, songs);
            }
            catch (ApiException ex)
            {
                _aggregatePlaybackQueue?.MarkMemberUnavailable(state.MemberIndex);
                state.FailedCount++;
                Message = $"歌单[{member.PlaylistName}]拉取失败:{ex.Message}";
                state.AdvanceMember();
            }
        }

        return null;
    }

    /// <summary>网络批次按最多 50 行一次的集合通知应用到 UI，给输入/渲染队列留下调度机会。</summary>
    private async Task AppendAggregateBatchAsync(
        AggregateSongBatch batch, AggregateLoadState state, int generation, CancellationToken ct)
    {
        for (var offset = 0; offset < batch.Songs.Count; offset += AggregateUiBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var songs = batch.Songs.Skip(offset).Take(AggregateUiBatchSize).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentLoad(generation, ct)) return;

                var rowStart = _allTrackRows.Count;
                _queueSongs.AddRange(songs);
                var rows = songs.Select((song, index) =>
                    CreateTrackRow(song, rowStart + index,
                        batch.Source == MusicSource.QQ ? null : _api)).ToList();
                _allTrackRows.AddRange(rows);
                RefreshVisibleTracks();
                SelectedPlaylist?.UpdateTrackCount(_allTrackRows.Count);

                if (!state.CoverSet)
                {
                    var cover = songs.FirstOrDefault(song => !string.IsNullOrEmpty(song.CoverUrl))?.CoverUrl ?? "";
                    if (cover.Length > 0)
                    {
                        state.CoverSet = true;
                        SelectedPlaylist?.RefreshCover(cover);
                    }
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>按来源分组稳定排序，同源成员保持用户配置的原始顺序。</summary>
    internal static List<AggregatePlaylistMember> OrderAggregateMembers(AggregatePlaylist aggregate)
        => aggregate.SourceOrder == AggregateSourceOrder.QqFirst
            ? aggregate.Members.OrderBy(member => member.Source == MusicSource.QQ ? 0 : 1).ToList()
            : aggregate.Members.OrderBy(member => member.Source == MusicSource.NetEase ? 0 : 1).ToList();

    private AggregateSongQueue CreateAggregatePlaybackQueue(IReadOnlyList<AggregatePlaylistMember> members)
    {
        var descriptors = members.Select(member =>
        {
            var item = member.Source == MusicSource.QQ
                ? QqPlaylists.FirstOrDefault(candidate => candidate.Id == member.PlaylistId)
                : Playlists.FirstOrDefault(candidate => candidate.Id == member.PlaylistId);
            // 0 既可能是真空歌单，也可能是列表接口未给计数；交给播放源做一次轻量元数据确认。
            int? knownCount = item?.Playlist.TrackCount is > 0 ? item.Playlist.TrackCount : null;
            return new AggregateSongQueue.Member(member, knownCount);
        }).ToList();
        return new AggregateSongQueue(_api, _qqApi, descriptors);
    }

    /// <summary>滚动接近底部时调用：补齐下一批(≤100)曲目元数据并物化。</summary>
    public Task LoadMoreAsync()
    {
        var cancellation = _loadCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested) return Task.CompletedTask;
        return IsAggregate
            ? LoadMoreAggregateAsync(_loadGeneration, cancellation.Token)
            : LoadMoreAsync(fillTo: null, generation: _loadGeneration, cancellation.Token);
    }

    private void OnTrackFiltersChanged()
    {
        RefreshVisibleTracks();
        if (Filters.IsActive)
            _ = EnsureAllTracksLoadedForFilterAsync();
    }

    private void ClearTrackRows()
    {
        _allTrackRows.Clear();
        Tracks.Clear();
    }

    private void RefreshVisibleTracks()
    {
        var projection = Filters.ApplyToTracks(_allTrackRows);
        // 新投影只是向末尾增长时保持 Add 通知，避免 Reset 让虚拟列表丢失当前滚动位置。
        if (Tracks.Count <= projection.Count
            && Tracks.SequenceEqual(projection.Take(Tracks.Count)))
        {
            Tracks.AddRange(projection.Skip(Tracks.Count).ToList());
            return;
        }

        Tracks.ReplaceAll(projection);
    }

    /// <summary>展开后的排序和筛选覆盖完整歌单；大歌单仍按原有小批次节奏补齐，避免阻塞界面。</summary>
    private Task EnsureAllTracksLoadedForFilterAsync() =>
        _loadAllForFilterTask ??= LoadAllTracksForFilterCoreAsync();

    private async Task LoadAllTracksForFilterCoreAsync()
    {
        await Task.Yield();
        try
        {
            var cancellation = _loadCancellation;
            if (cancellation is null || cancellation.IsCancellationRequested) return;
            var generation = _loadGeneration;
            var ct = cancellation.Token;

            while (Filters.IsActive && IsCurrentLoad(generation, ct))
            {
                while (IsLoadingMore && Filters.IsActive && IsCurrentLoad(generation, ct))
                    await Task.Delay(50, ct);

                if (!Filters.IsActive || !IsCurrentLoad(generation, ct)) break;
                var before = _allTrackRows.Count;

                if (IsAggregate)
                {
                    if (_aggregateLoad is not { HasMore: true }) break;
                    await LoadMoreAggregateAsync(generation, ct);
                }
                else
                {
                    if (_materialized >= _trackIds.Count) break;
                    var target = Math.Min(_trackIds.Count, _materialized + 100);
                    await LoadMoreAsync(target, generation, ct);
                }

                if (_allTrackRows.Count == before) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _loadAllForFilterTask = null;
        }
    }

    private async Task LoadMoreAsync(int? fillTo, int generation, CancellationToken ct)
    {
        if (IsLoadingMore) return;
        IsLoadingMore = true;
        LoadingMoreText = "正在加载更多歌曲…";
        var retriedEmptyBatch = false;
        try
        {
            var target = fillTo ?? Math.Min(_trackIds.Count, _materialized + 100);
            while (_materialized < target && _materialized < _trackIds.Count)
            {
                ct.ThrowIfCancellationRequested();
                // 从当前未解析位置取下一段(≤100 个缺失 id)
                var slice = new List<long>();
                for (var i = _materialized; i < _trackIds.Count && slice.Count < 100; i++)
                    if (!_known.ContainsKey(_trackIds[i])) slice.Add(_trackIds[i]);
                if (slice.Count > 0)
                {
                    List<Song> songs;
                    try
                    {
                        songs = await _api.GetSongsByIdsAsync(slice, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 批量详情被限速(405"操作频繁")或网络失败:按空批走退避重试,不放弃整个补页
                        songs = [];
                        LoadingMoreText = ex is ApiException { Code: NetEaseApiClient.ThrottledCode }
                            ? "网易云限速中，稍后自动重试…"
                            : "加载受阻，稍后自动重试…";
                    }
                    if (!IsCurrentLoad(generation, ct)) return;
                    foreach (var s in songs)
                        if (s.Id != 0) _known[s.Id] = s;
                }

                var before = _materialized;
                AppendKnownTracks();
                if (!IsCurrentLoad(generation, ct)) return;
                if (_materialized == before)
                {
                    // 本批无推进(限速/缺失 id 全部失效):退避 2s 重试一次,仍无推进则停止,滚动时再续
                    if (retriedEmptyBatch) break;
                    retriedEmptyBatch = true;
                    await Task.Delay(2000, ct);
                    continue;
                }
                retriedEmptyBatch = false;
                LoadingMoreText = "正在加载更多歌曲…";
                // 批间小退避(带抖动):贴近真实客户端节奏,降低再次触发账号限速的概率
                if (_materialized < target && _materialized < _trackIds.Count)
                    await Task.Delay(250 + Random.Shared.Next(150), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ApiException)
        {
            // 单批失败不拖垮歌单;滚动时重试
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoadingMore = false;
                if (SelectedPlaylist is { } playlist && _queueSongs.Count > 0)
                    await _musicCache.CachePlaylistTracksAsync(playlist.Playlist, _queueSongs);
            }
        }
    }

    /// <summary>把最近一次成功打开的曲目先恢复到详情页；网络刷新成功后会被新数据替换。</summary>
    private async Task<bool> RestoreCachedTracksAsync(
        PlaylistItemViewModel playlist, int generation, CancellationToken ct)
    {
        var songs = await _musicCache.TryGetPlaylistTracksAsync(playlist.Playlist);
        if (!IsCurrentLoad(generation, ct)) return false;
        if (songs.Count == 0) return false;

        var cachedAudio = await Task.Run(() => _musicCache.GetAudioCacheAvailability(songs), ct);
        if (!IsCurrentLoad(generation, ct)) return false;
        // 离线队列也只保留有音频文件的歌曲，防止上一首/下一首或自动连播绕过行禁用状态。
        _queueSongs.AddRange(songs.Where((_, index) => cachedAudio[index]));
        for (var index = 0; index < songs.Count; index++)
        {
            var song = songs[index];
            song.PreferCachedPlayback = cachedAudio[index];
            var row = new SongItemViewModel(
                song,
                _player.PlayFromList,
                _allTrackRows.Count + 1,
                _queueSongs,
                song.Source == MusicSource.NetEase ? _api : null,
                playlist.Name);
            // 离线快照包含上次已解析的完整曲目元数据，但只有确实存在音频文件的歌曲才能点击。
            row.IsPlayable = cachedAudio[index];
            // 快渲行不走 CreateTrackRow,移除命令需在此注入(这些行会被 ApplyFreshRows 原地复用,
            // 网络刷新也不会重建 —— 漏注入的话"我喜欢的音乐"这类必然命中缓存的歌单永远没有移除项)。
            AttachRemoveCommandIfNeeded(row);
            _allTrackRows.Add(row);
        }
        RefreshVisibleTracks();
        _materialized = songs.Count;
        return true;
    }

    /// <summary>把 trackIds 里连续已解析的曲目物化成列表项。
    /// UI 行与 _queueSongs 仍同步增长，但播放使用 _playbackQueue 的完整逻辑范围。</summary>
    private void AppendKnownTracks()
    {
        var changed = false;
        while (_materialized < _trackIds.Count)
        {
            var id = _trackIds[_materialized];
            if (!_known.TryGetValue(id, out var song)) break;
            _allTrackRows.Add(CreateTrackRow(song, _materialized, _api));
            _queueSongs.Add(song);
            _materialized++;
            changed = true;
        }
        if (changed) RefreshVisibleTracks();
    }

    /// <summary>已上屏的行与 <paramref name="songs"/> 逐首同 id 的最大前缀长度。
    /// 只有这一段敢"原地升级":再往后可能是新增、换序,或只是缓存里多出来的尾巴,内容不可信。</summary>
    private int CommonRowPrefix(IReadOnlyList<Song> songs)
    {
        var limit = Math.Min(_allTrackRows.Count, songs.Count);
        var adopt = 0;
        while (adopt < limit && _allTrackRows[adopt].Song.Id == songs[adopt].Id) adopt++;
        return adopt;
    }

    /// <summary>网络结果落地:共同前缀的行**原地换绑播放委托**(既不换行对象、也不动集合
    /// ⇒ 行控件与已解码的封面都不重建),之后那段整段按新数据重做,可见列表同步跟上。
    ///
    /// 为什么不能整表重建:打开歌单是**两遍装载**(先渲染磁盘缓存,网络回来后换在线数据),
    /// 重建会让每个行容器离树回收;容器复用/重建时 DataContext 先被清成 null,
    /// 行模板里的封面附加属性收到 null 就把已解码的图丢掉,再走一遍**异步** SetSource ——
    /// 中间那几帧露出 ArtFallback 占位色,用户看到的就是"图片又加载了一遍"。
    /// 实测(2026-09-20,--pl-cover-flash):清空重建与"逐位替换集合元素"都会整批换容器,
    /// 只有"集合元素与行对象都不动"才保得住那些图。详见 <see cref="SongItemViewModel.RebindPlayback"/>。
    ///
    /// 歌单封面本来就不受影响:它走 <c>RefreshCover</c>,只在 URL 变了才重载。
    ///
    /// <c>internal</c> 是为了让 <c>--pl-cover-flash</c> 探针能直接驱动生产代码的这一条路径
    /// (与 <see cref="RestoreNavigationSnapshotAsync"/> 同理);生产调用方只有下面两处打开歌单。</summary>
    internal void ApplyFreshRows(IReadOnlyList<Song> songs, Func<Song, int, SongItemViewModel> createRow)
    {
        var lazyQueue = _playbackQueue;
        var adopt = CommonRowPrefix(songs);

        for (var i = 0; i < adopt; i++)
        {
            var index = i;
            // 在线行与缓存行的差别只有"播放队列怎么算":懒歌单走完整逻辑队列,
            // 非懒歌单(QQ 歌单/云盘)仍然是"已物化列表 + 来源名"。
            // (委托类型显式写出来:三元里方法组与 lambda 无法统一推导)
            Func<Song, IReadOnlyList<Song>?, string?, Task<bool>> binding;
            IReadOnlyList<Song>? bindingQueue;
            string? bindingSource;
            if (lazyQueue is null)
            {
                binding = _player.PlayFromList;
                bindingQueue = _queueSongs;
                bindingSource = PlaylistTitle;
            }
            else
            {
                binding = (candidate, _, _) =>
                    _player.PlayFromLazyList(candidate, lazyQueue, index, _queueSongs, PlaylistTitle);
                bindingQueue = null;
                bindingSource = null;
            }
            _allTrackRows[index].RebindPlayback(binding, bindingQueue, bindingSource);
        }

        // 共同前缀之后:内容不可信(新增/换序/缓存尾巴),整段按网络结果重做 —— 本来就要加载。
        while (_allTrackRows.Count > adopt) _allTrackRows.RemoveAt(_allTrackRows.Count - 1);
        for (var i = adopt; i < songs.Count; i++) _allTrackRows.Add(createRow(songs[i], i));
        _materialized = songs.Count;

        if (Filters.IsActive)
        {
            // 筛选激活时可见列表不是 _allTrackRows 的同序投影,顺序/条数都对不上,只能整批重算。
            RefreshVisibleTracks();
            return;
        }

        // 可见列表是 _allTrackRows 的同序投影:前 adopt 项**原样保留**(行对象不动 ⇒ 容器不动
        // ⇒ 封面不重新加载),只在尾部增删。
        if (adopt == 0)
        {
            // 一行都对不上(首次打开、换了歌单、顺序全变):整批换。
            // 交给既有投影(内部不匹配前缀时会走一次 Reset),不再手写一条等价路径。
            RefreshVisibleTracks();
            return;
        }
        if (Tracks.Count > adopt) Tracks.RemoveRange(adopt, Tracks.Count - adopt);
        if (Tracks.Count < _allTrackRows.Count) Tracks.AddRange(_allTrackRows.Skip(Tracks.Count).ToList());
    }

    /// <summary>当前打开的页面是否允许从来源移除歌曲(自己的歌单;云盘/聚合/他人歌单不算)。
    /// 行菜单"从歌单中移除"据此注入。</summary>
    private bool CanRemoveTracksNow => !_isCloud && !IsAggregate
        && SelectedPlaylist?.Playlist.CanAddTracks == true;

    /// <summary>给自己的歌单注入"从歌单中移除"入口;不可移除的页面显式置 null(行 VM 可能来自复用的快照)。</summary>
    private void AttachRemoveCommandIfNeeded(SongItemViewModel row)
        => row.RemoveFromSourceCommand = CanRemoveTracksNow ? RemoveSongFromPlaylistCommand : null;

    private SongItemViewModel CreateTrackRow(Song song, int zeroBasedIndex, NetEaseApiClient? api)
    {
        var lazyQueue = _playbackQueue;
        if (lazyQueue is null)
        {
            var row = new SongItemViewModel(
                song, _player.PlayFromList, zeroBasedIndex + 1, _queueSongs, api, PlaylistTitle);
            AttachRemoveCommandIfNeeded(row);
            return row;
        }

        lazyQueue.Remember(zeroBasedIndex, song);
        var source = PlaylistTitle;
        var lazyRow = new SongItemViewModel(song,
            candidate => _player.PlayFromLazyList(
                candidate, lazyQueue, zeroBasedIndex, _queueSongs, source),
            zeroBasedIndex + 1, api);
        AttachRemoveCommandIfNeeded(lazyRow);
        return lazyRow;
    }

    /// <summary>头部「播放全部」:从首个可播行开始；懒歌单只物化首屏，播放器按需解析完整逻辑队列。</summary>
    [RelayCommand]
    private async Task PlayAllAsync()
    {
        if (Tracks.Count == 0) return;
        // 非懒队列保留原行为；懒队列无需为了播放器提前创建更多 Song/UI 行。
        var cancellation = _loadCancellation;
        if (_playbackQueue is null && cancellation is { IsCancellationRequested: false })
            _ = LoadMoreAsync(fillTo: Math.Min(_trackIds.Count, _materialized + 300),
                generation: _loadGeneration, cancellation.Token);
        var firstPlayable = Tracks.FirstOrDefault(track => track.IsPlayable);
        if (firstPlayable is not null)
            await firstPlayable.PlayCommand.ExecuteAsync(null);
    }

    /// <summary>创建歌单(侧栏分组头"+"按钮):按音源路由到对应客户端。
    /// 成功返回新歌单;失败抛 ApiException(对话框内呈现)。</summary>
    public Task<Playlist> CreatePlaylistAsync(MusicSource source, string name, bool isPrivate)
        => source == MusicSource.QQ
            ? _qqApi.CreatePlaylistAsync(name, isPrivate)
            : _api.CreatePlaylistAsync(name, isPrivate);

    /// <summary>重命名歌单(侧栏右键):按歌单音源路由到对应客户端。
    /// 成功后由宿主刷新侧栏分组(RefreshAfterRenameAsync);失败抛 ApiException(对话框内呈现)。</summary>
    public Task RenamePlaylistAsync(PlaylistItemViewModel item, string newName)
        => item.Playlist.Source == MusicSource.QQ
            ? _qqApi.RenamePlaylistAsync(item.Playlist, newName)
            : _api.RenamePlaylistAsync(item.Playlist, newName);

    /// <summary>删除自己创建的歌单(侧栏右键,确认弹窗后调用):按歌单音源路由到对应客户端。
    /// 成功后由宿主刷新侧栏分组(RefreshAfterDeleteAsync);失败抛 ApiException(对话框内呈现)。</summary>
    public Task DeletePlaylistAsync(PlaylistItemViewModel item)
        => item.Playlist.Source == MusicSource.QQ
            ? _qqApi.DeletePlaylistAsync(item.Playlist)
            : _api.DeletePlaylistAsync(item.Playlist);

    /// <summary>该歌单是否为红心集合("我喜欢"):红心集合不允许重命名/删除,右键菜单据此隐藏。
    /// QQ 按资产目录 id(201)识别,网易云按拉列表时定位到的喜欢集合歌单 id。</summary>
    public bool IsLikedPlaylist(Playlist playlist)
        => playlist.Source == MusicSource.QQ
            ? playlist.DirId == QQMusicApiClient.LikedDirId || playlist.Name == "我喜欢"
            : playlist.Id != 0 && playlist.Id == _api.LikedPlaylistId;

    /// <summary>重命名成功后刷新对应侧栏分组(换新实例,侧栏名随列表更新);
    /// 若该歌单正作为详情页打开,把详情页切到新实例并同步标题(曲目不动)。</summary>
    public async Task RefreshAfterRenameAsync(PlaylistItemViewModel renamed)
    {
        var source = renamed.Playlist.Source;
        if (source == MusicSource.QQ)
            await ReloadQqPlaylistsAsync();
        else
            await ReloadNetEasePlaylistsAsync();

        var fresh = source == MusicSource.QQ
            ? QqPlaylists.FirstOrDefault(p => p.Id == renamed.Id)
            : Playlists.FirstOrDefault(p => p.Id == renamed.Id);
        if (fresh is null) return;
        if (ReferenceEquals(SelectedPlaylist, renamed))
        {
            SelectedPlaylist = fresh;
            PlaylistTitle = fresh.Name;
        }
    }

    /// <summary>删除成功后刷新对应侧栏分组;若被删歌单正作为详情页打开,清空详情回到
    /// "请选择歌单"占位态(自增加载代次使在途曲目加载作废)。</summary>
    public async Task RefreshAfterDeleteAsync(PlaylistItemViewModel deleted)
    {
        var wasOpen = ReferenceEquals(SelectedPlaylist, deleted);
        if (deleted.Playlist.Source == MusicSource.QQ)
            await ReloadQqPlaylistsAsync();
        else
            await ReloadNetEasePlaylistsAsync();

        if (wasOpen)
            ResetDetailPage();
    }

    /// <summary>聚合歌单在侧栏被重命名(集合中已换新实例)后,若打开中的详情页正是它,
    /// 换引用保持后续聚合设置弹窗的比较一致,并同步合成歌单名与标题(曲目不动)。</summary>
    public void ApplyAggregateRename(Models.AggregatePlaylist old, Models.AggregatePlaylist fresh)
    {
        if (!ReferenceEquals(_currentAggregate, old)) return;
        _currentAggregate = fresh;
        SelectedPlaylist = new PlaylistItemViewModel(new Playlist { Name = fresh.Name });
        PlaylistTitle = fresh.Name;
    }

    /// <summary>聚合歌单被删除后清空详情页回占位态(自增加载代次使在途合并拉取作废)。</summary>
    public void CloseAggregateDetail()
    {
        if (_currentAggregate is null && !IsAggregate) return;
        ResetDetailPage();
    }

    /// <summary>详情页整体复位:清曲目与增量加载状态、解除打开的聚合引用,回到"请选择歌单"占位态。</summary>
    private void ResetDetailPage()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _isCloud = false;
        _currentAggregate = null;
        _aggregateLoad = null;
        IsAggregate = false;
        SelectedPlaylist = null;
        PlaylistTitle = "";
        CreatorName = "";
        ClearTrackRows();
        Filters.Reset();
        _trackIds = new List<long>();
        _known.Clear();
        _queueSongs.Clear();
        _playbackQueue = null;
        _aggregatePlaybackQueue = null;
        _materialized = 0;
        IsBusy = false;
        IsLoadingMore = false;
        Message = null;
    }

    /// <summary>重新拉取网易云用户歌单(创建/删除歌单后刷新侧栏;静默失败,保底不清空现列表)。</summary>
    public async Task ReloadNetEasePlaylistsAsync()
    {
        if (!IsLoggedIn) return;
        try
        {
            var profile = await _api.GetUserProfileAsync();
            UserName = profile.Nickname;
            var items = await _api.GetUserPlaylistItemsAsync(profile.UserId);
            SplitNetEasePlaylists(items);
            _netEaseRestoredFromCache = false;
            await _musicCache.CachePlaylistListAsync(MusicSource.NetEase, UserName,
                items.Select(i => new Playlist
                {
                    Id = i.Id, Name = i.Name, CoverUrl = i.CoverUrl, TrackCount = i.TrackCount,
                    PlayCount = i.PlayCount, Source = MusicSource.NetEase, CanAddTracks = !i.Subscribed,
                }).ToList());
        }
        catch (Exception ex) when (ex is ApiException || IsConnectivityFailure(ex))
        {
            // 刷新失败保持现列表(侧栏旧数据仍可用)
        }
    }

    /// <summary>重新拉取 QQ 用户歌单(创建/删除歌单后刷新侧栏;失败静默保持现列表)。</summary>
    public async Task ReloadQqPlaylistsAsync()
    {
        if (!IsQqLoggedIn) return;
        await LoadQqPlaylistsAsync();
    }

    private async Task LoadProfileAndPlaylistsAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var profile = await _api.GetUserProfileAsync();
            IsLoggedIn = true;
            _netEaseRestoredFromCache = false;
            UserName = profile.Nickname;
            // 有正在展示/加载中的具体歌单(如刚点了个性推荐的卡片)时不得覆盖创建者,
            // 也不得自动打开"我喜欢的音乐" —— 否则会与该歌单的加载代次竞争,
            // 把用户点开的歌单顶掉、创建者显示成自己(实测)。
            if (SelectedPlaylist is null)
                CreatorName = profile.Nickname;
            AvatarUrl = profile.AvatarUrl;

            var items = await _api.GetUserPlaylistItemsAsync(profile.UserId);
            SplitNetEasePlaylists(items);
            // 此刻"我喜欢的音乐"歌单 id 已按新账号刷新,播放条可以重新判红心
            NotifyAccountChanged();
            await _musicCache.CachePlaylistListAsync(MusicSource.NetEase, UserName,
                items.Select(i => new Playlist
                {
                    Id = i.Id, Name = i.Name, CoverUrl = i.CoverUrl, TrackCount = i.TrackCount,
                    PlayCount = i.PlayCount, Source = MusicSource.NetEase, CanAddTracks = !i.Subscribed,
                }).ToList());

            // 网易云通常把“我喜欢的音乐”放在首位；自动打开它，使该入口直接呈现可用的歌单详情。
            // 仅在当前没有展示任何具体歌单、且用户正停在收藏页等待时才自动打开
            // (守卫由 MainViewModel 注入)—— 启动页是用户页,登录恢复不得把用户顶离。
            if (Playlists.Count > 0 && SelectedPlaylist is null && AutoOpenFavoritesGuard?.Invoke() != false)
                await OpenPlaylistAsync(Playlists[0]);
        }
        catch (Exception ex) when (ex is ApiException || IsConnectivityFailure(ex))
        {
            Message = _netEaseRestoredFromCache
                ? "当前网络不可用，已恢复离线歌单"
                : $"登录失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool IsConnectivityFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException;

    /// <summary>构造时同步恢复轻量索引，使主导航第一次构建就能看到离线歌单。</summary>
    private void RestoreCachedLibraries()
    {
        if (_musicCache.TryGetPlaylistLibrary(MusicSource.NetEase) is { } netEase)
        {
            UserName = netEase.UserName;
            CreatorName = netEase.UserName;
            foreach (var playlist in netEase.Playlists)
                Playlists.Add(new PlaylistItemViewModel(playlist));
            // 离线资料的可见性不依赖 Cookie；有本地凭证时才恢复账号登录外观并尝试联网验证。
            if (_cookie.MusicU is { Length: > 0 })
            {
                IsLoggedIn = true;
                _netEaseRestoredFromCache = true;
            }
        }

        if (_musicCache.TryGetPlaylistLibrary(MusicSource.QQ) is { } qq)
        {
            QqUserName = qq.UserName;
            foreach (var playlist in qq.Playlists)
                QqPlaylists.Add(new PlaylistItemViewModel(playlist));
            if (_cookie.QQCookieRaw is { Length: > 0 })
            {
                IsQqLoggedIn = true;
                _qqRestoredFromCache = true;
            }
        }
    }
}
