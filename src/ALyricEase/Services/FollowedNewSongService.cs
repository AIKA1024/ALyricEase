using System.Globalization;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Services;

/// <summary>关注新歌货架上的一张卡：一首单曲，或同一张专辑里的多首新歌合成的**一张专辑卡**。
/// 一张专辑连发十首会把整条货架占满，所以同专辑只留一格。</summary>
public sealed record FollowedNewSongItem(IReadOnlyList<Song> Songs, string Album)
{
    /// <summary>卡片的代表曲目：组内最新那首（标题/封面/日期都取它）。</summary>
    public Song Lead => Songs[0];

    public bool IsAlbum => Songs.Count > 1;

    /// <summary>专辑全量曲目（点 ▶ 播整张）。拉取失败时为空，界面回落到 <see cref="Songs"/>。</summary>
    public IReadOnlyList<Song> FullSongs { get; init; } = [];

    /// <summary>专辑真实曲数：拿到全量曲目就用它，否则退回采样数。</summary>
    public int TotalCount => FullSongs.Count > 0 ? FullSongs.Count : Songs.Count;

    /// <summary>是否拿到了整张专辑 —— 拿不到时卡片只能写「N 首新歌」，
    /// 不能把采样数冒充成专辑曲数（用户看到「BELOVED 4 首」当场就问过来了）。</summary>
    public bool HasFullAlbum => FullSongs.Count > 0;

    /// <summary>点 ▶ 时真正入队的曲目：整张专辑优先，否则这批采样。</summary>
    public IReadOnlyList<Song> PlaybackSongs => HasFullAlbum ? FullSongs : Songs;
}

/// <summary>关注歌手新歌的汇总结果。<see cref="Items"/> 是货架卡片（同专辑已合并），
/// <see cref="Songs"/> 是展平后的播放队列（按发行日期倒序去重）。</summary>
public sealed record FollowedNewSongResult(
    IReadOnlyList<FollowedNewSongItem> Items,
    IReadOnlyList<Song> Songs,
    int NetEaseArtists,
    int QqArtists,
    bool NetEaseFailed,
    bool QqFailed)
{
    public static readonly FollowedNewSongResult Empty = new([], [], 0, 0, false, false);

    /// <summary>至少有一侧真的拿到了歌手名单,才能说"这段时间他们没发歌"。</summary>
    public bool HasArtists => NetEaseArtists > 0 || QqArtists > 0;
}

/// <summary>
/// 关注歌手最近三个月的新歌 —— 浏览页「关注新歌」区块的数据源。
///
/// <para>
/// 两个平台都**没有**"关注歌手新歌"的聚合接口(docs/netease-api/02-endpoints.md 有实测记录),
/// 只能"关注列表 + 每位歌手按发行时间取最新"逐位拼:本机实测 17(网易云)+ 32(QQ)位,
/// 也就是近 50 次请求。所以这一层必须做三件事,否则每次进浏览页都要等十几秒:
/// ① 30 分钟内存缓存;② 单飞(并发进入只跑一次);③ 每位歌手的请求走 6 并发闸门。
/// </para>
///
/// <para>
/// 全站"新歌速递"不能拿来过滤关注歌手(实测网易云 100 首命中 0 首),所以别退化成那个方案。
/// </para>
/// </summary>
public sealed class FollowedNewSongService
{
    /// <summary>时间窗口:最近 3 个月(用户口径)。</summary>
    public const int LookbackMonths = 3;

    /// <summary>每位歌手只取最近几首:同一张专辑常有多首单曲,4 首足够覆盖窗口,
    /// 再多只是把请求结果丢掉。</summary>
    private const int PerArtist = 4;

    private const int MaxConcurrency = 6;

    /// <summary>两个货架各自最多展示多少张卡（「关注新歌」是单曲卡，「关注新专」是专辑卡）:
    /// 再多也没人横向滚到底,还拖慢封面解码。分开算 —— 单曲多的日子不能把专辑整块挤掉。</summary>
    private const int MaxSingles = 24;

    private const int MaxAlbums = 24;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private readonly NetEaseApiClient _ne;
    private readonly QQMusicApiClient _qq;
    private readonly SemaphoreSlim _artistGate = new(MaxConcurrency, MaxConcurrency);
    private readonly object _sync = new();

    private FollowedNewSongResult? _cachedResult;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private Task<FollowedNewSongResult>? _inflight;
    private int _generation;

    public FollowedNewSongService(NetEaseApiClient ne, QQMusicApiClient qq)
    {
        _ne = ne;
        _qq = qq;
    }

    /// <summary>任一平台已登录才有内容;都没登录时区块整块隐藏(一次请求都不发)。</summary>
    public bool Enabled => _ne.IsLoggedIn || _qq.IsLoggedIn;

    /// <summary>取汇总结果。<paramref name="force"/> = 用户点刷新,跳过缓存但仍走单飞。</summary>
    public Task<FollowedNewSongResult> GetAsync(bool force, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!force && _cachedResult is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return Task.FromResult(_cachedResult);
            if (_inflight is { IsCompleted: false }) return _inflight;

            var generation = ++_generation;
            _inflight = RunAsync(generation, ct);
            return _inflight;
        }
    }

    private async Task<FollowedNewSongResult> RunAsync(int generation, CancellationToken ct)
    {
        try
        {
            var cutoff = DateOnly.FromDateTime(DateTime.Today).AddMonths(-LookbackMonths);
            var neTask = LoadNetEaseAsync(cutoff, ct);
            var qqTask = LoadQqAsync(cutoff, ct);
            await Task.WhenAll(neTask, qqTask).ConfigureAwait(false);

            var (neSongs, neArtists, neFailed) = await neTask.ConfigureAwait(false);
            var (qqSongs, qqArtists, qqFailed) = await qqTask.ConfigureAwait(false);

            var merged = neSongs.Concat(qqSongs)
                // 合唱两边各出现一次 → 按 (源, 曲目 id) 去重;
                // 同一首歌在同一专辑里可能下发多条(QQ 实测有),再按 歌名+歌手+日期 兜一层。
                .GroupBy(song => (song.Source, song.Id))
                .Select(group => group.First())
                .GroupBy(song => (song.Source, song.Name, song.Artist, song.PublishDate))
                .Select(group => group.First())
                .OrderByDescending(song => ReleaseDate(song) ?? DateOnly.MinValue)
                .ToList();

            // 同一张专辑的多首新歌只占一格:先合并再截断,否则一张专辑连发十几首
            // 会把 24 个格子全吃掉,别的歌手一首也看不见。
            // 截断按两类各算上限,再合回一个按日期倒序的清单(界面自己按 IsAlbum 分流)。
            var grouped = GroupByAlbum(merged);
            var items = grouped.Where(item => !item.IsAlbum).Take(MaxSingles)
                .Concat(grouped.Where(item => item.IsAlbum).Take(MaxAlbums))
                .OrderByDescending(item => ReleaseDate(item.Lead) ?? DateOnly.MinValue)
                .ToList();

            // 卡片代表"一张专辑",副标题要写专辑真实曲数(采样只有 4 首时尤其明显)、▶ 要能播整张。
            // 只补**截断后**还在货架上的专辑(16 张左右),不补命中集合里的每一张。
            items = await FillAlbumSongsAsync(items, ct).ConfigureAwait(false);

            var songs = items.SelectMany(item => item.Songs)
                .OrderByDescending(song => ReleaseDate(song) ?? DateOnly.MinValue)
                .ToList();

            var result = new FollowedNewSongResult(items, songs, neArtists, qqArtists, neFailed, qqFailed);

            // 两侧都失败时不要把"空"写进缓存:下次进页面还会重试。
            if (!(neFailed && qqFailed))
            {
                lock (_sync)
                {
                    _cachedResult = result;
                    _cachedAt = DateTimeOffset.UtcNow;
                }
            }

            return result;
        }
        finally
        {
            lock (_sync)
            {
                if (generation == _generation) _inflight = null;
            }
        }
    }

    private async Task<(List<Song> Songs, int Artists, bool Failed)> LoadNetEaseAsync(
        DateOnly cutoff, CancellationToken ct)
    {
        if (!_ne.IsLoggedIn) return ([], 0, false);
        try
        {
            var artists = await _ne.GetArtistSublistAsync(200, 0, ct).ConfigureAwait(false);
            var songs = await FetchPerArtistAsync(
                artists.Select(artist => (Func<CancellationToken, Task<IReadOnlyList<Song>>>)(
                    token => FetchNetEaseArtistAsync(artist.Id, token))).ToList(), cutoff, ct)
                .ConfigureAwait(false);
            return (songs, artists.Count, false);
        }
        catch (OperationCanceledException)
        {
            return ([], 0, false);
        }
        catch
        {
            return ([], 0, true);
        }
    }

    private async Task<(List<Song> Songs, int Artists, bool Failed)> LoadQqAsync(
        DateOnly cutoff, CancellationToken ct)
    {
        if (!_qq.IsLoggedIn) return ([], 0, false);
        try
        {
            var page = await _qq.GetFollowSingersAsync("", 0, 200, ct).ConfigureAwait(false);
            var singers = page.Users.Where(user => user.SingerMid.Length > 0).ToList();
            var songs = await FetchPerArtistAsync(
                singers.Select(singer => (Func<CancellationToken, Task<IReadOnlyList<Song>>>)(
                    async token => await _qq.GetArtistSongsAsync(singer.SingerMid, PerArtist, token, order: 2)
                        .ConfigureAwait(false))).ToList(),
                cutoff, ct).ConfigureAwait(false);
            return (songs, singers.Count, false);
        }
        catch (OperationCanceledException)
        {
            return ([], 0, false);
        }
        catch
        {
            return ([], 0, true);
        }
    }

    private async Task<IReadOnlyList<Song>> FetchNetEaseArtistAsync(long artistId, CancellationToken ct)
    {
        var (songs, _, _) = await _ne.GetArtistSongPageAsync(artistId, PerArtist, 0, ct, "time")
            .ConfigureAwait(false);
        return songs;
    }

    /// <summary>补齐专辑卡的全量曲目：QQ 按 albumMid、网易云按 album id 各拉一次，复用 6 并发闸门。
    /// 失败只丢那一张（退回采样曲目），不影响整块；只补截断后仍在货架上的专辑。</summary>
    private async Task<List<FollowedNewSongItem>> FillAlbumSongsAsync(
        List<FollowedNewSongItem> items, CancellationToken ct)
    {
        var tasks = items.Select(async item =>
        {
            if (!item.IsAlbum) return item;
            await _artistGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var full = await LoadAlbumSongsAsync(item.Lead, ct).ConfigureAwait(false);
                return full.Count > 0 ? item with { FullSongs = full } : item;
            }
            catch (OperationCanceledException)
            {
                return item;
            }
            catch
            {
                return item;
            }
            finally
            {
                _artistGate.Release();
            }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    private async Task<List<Song>> LoadAlbumSongsAsync(Song lead, CancellationToken ct)
    {
        if (lead.Source == MusicSource.QQ)
        {
            if (lead.AlbumMid.Length == 0) return [];
            return await _qq.GetAlbumSongsByMidAsync(lead.AlbumMid, ct: ct).ConfigureAwait(false);
        }
        if (lead.AlbumId == 0) return [];
        var detail = await _ne.GetAlbumAsync(lead.AlbumId, ct).ConfigureAwait(false);
        return detail.Songs.ToList();
    }

    /// <summary>按 6 并发跑完所有歌手,单点失败只丢那一位(独立歌手的接口偶尔 404)。</summary>
    private async Task<List<Song>> FetchPerArtistAsync(
        List<Func<CancellationToken, Task<IReadOnlyList<Song>>>> requests, DateOnly cutoff,
        CancellationToken ct)
    {
        var tasks = requests.Select(async request =>
        {
            await _artistGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await request(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (IReadOnlyList<Song>)[];
            }
            catch
            {
                return (IReadOnlyList<Song>)[];
            }
            finally
            {
                _artistGate.Release();
            }
        });

        var pages = await Task.WhenAll(tasks).ConfigureAwait(false);
        return pages.SelectMany(page => page)
            .Where(song => InWindow(song, cutoff))
            .ToList();
    }

    /// <summary>窗口判断依赖发行日期文本;没有日期的条目直接丢掉 —— 排序和"是不是新歌"都无从谈起。</summary>
    private static bool InWindow(Song song, DateOnly cutoff)
    {
        if (ReleaseDate(song) is not { } date) return false;
        var today = DateOnly.FromDateTime(DateTime.Today);
        return date >= cutoff && date <= today.AddDays(1);
    }

    /// <summary>
    /// 把同一张专辑的多首新歌并成一格。输入已按发行日期倒序，所以组的先后天然就是
    /// "组内最新那首"的先后，不需要二次排序；组内也保持倒序，<see cref="FollowedNewSongItem.Lead"/>
    /// 就是这张专辑最新的一首。
    ///
    /// <para>专辑键优先用 id：网易云 / QQ 的 id 空间不互通，必须带源；QQ 偶尔 albumId 为 0，
    /// 再退回 albumMid。两者都没有时才拼 专辑名+歌手+日期 —— 「精选」这类同名专辑不少，
    /// 光靠名字会把不同专辑并掉。</para>
    /// </summary>
    private static List<FollowedNewSongItem> GroupByAlbum(IReadOnlyList<Song> songs)
    {
        var buckets = new List<List<Song>>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var song in songs)
        {
            var key = AlbumKey(song);
            if (key.Length > 0 && byKey.TryGetValue(key, out var index))
            {
                buckets[index].Add(song);
                continue;
            }
            if (key.Length > 0) byKey[key] = buckets.Count;
            buckets.Add([song]);
        }
        return buckets.Select(bucket => new FollowedNewSongItem(bucket, bucket[0].Album)).ToList();
    }

    private static string AlbumKey(Song song)
    {
        if (song.Album.Length == 0) return "";
        var source = (int)song.Source;
        if (song.AlbumId != 0) return $"{source}|id|{song.AlbumId}";
        if (song.AlbumMid.Length > 0) return $"{source}|mid|{song.AlbumMid}";
        return $"{source}|name|{song.Album}\u0001{song.Artist}\u0001{song.PublishDate}";
    }

    private static DateOnly? ReleaseDate(Song song)
        => DateOnly.TryParseExact(song.PublishDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
}
