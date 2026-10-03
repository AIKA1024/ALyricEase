using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ALyricEase.ViewModels;

/// <summary>搜索结果单行:展示歌曲信息 + 双击播放。封面/红心状态后台加载。
/// 可播性预判:VIP 歌曲在未登录/已确认非会员时直接禁用整行(播放前可知);
/// 版权/区域等只有播放时才知道的,播放失败后补标禁用。</summary>
public sealed partial class SongItemViewModel : ViewModelBase
{
    private Func<Song, IReadOnlyList<Song>?, string?, Task<bool>> _playSong;
    private IReadOnlyList<Song>? _queue;
    private string? _source;
    private readonly NetEaseApiClient? _api;

    private readonly HashSet<MusicSource> _likedRequested = new();
    private readonly Dictionary<MusicSource, int> _likedGenerations = new();
    private readonly Dictionary<MusicSource, bool> _likedStates = new();
    private readonly HashSet<MusicSource> _likeOperations = new();

    /// <summary>红心按钮与跳转是按音源路由的账号能力;红心两音源均支持,歌手/专辑跳转仍仅网易云为纯本地判定。</summary>
    private bool IsNetEase => Song.Source == Services.MusicSource.NetEase;

    /// <summary>按曲目音源解析红心能力客户端:网易云直连构造注入实例;其他音源经服务定位器取
    /// MusicApiProvider 路由(与 OpenArtistAsync 的用法一致)。宿主未初始化/未注册该音源时返回 null,
    /// 红心按钮静默降级为无操作(SelfTest/Headless 等无宿主环境安全)。</summary>
    private IUserMusicApi? GetLikeApi() => GetLikeApi(Song.Source);

    private IUserMusicApi? GetLikeApi(MusicSource source)
    {
        if (source == MusicSource.NetEase && _api is not null) return _api;
        try { return ServiceLocator.Get<MusicApiProvider>().User(source); }
        catch { return null; }
    }

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

    public SongItemViewModel(Song song, Func<Song, Task<bool>> playSong, int index = 0, NetEaseApiClient? api = null)
        : this(song, (s, _, _) => playSong(s), index, api: api) { }

    public SongItemViewModel(Song song, Func<Song, IReadOnlyList<Song>?, string?, Task<bool>> playSong, int index = 0, IReadOnlyList<Song>? queue = null, NetEaseApiClient? api = null, string? source = null)
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
        // 可播性预判:播放前即可确定的(未登录/已确认非会员的 VIP 歌曲)直接禁用整行
        RefreshPlayability();
        // 封面由视图的租约式加载器按可见性拉取，VM 不再长期持有 Bitmap。
    }

    /// <summary>整行是否可播放(不可播时行禁用置灰)。预判 + 播放实测两路更新。</summary>
    [ObservableProperty]
    private bool _isPlayable = true;

    /// <summary>就地换播放绑定:把"离线缓存行"升级为"在线行"(歌单页网络刷新时用)。
    ///
    /// ⚠ 为什么必须是"就地换"而不是"重建一行":歌单页打开时是两遍装载 ——
    /// 先渲染该歌单的磁盘缓存(封面立刻有图),网络结果回来后再换在线数据。
    /// 实测(2026-09-20,--pl-cover-flash,同进程三档对照)**清空重建与"逐位替换集合元素"
    /// 都会让 ItemsControl 把行控件整批换掉**(Avalonia 的 Replace 通知同样走容器回收):
    /// 容器复用/重建时 DataContext 先被清成 null,行模板里的
    /// <c>infra:ManagedCoverImage.Source</c> 收到 null 就把已解码的那张图丢掉,
    /// 新 DataContext 落下来时再走一遍**异步** SetSource,中间那几帧露出行模板底下的
    /// ArtFallback 占位色 —— 用户看到的就是"图片又加载了一遍"。
    /// 只有"集合元素与行对象都不动、只换行内部的播放绑定"才能保住那张图。
    ///
    /// Song 元数据不变(同一首歌的同一份元数据),所以展示属性无需通知。</summary>
    public void RebindPlayback(
        Func<Song, IReadOnlyList<Song>?, string?, Task<bool>> playSong,
        IReadOnlyList<Song>? queue,
        string? source)
    {
        _playSong = playSong;
        _queue = queue;
        _source = source;
        // 离线快照给这条曲目打的"跳过在线音质升级"标记,到在线为止。
        Song.PreferCachedPlayback = false;
        // 离线时按"有没有本地音频"禁用的行,在线后要按登录/会员重新判定,否则会一直灰着。
        RefreshPlayability();
    }

    /// <summary>按当前登录/会员状态重算可播性(数据驱动,播放前即可确定的部分):
    /// 免费歌恒可点；VIP 歌曲在未登录、或会员状态已确认且非会员时禁用。
    /// 会员状态未加载(IsVipLoaded=false)时不下结论保持可点，由播放实测兜底。
    /// 登录态变化后可重调(行集合重建时 ctor 已自动跑一次)。</summary>
    public void RefreshPlayability()
    {
        IsPlayable = PlaybackAvailability.CanAttempt(Song, GetLikeApi());
    }

    /// <summary>歌手子菜单项(多歌手时用)。</summary>
    public IReadOnlyList<ArtistNavItem> Artists { get; }

    public bool HasMultipleArtists => Artists.Count > 1;

    /// <summary>容器 realized 时调用:首次才拉取红心状态(幂等)。未登录/服务未就绪则保持未喜欢。</summary>
    public void EnsureLikedLoaded()
    {
        foreach (var source in CombinedLikeTargetResolver.GetSources(Song))
        {
            var api = GetLikeApi(source);
            if (api is null) continue;
            // 幂等:同一登录身份只取一次;代次变化(换号/登出/刷新凭证)时允许重取。
            if (_likedRequested.Contains(source)
                && _likedGenerations.TryGetValue(source, out var generation)
                && generation == api.AccountGeneration)
                continue;
            _likedRequested.Add(source);
            _ = LoadLikedAsync(source, api);
        }
    }

    public Song Song { get; }

    /// <summary>队列来源名(歌单名/歌手名等,构造时传入):歌曲行右键菜单"来源"项展示用;未传则按音源显示平台名。</summary>
    public string? SourceName => _source;

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

    /// <summary>有可跳转的歌手:网易云按数字 id,QQ 按 singer mid。
    /// 须排除 id=0 的占位项——云盘无版权歌(服务端抹掉 ar[].id)只有名字,Count>0 但全是 0,不可跳。</summary>
    public bool HasArtist => (IsNetEase && Song.ArtistIds.Any(id => id != 0)) || Song.ArtistMids.Count > 0;

    /// <summary>有歌手名(展示用):与 HasArtist(可跳转)区分——云盘等无版权歌曲有名字无 id。</summary>
    public bool HasArtistName => !string.IsNullOrEmpty(Song.Artist);

    /// <summary>有可跳转的专辑:网易云按数字 id,QQ 按 album mid。</summary>
    public bool HasAlbum => (IsNetEase && Song.AlbumId != 0) || Song.AlbumMid.Length > 0;

    /// <summary>有专辑名(展示用):与 HasAlbum(可跳转)区分——同上。</summary>
    public bool HasAlbumName => !string.IsNullOrEmpty(Song.Album);

    /// <summary>歌手或专辑至少有名(每日行歌名下组合链接按钮的显示条件:两者皆无则整个隐藏)。</summary>
    public bool HasArtistOrAlbumName => HasArtistName || HasAlbumName;

    /// <summary>歌手或专辑至少一个可跳转;都不可跳(如云盘无版权歌)时合并链接按钮整体禁用。</summary>
    public bool HasArtistOrAlbum => HasArtist || HasAlbum;

    /// <summary>歌单内序号(1 起);非歌单场景为 0。</summary>
    public int Index { get; private set; }

    /// <summary>来源列表(自己的歌单)删歌后行序号前移:原地改号并通知,行容器与行对象都不重建。</summary>
    public void Renumber(int newIndex)
    {
        if (Index == newIndex) return;
        Index = newIndex;
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(DisplayIndex));
    }

    /// <summary>宿主页面注入的"从当前来源移除该曲"命令(仅自己的歌单,歌曲行菜单据此显隐);
    /// null = 来源不可移除(他人歌单/云盘/聚合/歌手页等)。参数为本行 VM。</summary>
    public ICommand? RemoveFromSourceCommand { get; set; }

    public string DisplayIndex => Index > 0 ? Index.ToString() : "";

    public string DurationText => FormatDuration(Song.DurationMs);

    /// <summary>VIP/付费标记,UI 用。</summary>
    public bool IsVip => Song.Fee != 0;

    /// <summary>是否已红心(在"我喜欢的音乐"里)。</summary>
    [ObservableProperty]
    private bool _isInLikelist;

    /// <summary>红心提示。综合结果会明确默认写入的平台，右键/长按可切换。</summary>
    public string LikeToolTip
    {
        get
        {
            if (!HasMultipleLikeSources)
                return IsInLikelist ? $"已添加到{SourceDisplayName(Song.Source)}我喜欢" : "喜欢";
            var preferred = GetPreferredCombinedLikeSource();
            return preferred is { } source && GetRecording(source) is not null
                ? $"喜欢（默认：{SourceDisplayName(source)}；右键或长按可更改）"
                : "喜欢（点击选择平台）";
        }
    }

    /// <summary>合并结果是否同时含多个平台录音。</summary>
    public bool HasMultipleLikeSources => CombinedLikeTargetResolver.GetSources(Song).Count > 1;

    public MusicSource? PreferredCombinedLikeSource => GetPreferredCombinedLikeSource();

    /// <summary>合并结果未设置默认平台时，首次点击必须由用户明确选择。</summary>
    public bool NeedsLikeSourceChoice()
        => HasMultipleLikeSources
           && (PreferredCombinedLikeSource is not { } source || !HasLikeRecording(source));

    public bool HasLikeRecording(MusicSource source) => GetRecording(source) is not null;

    public bool CanToggleLikeOn(MusicSource source)
        => GetRecording(source) is not null && GetLikeApi(source)?.CanToggleLike == true;

    public bool IsLikedOn(MusicSource source)
        => _likedStates.TryGetValue(source, out var liked) && liked;

    [RelayCommand]
    private async Task PlayAsync()
    {
        var ok = await _playSong(Song, _queue, _source);
        // 播放实测不可播(版权/区域/播放前预判漏网的):该曲整行禁用
        if (!ok) IsPlayable = false;
    }

    /// <summary>切换红心:乐观更新,失败回滚。按音源路由到对应平台的"喜欢"能力;
    /// 未登录时引导弹登录弹层(无宿主环境静默忽略)。</summary>
    [RelayCommand]
    private async Task LikeAsync()
    {
        // 守卫放在命令层，确保鼠标、键盘及其他命令入口都遵守首次明确选择平台的规则。
        if (NeedsLikeSourceChoice())
        {
            TryOpenLikeSourceDialog();
            return;
        }

        var source = ResolveLikeTarget() ?? Song.Source;
        await ToggleLikeOnSourceAsync(source);
    }

    /// <summary>在指定平台切换“我喜欢”。默认平台由选择对话框的独立确认动作管理。</summary>
    public async Task ToggleLikeOnSourceAsync(MusicSource source)
    {
        var recording = GetRecording(source);
        var api = GetLikeApi(source);
        if (recording is null || api is null || !_likeOperations.Add(source)) return;

        if (!api.CanToggleLike)
        {
            _likeOperations.Remove(source);
            TryOpenLoginDialog(source, null);
            return;
        }

        var previous = IsLikedOn(source);
        SetLikedState(source, !previous); // 乐观更新,立即反馈
        try
        {
            SetLikedState(source, await api.LikeToggleAsync(recording.Id));
            _likedGenerations[source] = api.AccountGeneration;
        }
        catch (ApiException ex) when (ShouldPromptRelogin(api, ex))
        {
            SetLikedState(source, previous);
            TryOpenLoginDialog(source, QQMusicApiClient.ReloginHintText);
        }
        catch
        {
            SetLikedState(source, previous);
        }
        finally
        {
            _likeOperations.Remove(source);
        }
    }

    public void SetPreferredCombinedLikeSource(MusicSource source)
    {
        if (!HasLikeRecording(source)) return;
        try { ServiceLocator.Get<AppStateStore>().SetPreferredCombinedLikeSource(source); }
        catch { /* 无宿主环境 */ }
        RefreshLikeTarget();
    }

    /// <summary>全局默认平台变化后，由已实现的歌曲行刷新当前红心代表的来源。</summary>
    public void RefreshLikeTarget()
    {
        UpdateDisplayedLikeState();
        OnPropertyChanged(nameof(LikeToolTip));
    }

    /// <summary>红心写被拒时的重登判定:QQ 按服务端错误码(常规失效码 + 写通道 80105 归并);
    /// 网易云暂无等价失效码契约,维持静默回滚。</summary>
    private static bool ShouldPromptRelogin(IUserMusicApi api, ApiException ex)
        => api is QQMusicApiClient qq && QQMusicApiClient.ShouldPromptRelogin(ex.Code);

    /// <summary>弹登录窗并定位到本曲音源标签;无宿主环境(SelfTest/Headless)静默忽略。</summary>
    private void TryOpenLoginDialog(MusicSource source, string? hint)
    {
        try { ServiceLocator.Get<MainViewModel>().OpenLoginDialogFor(source, hint); }
        catch { /* 无宿主环境 */ }
    }

    private void TryOpenLikeSourceDialog()
    {
        try { ServiceLocator.Get<MainViewModel>().OpenLikeSourceDialog(this); }
        catch { /* 无宿主环境 */ }
    }

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

    private async Task LoadLikedAsync(MusicSource source, IUserMusicApi api)
    {
        var generation = api.AccountGeneration;
        try
        {
            await api.EnsureLikedIdsAsync();
            _likedGenerations[source] = generation;
            var recording = GetRecording(source);
            SetLikedState(source, recording is not null && api.IsLiked(recording.Id));
        }
        catch
        {
            // 保持未喜欢
        }
    }

    private Song? GetRecording(MusicSource source)
        => SongRecordingResolver.Resolve(Song, source);

    private MusicSource? GetPreferredCombinedLikeSource()
    {
        try { return ServiceLocator.Get<AppStateStore>().PreferredCombinedLikeSource; }
        catch { return null; }
    }

    private MusicSource? ResolveLikeTarget()
        => CombinedLikeTargetResolver.Resolve(
            Song,
            GetPreferredCombinedLikeSource());

    private void SetLikedState(MusicSource source, bool liked)
    {
        _likedStates[source] = liked;
        UpdateDisplayedLikeState();
        OnPropertyChanged(nameof(LikeToolTip));
    }

    private void UpdateDisplayedLikeState()
    {
        var displaySource = ResolveLikeTarget() ?? Song.Source;
        IsInLikelist = IsLikedOn(displaySource);
    }

    public static string SourceDisplayName(MusicSource source)
        => source == MusicSource.QQ ? "QQ音乐" : "网易云音乐";

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
