using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>浏览页卡片:榜单、歌单、新歌共用同一种卡片(封面 + 标题 + 副标题 + 播放量角标 + 音源角标)。
/// 整卡是一个按钮,点击行为由构造时传入的 activate 决定:榜单/歌单进详情页,新歌直接播放。</summary>
public sealed partial class BrowseCardViewModel : ViewModelBase
{
    /// <summary>页面过渡(300ms)结束再亮出封面:首帧一次性挂十几二十张位图会把切页动画拖住
    /// (个性推荐 30 张时实测 ~250ms),延后提交后视觉上就是"转场结束、封面到位"。</summary>
    private static readonly TimeSpan CoverTransitionDelay = TimeSpan.FromMilliseconds(320);

    private readonly Func<Task>? _activate;
    private bool _coverRequestScheduled;

    public BrowseCardViewModel(string title, string subtitle, string coverUrl, MusicSource source,
        long playCount = 0, Func<Task>? activate = null, string badge = "", PlaylistItemViewModel? playlist = null)
    {
        Title = title;
        Subtitle = subtitle;
        CoverUrl = coverUrl;
        Source = source;
        PlayCount = playCount;
        Badge = badge;
        _activate = activate;
        Playlist = playlist;
    }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>歌单/榜单卡片携带的数据(封面上的 ▶ 播放、••• 与右键菜单共用);歌曲卡片为 null。
    /// 卡片的点击行为另由 <see cref="_activate"/> 承担 —— 点封面进歌单页,点 ▶ 才播。</summary>
    public PlaylistItemViewModel? Playlist { get; }

    /// <summary>是否歌单卡(含榜单):只有这两类长操作圆钮,歌曲卡点击即播放,没有"整张歌单"语义。</summary>
    public bool IsPlaylist => Playlist is not null;

    public string CoverUrl { get; }

    /// <summary>封面右上角的时间徽章(「3 天前」)。新歌没有播放量,那个位置空着不如给发行时间。</summary>
    public string Badge { get; }

    public bool HasBadge => Badge.Length > 0;

    /// <summary>音源:决定封面左下角角标,也是"全部"筛选下唯一的平台标识。</summary>
    public MusicSource Source { get; }

    public long PlayCount { get; }

    public bool HasPlayCount => PlayCount > 0;

    public string PlayCountText => HasPlayCount ? NetEaseApiClient.FormatPlayCount(PlayCount) : "";

    public IImage? SourceBadge => SourceBadges.For(Source);

    /// <summary>封面左下角角标里的平台名。这一页两个源混排,光靠 favicon 认不出来。</summary>
    public string SourceLabel => Source == MusicSource.QQ ? "QQ音乐" : "网易云音乐";

    /// <summary>视图实际请求的封面 URL:过渡期间保持 null(→ 占位底色),过渡结束后才开始加载。</summary>
    [ObservableProperty] private string? _displayCoverUrl;

    /// <summary>卡片第一次实化时延迟到页面过渡结束再请求封面;已显示过的卡片保持 URL,
    /// 不在筛选/刷新往返时先清空再排队(否则会先闪占位底色)。</summary>
    public void PrepareCoverForTransition()
    {
        if (_coverRequestScheduled
            || string.Equals(DisplayCoverUrl, CoverUrl, StringComparison.Ordinal))
            return;

        _coverRequestScheduled = true;
        _ = CommitCoverAfterTransitionAsync();
    }

    private async Task CommitCoverAfterTransitionAsync()
    {
        await Task.Delay(CoverTransitionDelay).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _coverRequestScheduled = false;
            DisplayCoverUrl = CoverUrl;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    [RelayCommand]
    private Task OpenAsync() => _activate?.Invoke() ?? Task.CompletedTask;
}

/// <summary>浏览页顶部海报位(一个官方榜单):大标题 + 一句导语 + 元信息 + 封面。
/// 数据与榜单卡片同源,但文案更长(卡片只有一行副标题),所以单独成类而不是塞进卡片。</summary>
public sealed partial class BrowseHeroViewModel : ViewModelBase
{
    private readonly Func<Task> _activate;

    public BrowseHeroViewModel(ToplistItem item, Func<Task> activate)
    {
        Title = item.Name;
        CoverUrl = item.CoverUrl;
        Source = item.Source;
        _activate = activate;
        // 海报位只放一句介绍,不铺整段说明(视图侧 MaxLines=2 兜底截断)。
        var summary = Summarize(item.Description);
        Description = summary.Length > 0 ? summary : FallbackDescription(item.Source);

        var parts = new List<string>(3);
        if (item.UpdateText.Length > 0) parts.Add(item.UpdateText);
        if (item.TrackCount > 0) parts.Add($"{item.TrackCount} 首");
        if (item.PlayCount > 0) parts.Add(NetEaseApiClient.FormatPlayCount(item.PlayCount));
        MetaLine = string.Join(" · ", parts);
    }

    public string Title { get; }

    public string CoverUrl { get; }

    public string Description { get; }

    /// <summary>「每日更新 · 100 首 · 1.2亿播放」:海报位底部那行小字。</summary>
    public string MetaLine { get; }

    public MusicSource Source { get; }

    public string SourceLabel => Source == MusicSource.QQ ? "QQ音乐" : "网易云音乐";

    /// <summary>海报位上的角标文案:两个源混排,只写平台名分不清这是榜单还是歌单。</summary>
    public string Kicker => $"{SourceLabel} · 官方榜单";

    public IImage? SourceBadge => SourceBadges.For(Source);

    [RelayCommand]
    private Task OpenAsync() => _activate();

    /// <summary>两个源的导语形态完全不同:网易云是一整句文案,QQ 是带 <c>&lt;br&gt;</c> 的条目清单
    /// (「1. 榜单定义：…&lt;br&gt;2. 更新频率：…」)。海报位只留第一句、去掉序号和「标签：」前缀,
    /// 否则会把原始标签和编号直接铺到图上。</summary>
    private static string Summarize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = WebUtility.HtmlDecode(Tags().Replace(raw, " ")).Replace('\u00A0', ' ');
        text = Whitespace().Replace(text, " ").Trim();
        text = LeadingIndex().Replace(text, "");

        // 只保留第一句;没有句号(短文案)就整段用。
        var end = text.IndexOf('。');
        if (end > 0) text = text[..(end + 1)];

        // 「榜单定义：」这类前缀在图上没有信息量,去掉后剩下的才是给人读的那句。
        text = LeadingLabel().Replace(text, "").Trim();
        return text.Length <= 160 ? text : text[..160];
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\s*\d+\s*[.、)）]\s*")]
    private static partial Regex LeadingIndex();

    [GeneratedRegex(@"^[^：:]{2,8}[：:]\s*")]
    private static partial Regex LeadingLabel();

    private static string FallbackDescription(MusicSource source) => source == MusicSource.QQ
        ? "QQ音乐官方榜单，按当天的收听热度排出座次。"
        : "网易云音乐官方榜单，跟着播放趋势每日更新。";
}

/// <summary>平台筛选胶囊:一个图标 + 文案。「全部」用字形图标,两个平台用音源角标 ——
/// 商店的分类入口都是带图标的胶囊,纯文字那排看着像表单。</summary>
public sealed record BrowseSourceFilterViewModel(string Label, IImage? Badge, string? Glyph = null)
{
    public bool HasBadge => Badge is not null;

    public bool HasGlyph => !string.IsNullOrEmpty(Glyph);
}

/// <summary>浏览页区块:标题 + 一行横向卡片。筛选平台时只换 Items 的内容,
/// 区块本身常驻(卡片实例保留在各自的源列表里,切回来不重新解码封面)。
/// 「关注新歌」要等逐位歌手查完才出内容,所以额外带 <see cref="IsBusy"/>:
/// 那段空窗期显示一行"正在汇总",而不是整块消失让人以为没这个功能。</summary>
public sealed partial class BrowseSectionViewModel : ViewModelBase
{
    public BrowseSectionViewModel(string title, string caption = "")
    {
        Title = title;
        Caption = caption;
        // 空/满与"这一块有几个源"都由 Items 派生:谁改集合都自动刷新,调用方不用记得补通知。
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsSectionVisible));
            OnPropertyChanged(nameof(ShowBusy));
            OnPropertyChanged(nameof(SourceSummary));
        };
    }

    public string Title { get; }

    /// <summary>标题右侧的小字(「最近 3 个月」)。空则不占位。</summary>
    [ObservableProperty] private string _caption;

    public bool HasCaption => Caption.Length > 0;

    partial void OnCaptionChanged(string value) => OnPropertyChanged(nameof(HasCaption));

    [ObservableProperty] private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSectionVisible));
        OnPropertyChanged(nameof(ShowBusy));
    }

    /// <summary>只有"还没有内容又要等"时才显示进度行:刷新时旧卡片还在,再压一行进度会显得抖动。</summary>
    public bool ShowBusy => IsBusy && !HasItems;

    public ObservableCollection<BrowseCardViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => !HasItems;

    /// <summary>区块整体显隐:有内容、或正在拉内容。</summary>
    public bool IsSectionVisible => HasItems || IsBusy;

    /// <summary>区块右侧来源提示:混合内容显示"网易云音乐 + QQ音乐",单一来源只显示那个平台。</summary>
    public string SourceSummary
    {
        get
        {
            var hasNe = Items.Any(card => card.Source == MusicSource.NetEase);
            var hasQq = Items.Any(card => card.Source == MusicSource.QQ);
            return (hasNe, hasQq) switch
            {
                (true, true) => "网易云音乐 + QQ音乐",
                (true, false) => "网易云音乐",
                (false, true) => "QQ音乐",
                _ => "",
            };
        }
    }
}

/// <summary>
/// 浏览页:把两个音源的发现内容放在同一页 —— 官方榜单(网易云「飙升/新歌/热歌/原创」+ QQ「巅峰榜」)、
/// 推荐歌单(网易云个性化推荐,QQ 无匿名公开入口)、新歌速递(两边的新歌榜前几首)。
///
/// <para>
/// 每条内容都带音源角标,并按平台交错混排,所以默认视图里第一屏就能同时看到两个源;
/// 顶部平台筛选(全部/网易云/QQ)只是重新投影同一批卡片实例,不会重新请求网络。
/// </para>
/// </summary>
public sealed partial class BrowseViewModel : ViewModelBase
{
    // 每源多取两张:进了轮播的那几张会从货架里剔除(见 LoadToplistsAsync),留点余量货架才不会秃。
    private const int ToplistPerSource = 7;
    private const int HeroCount = 5;
    private const int RecommendCount = 8;
    private const int NewSongsPerSource = 5;

    /// <summary>网易云新歌榜(官方榜单 id):新歌速递区块的网易云侧内容。</summary>
    private const long NetEaseNewSongToplistId = 3779629;

    /// <summary>QQ 新歌榜 topId:与网易云新歌榜配对。</summary>
    private const int QqNewSongToplistId = 27;

    private readonly NetEaseApiClient _api;
    private readonly QQMusicApiClient _qq;
    private readonly PlayerViewModel _player;
    private readonly DispatcherService _dispatcher;
    private readonly FollowedNewSongService _followedFeed;

    private bool _loading;
    private bool _loaded;

    // 三个区块的完整卡片池(两个源拼在一起)。筛选只是从这里取子集。
    private IReadOnlyList<BrowseCardViewModel> _toplists = [];
    private IReadOnlyList<BrowseHeroViewModel> _heroes = [];
    private IReadOnlyList<BrowseCardViewModel> _recommended = [];
    private IReadOnlyList<BrowseCardViewModel> _newSongs = [];

    /// <summary>新歌速递的播放队列:点任意一首,上一首/下一首在两边的新歌之间连续切换。</summary>
    private IReadOnlyList<Song> _newSongQueue = [];

    /// <summary>关注新歌的卡片与播放队列(独立加载,后到)。</summary>
    private IReadOnlyList<BrowseCardViewModel> _followed = [];
    private IReadOnlyList<Song> _followedQueue = [];
    private CancellationTokenSource? _followedCts;

    public BrowseViewModel(NetEaseApiClient api, QQMusicApiClient qq, DispatcherService dispatcher,
        PlayerViewModel player, FollowedNewSongService followedFeed)
    {
        _api = api;
        _qq = qq;
        _dispatcher = dispatcher;
        _player = player;
        _followedFeed = followedFeed;
        ToplistsSection = new BrowseSectionViewModel("排行榜");
        RecommendedSection = new BrowseSectionViewModel("推荐歌单");
        NewSongsSection = new BrowseSectionViewModel("新歌速递");
        FollowedSection = new BrowseSectionViewModel("关注新歌",
            $"最近 {FollowedNewSongService.LookbackMonths} 个月");
        // 「关注新歌」排在「新歌速递」之前:它是按自己的关注列表算的,和下面的榜单没有重叠。
        // 它要逐位歌手查(近 50 次请求),所以**不阻塞**首屏:前三块先上屏,它自己后补。
        Sections = [ToplistsSection, RecommendedSection, FollowedSection, NewSongsSection];
        HeroItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMultipleHeroes));
        SourceFilters =
        [
            new BrowseSourceFilterViewModel("全部", null, "\uF45F"),
            new BrowseSourceFilterViewModel("网易云音乐", SourceBadges.For(MusicSource.NetEase)),
            new BrowseSourceFilterViewModel("QQ音乐", SourceBadges.For(MusicSource.QQ)),
        ];
    }

    /// <summary>平台筛选胶囊的数据源(固定三项,和 <see cref="SourceIndex"/> 一一对应)。</summary>
    public IReadOnlyList<BrowseSourceFilterViewModel> SourceFilters { get; }

    public ObservableCollection<BrowseSectionViewModel> Sections { get; }

    /// <summary>海报位的候选(按平台交错)。筛选平台时用它重新投影成 <see cref="HeroItems"/>。</summary>
    public ObservableCollection<BrowseHeroViewModel> HeroItems { get; } = new();

    /// <summary>当前海报位;null = 没有候选(加载中/全部失败),视图整块隐藏。</summary>
    [ObservableProperty] private BrowseHeroViewModel? _hero;

    /// <summary>与海报位圆点列表双向绑定:用户点圆点、或代码设值(切平台/翻页)都走这里。</summary>
    [ObservableProperty] private int _heroIndex;

    public bool HasHero => Hero is not null;

    public bool HasMultipleHeroes => HeroItems.Count > 1;

    partial void OnHeroChanged(BrowseHeroViewModel? value) => OnPropertyChanged(nameof(HasHero));

    /// <summary>圆点列表在重建瞬间会把 SelectedIndex 写成 -1,别让那一下把海报位清空。</summary>
    partial void OnHeroIndexChanged(int value)
    {
        if (value >= 0 && value < HeroItems.Count) Hero = HeroItems[value];
    }

    [RelayCommand]
    private void NextHero()
    {
        if (HeroItems.Count > 1) HeroIndex = (HeroIndex + 1) % HeroItems.Count;
    }

    [RelayCommand]
    private void PrevHero()
    {
        if (HeroItems.Count > 1) HeroIndex = (HeroIndex - 1 + HeroItems.Count) % HeroItems.Count;
    }

    // ---------- 右侧三宫格宣传位 ----------

    /// <summary>上排那张通栏宣传位(商店右侧最大的那张)。</summary>
    public BrowseCardViewModel? TopPromo { get; private set; }

    public BrowseCardViewModel? BottomLeft { get; private set; }

    public BrowseCardViewModel? BottomRight { get; private set; }

    public bool HasPromos => TopPromo is not null && BottomLeft is not null && BottomRight is not null;

    /// <summary>三张宣传位是"一个整体":任意一张缺了就整块收起,否则会露出半格空位。</summary>
    private void SetPromos(IReadOnlyList<BrowseCardViewModel> pick)
    {
        TopPromo = pick.Count > 0 ? pick[0] : null;
        BottomLeft = pick.Count > 1 ? pick[1] : null;
        BottomRight = pick.Count > 2 ? pick[2] : null;
        OnPropertyChanged(nameof(TopPromo));
        OnPropertyChanged(nameof(BottomLeft));
        OnPropertyChanged(nameof(BottomRight));
        OnPropertyChanged(nameof(HasPromos));
    }

    /// <summary>"全部"下两个源的推荐歌单轮流占格(网易云 / QQ / 网易云),单平台筛选就三张同源。
    /// 取**每个源的最后三张**:货架里同一批歌单是从头排的,宣传位再取前三张就会和紧挨着的
    /// 前三张卡片一模一样(同一摞封面在一屏里出现两次);取尾部则落在货架右端,默认视野外。
    /// 某一源歌单没拿到时用新歌补位 —— 缺一张就整块收起,白白少一列宣传位。</summary>
    private void RebuildPromos()
    {
        var nePlaylists = _recommended.Where(card => card.Source == MusicSource.NetEase).ToList();
        var qqPlaylists = _recommended.Where(card => card.Source == MusicSource.QQ).ToList();
        var pick = SourceIndex switch
        {
            1 => nePlaylists.TakeLast(3).ToList(),
            2 => qqPlaylists.TakeLast(3).ToList(),
            _ => Interleave(nePlaylists.TakeLast(2).ToList(), qqPlaylists.TakeLast(2).ToList()).Take(3).ToList(),
        };

        if (pick.Count < 3)
        {
            var fillers = SourceIndex switch
            {
                1 => _newSongs.Where(card => card.Source == MusicSource.NetEase),
                2 => _newSongs.Where(card => card.Source == MusicSource.QQ),
                _ => _newSongs,
            };
            foreach (var card in fillers)
            {
                if (pick.Count >= 3) break;
                if (!pick.Contains(card)) pick.Add(card);
            }
        }
        SetPromos(pick);
    }

    public BrowseSectionViewModel ToplistsSection { get; }

    public BrowseSectionViewModel RecommendedSection { get; }

    public BrowseSectionViewModel NewSongsSection { get; }

    /// <summary>关注歌手最近三个月的新歌(需登录;都没登录时整块隐藏)。</summary>
    public BrowseSectionViewModel FollowedSection { get; }

    /// <summary>平台筛选:0=全部,1=网易云音乐,2=QQ音乐(与个人主页的筛选顺序一致)。</summary>
    [ObservableProperty] private int _sourceIndex;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private string? _statusMessage;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public bool IsEmpty => !IsLoading && Sections.All(section => !section.HasItems);

    /// <summary>首载(幂等):榜单与新歌不依赖登录态,整页只拉一次;需要重新取用 <see cref="RefreshCommand"/>。</summary>
    public Task EnsureLoadedAsync() => _loaded || _loading ? Task.CompletedTask : LoadAsync(force: false);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_loading) return;
        await LoadAsync(force: true);
    }

    partial void OnSourceIndexChanged(int value) => RebuildSections();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        _loading = true;
        IsLoading = true;
        StatusMessage = null;
        try
        {
            // 三块内容并行;单块失败只影响自己,另一源照常上屏。
            var toplistsTask = LoadToplistsAsync();
            var recommendedTask = LoadRecommendedAsync();
            var newSongsTask = LoadNewSongsAsync();
            await Task.WhenAll(toplistsTask, recommendedTask, newSongsTask).ConfigureAwait(false);

            var (toplists, heroes, toplistFailures) = await toplistsTask.ConfigureAwait(false);
            var (recommended, recommendFailures) = await recommendedTask.ConfigureAwait(false);
            var (newSongs, newSongQueue, newSongFailures) = await newSongsTask.ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                _toplists = toplists;
                _heroes = heroes;
                _recommended = recommended;
                _newSongs = newSongs;
                _newSongQueue = newSongQueue;
                // 一条内容都没拿到(断网)不算"已加载":下次进页面还要自动重试,别让用户先点刷新。
                _loaded = toplists.Count > 0 || heroes.Count > 0 || recommended.Count > 0 || newSongs.Count > 0;
                ApplySection(ToplistsSection, _toplists);
                ApplySection(RecommendedSection, _recommended);
                ApplySection(NewSongsSection, _newSongs);
                RebuildHero();
                RebuildPromos();
                StatusMessage = BuildStatus(toplistFailures | recommendFailures | newSongFailures);
            }).ConfigureAwait(false);

            // 关注新歌要逐位歌手查(近 50 次请求),**不** await:上面三块已经上屏,
            // 它自己在后台补充,完成后单独刷新自己的区块。
            _followedCts?.Cancel();
            _followedCts = new CancellationTokenSource();
            _ = LoadFollowedAsync(force, _followedCts.Token);
        }
        catch (Exception)
        {
            await _dispatcher.InvokeAsync(() =>
                StatusMessage = "暂时无法加载浏览内容，请检查网络后刷新。").ConfigureAwait(false);
        }
        finally
        {
            _loading = false;
            await _dispatcher.InvokeAsync(() => IsLoading = false).ConfigureAwait(false);
        }
    }

    /// <summary>关注新歌:独立于前三块加载。未登录任一平台时直接返回(区块保持隐藏)。</summary>
    private async Task LoadFollowedAsync(bool force, CancellationToken ct)
    {
        if (!_followedFeed.Enabled) return;

        await _dispatcher.InvokeAsync(() => FollowedSection.IsBusy = true).ConfigureAwait(false);
        try
        {
            var result = await _followedFeed.GetAsync(force, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            await _dispatcher.InvokeAsync(() =>
            {
                _followedQueue = result.Songs;
                _followed = result.Songs.Select(ToFollowedCard).ToList();
                ApplySection(FollowedSection, _followed);
                FollowedSection.Caption = BuildFollowedCaption(result);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 刷新时被新的一轮顶掉:保持现状即可
        }
        catch
        {
            await _dispatcher.InvokeAsync(() =>
            {
                FollowedSection.Caption = $"最近 {FollowedNewSongService.LookbackMonths} 个月 · 加载失败";
            }).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => FollowedSection.IsBusy = false).ConfigureAwait(false);
        }
    }

    private static string BuildFollowedCaption(FollowedNewSongResult result)
    {
        var window = $"最近 {FollowedNewSongService.LookbackMonths} 个月";
        if (result.Songs.Count > 0) return $"{window} · {result.Songs.Count} 首";
        if (result.NetEaseFailed && result.QqFailed) return $"{window} · 加载失败";
        // 拿到了名单却一首都没命中,和"一个歌手都没关注"是两件事,文案要分开。
        return result.HasArtists ? $"{window} · 这段时间没发新歌" : window;
    }

    private BrowseCardViewModel ToFollowedCard(Song song)
        => new(song.Name, song.Artist, song.CoverUrl, song.Source, 0,
            () => _player.PlayFromList(song, _followedQueue, "关注新歌"),
            badge: RelativeDate(song.PublishDate));

    /// <summary>徽章文案:新歌的时间感比"发行日期"更重要,超过一个月才退回月数。</summary>
    private static string RelativeDate(string publishDate)
    {
        if (!DateOnly.TryParseExact(publishDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return "";
        var days = DateOnly.FromDateTime(DateTime.Today).DayNumber - date.DayNumber;
        return days switch
        {
            <= 0 => "今天",
            1 => "昨天",
            < 7 => $"{days} 天前",
            < 30 => $"{days / 7} 周前",
            _ => $"{days / 30} 个月前",
        };
    }

    /// <summary>失败位:bit0=网易云,bit1=QQ。有没有内容才决定提示口径 ——
    /// 用卡片池判断而不是当前筛选后的可见项,否则切到单一平台时提示会说反。
    /// 海报位单独算:轮播里的榜单不在货架里,只看货架会把"只有轮播有内容"误判成整源不可用。</summary>
    private string? BuildStatus(int failures)
    {
        if (failures == 0) return null;
        var all = _toplists.Concat(_recommended).Concat(_newSongs).ToList();
        var neOk = all.Any(card => card.Source == MusicSource.NetEase)
                   || _heroes.Any(hero => hero.Source == MusicSource.NetEase);
        var qqOk = all.Any(card => card.Source == MusicSource.QQ)
                   || _heroes.Any(hero => hero.Source == MusicSource.QQ);
        return (neOk, qqOk) switch
        {
            (true, true) => null,
            (true, false) => "QQ音乐的内容暂时不可用，已显示网易云音乐的内容。",
            (false, true) => "网易云音乐的内容暂时不可用，已显示 QQ音乐的内容。",
            _ => "暂时无法加载浏览内容，请检查网络后刷新。",
        };
    }

    private async Task<(IReadOnlyList<BrowseCardViewModel> Cards, IReadOnlyList<BrowseHeroViewModel> Heroes, int Failures)> LoadToplistsAsync()
    {
        var neTask = SafeAsync(_api.GetToplistsAsync(ToplistPerSource));
        var qqTask = SafeAsync(_qq.GetToplistsAsync(ToplistPerSource));
        await Task.WhenAll(neTask, qqTask).ConfigureAwait(false);

        var ne = await neTask.ConfigureAwait(false);
        var qq = await qqTask.ConfigureAwait(false);
        var items = Interleave(ne.Items, qq.Items);
        // 海报位取交错后的前几张:第一张是网易云、第二张是 QQ,切"下一个"就能换平台看。
        var heroes = items.Take(HeroCount).Select(ToHero).ToList();
        // 进了轮播的榜单不再进货架 —— 同一张封面在一屏里出现两次,看着像内容重复。
        // 因为"轮播候选"就是交错序列的前缀,按平台筛选后各源的轮播成员也正好是它们在该源里的前缀,
        // 所以直接 Skip 就能对任意筛选组合保持一致(不用额外记住哪张被轮播用过)。
        var cards = items.Skip(HeroCount).Select(ToToplistCard).ToList();
        return (cards, heroes, (ne.Failed ? 1 : 0) | (qq.Failed ? 2 : 0));
    }

    private async Task<(IReadOnlyList<BrowseCardViewModel> Cards, int Failures)> LoadRecommendedAsync()
    {
        // QQ 侧的「你的私荐歌单」:推荐流里的歌单卡,不够再用歌单广场补位(客户端里的同名入口)。
        var neTask = SafeAsync(_api.GetPersonalizedPlaylistsAsync(RecommendCount));
        var qqTask = SafeAsync(_qq.GetRecommendedPlaylistsAsync(RecommendCount));
        await Task.WhenAll(neTask, qqTask).ConfigureAwait(false);

        var ne = await neTask.ConfigureAwait(false);
        var qq = await qqTask.ConfigureAwait(false);
        var cards = Interleave(ne.Items, qq.Items).Select(ToPlaylistCard).ToList();
        return (cards, (ne.Failed ? 1 : 0) | (qq.Failed ? 2 : 0));
    }

    private async Task<(IReadOnlyList<BrowseCardViewModel> Cards, IReadOnlyList<Song> Queue, int Failures)> LoadNewSongsAsync()
    {
        // 两边都用"新歌榜"前几首:一首一城,卡片副标题就是歌手,和榜单区块区分开。
        var neTask = SafeAsync(_api.GetPlaylistTracksAsync(NetEaseNewSongToplistId, NewSongsPerSource));
        var qqTask = SafeAsync(_qq.GetToplistSongsAsync(QqNewSongToplistId, NewSongsPerSource));
        await Task.WhenAll(neTask, qqTask).ConfigureAwait(false);

        var ne = await neTask.ConfigureAwait(false);
        var qq = await qqTask.ConfigureAwait(false);
        // 两边都是"新歌榜",重叠很常见(同一首新歌两个平台都会上架)。同一个录音只留网易云那条,
        // 判据复用搜索页的保守匹配(歌名归一化相等 + 歌手吻合 + 时长差 ≤5s),证据不足时宁可留两条。
        var qqOnly = SongSearchMerger.KeepUnmatched(ne.Items, qq.Items);
        // 播放队列用**和界面一致**的交错顺序:下一首切到的就是右边那张卡片,不是"先放完网易云再放 QQ"。
        var queue = Interleave(ne.Items, qqOnly);
        var cards = queue.Select(song => ToSongCard(song, queue)).ToList();
        return (cards, queue, (ne.Failed ? 1 : 0) | (qq.Failed ? 2 : 0));
    }

    private void ApplySection(BrowseSectionViewModel section, IReadOnlyList<BrowseCardViewModel> pool)
    {
        var desired = SourceIndex switch
        {
            1 => pool.Where(card => card.Source == MusicSource.NetEase).ToList(),
            2 => pool.Where(card => card.Source == MusicSource.QQ).ToList(),
            _ => pool,
        };
        CollectionSync.Apply(section.Items, desired);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RebuildSections()
    {
        ApplySection(ToplistsSection, _toplists);
        ApplySection(RecommendedSection, _recommended);
        ApplySection(NewSongsSection, _newSongs);
        ApplySection(FollowedSection, _followed);
        RebuildHero();
        RebuildPromos();
    }

    /// <summary>海报位跟着平台筛选走:只换投影,不重新请求;选中的永远是筛完的第一张。
    /// 屏上只有一张海报,重建代价是一次封面加载。</summary>
    private void RebuildHero()
    {
        var pool = SourceIndex switch
        {
            1 => _heroes.Where(hero => hero.Source == MusicSource.NetEase).ToList(),
            2 => _heroes.Where(hero => hero.Source == MusicSource.QQ).ToList(),
            _ => (IReadOnlyList<BrowseHeroViewModel>)_heroes,
        };

        // 先清空再填:列表清空时 ListBox 会把 SelectedIndex = -1 写回来,直接赋 0 可能因为
        // "值没变"而不发通知,圆点就停在未选中态。整套在同一帧里完成,不会看到闪一下。
        Hero = null;
        HeroIndex = -1;
        HeroItems.Clear();
        foreach (var hero in pool) HeroItems.Add(hero);
        if (HeroItems.Count > 0)
        {
            HeroIndex = 0;
            Hero = HeroItems[0];
        }
    }

    // ---------- 卡片工厂 ----------

    private BrowseCardViewModel ToToplistCard(ToplistItem item)
    {
        var playlist = NewToplistPlaylist(item);
        return new BrowseCardViewModel(item.Name, item.Summary, item.CoverUrl, item.Source,
            item.PlayCount, () => OpenPlaylistAsync(playlist), playlist: playlist);
    }

    private BrowseHeroViewModel ToHero(ToplistItem item) => item.Source == MusicSource.QQ
        ? new BrowseHeroViewModel(item, () => OpenQqToplistAsync(item))
        : new BrowseHeroViewModel(item, () => OpenNetEasePlaylistAsync(item.Id, item.Name, item.CoverUrl, item.TrackCount, item.PlayCount));

    private BrowseCardViewModel ToPlaylistCard(RecommendItem item)
    {
        // 副标题用曲数:播放量已经占了角标,再写一遍「6万播放」只是把同一件事说两次。
        // QQ 歌单拿不到曲目数(推荐流/广场都只有播放量),那里退回上一层的文案(推荐理由 / 创建者)。
        var subtitle = item.TrackCount > 0 ? $"{item.TrackCount} 首" : item.Subtitle;
        var playlist = item.Source == MusicSource.QQ
            ? NewQqPlaylist(item.Id, item.Title, item.CoverUrl, item.PlayCount)
            : NewNetEasePlaylist(item.Id, item.Title, item.CoverUrl, item.TrackCount, item.PlayCount,
                item.CreatorName, item.CreatorId);
        return new BrowseCardViewModel(item.Title, subtitle, item.CoverUrl, item.Source, item.PlayCount,
            () => OpenPlaylistAsync(playlist), playlist: playlist);
    }

    private BrowseCardViewModel ToSongCard(Song song, IReadOnlyList<Song> queue)
        => new(song.Name, song.Artist, song.CoverUrl, song.Source, 0,
            () => _player.PlayFromList(song, queue, "浏览"));

    // ---------- 打开 ----------

    /// <summary>歌单页的统一入口(卡片点击、▶ 播放、more 菜单都拿同一份 VM)。</summary>
    private Task OpenPlaylistAsync(PlaylistItemViewModel playlist)
    {
        ServiceLocator.Get<MainViewModel>().OpenShellPlaylistAuto(playlist);
        return Task.CompletedTask;
    }

    private Task OpenNetEasePlaylistAsync(long id, string name, string cover, int trackCount, long playCount)
        => OpenPlaylistAsync(NewNetEasePlaylist(id, name, cover, trackCount, playCount));

    /// <summary>QQ 普通歌单(推荐歌单):TopId 留 0,歌单页走 uniform_get_Dissinfo 取曲目。</summary>
    private Task OpenQqPlaylistAsync(long dissId, string name, string cover, long playCount)
        => OpenPlaylistAsync(NewQqPlaylist(dissId, name, cover, playCount));

    /// <summary>QQ 榜单不是歌单:Id 只当缓存键,TopId 决定歌单页从榜单接口取曲目。</summary>
    private Task OpenQqToplistAsync(ToplistItem item) => OpenPlaylistAsync(NewToplistPlaylist(item));

    private static PlaylistItemViewModel NewNetEasePlaylist(long id, string name, string cover, int trackCount,
        long playCount, string creatorName = "", long creatorId = 0)
        => new(new Playlist
        {
            Id = id,
            Name = name,
            CoverUrl = cover,
            TrackCount = trackCount,
            PlayCount = playCount,
            Source = MusicSource.NetEase,
            CreatorName = creatorName,
        })
        {
            CreatorName = creatorName,
            CreatorId = creatorId,
        };

    private static PlaylistItemViewModel NewQqPlaylist(long dissId, string name, string cover, long playCount)
        => new(new Playlist
        {
            Id = dissId,
            Name = name,
            CoverUrl = cover,
            PlayCount = playCount,
            Source = MusicSource.QQ,
            CreatorName = "QQ音乐",
        })
        {
            CreatorName = "QQ音乐",
        };

    private static PlaylistItemViewModel NewToplistPlaylist(ToplistItem item)
        => item.Source == MusicSource.QQ
            ? new PlaylistItemViewModel(new Playlist
            {
                Id = item.Id,
                TopId = (int)item.Id,
                Name = item.Name,
                CoverUrl = item.CoverUrl,
                TrackCount = item.TrackCount,
                PlayCount = item.PlayCount,
                Source = MusicSource.QQ,
                CreatorName = "QQ音乐",
            })
            {
                CreatorName = "QQ音乐",
            }
            : NewNetEasePlaylist(item.Id, item.Name, item.CoverUrl, item.TrackCount, item.PlayCount);

    // ---------- 工具 ----------

    private static List<T> Interleave<T>(IReadOnlyList<T> first, IReadOnlyList<T> second)
    {
        var result = new List<T>(first.Count + second.Count);
        for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) result.Add(first[i]);
            if (i < second.Count) result.Add(second[i]);
        }
        return result;
    }

    private static async Task<ListResult<T>> SafeAsync<T>(Task<List<T>> request)
    {
        try
        {
            return new ListResult<T>(await request.ConfigureAwait(false), false);
        }
        catch
        {
            return new ListResult<T>([], true);
        }
    }

    private readonly record struct ListResult<T>(IReadOnlyList<T> Items, bool Failed);
}
