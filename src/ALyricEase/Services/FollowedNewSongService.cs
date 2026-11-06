using System.Globalization;
using ALyricEase.Models;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Services;

/// <summary>关注歌手新歌的汇总结果。<see cref="Songs"/> 已按发行日期倒序去重。</summary>
public sealed record FollowedNewSongResult(
    IReadOnlyList<Song> Songs,
    int NetEaseArtists,
    int QqArtists,
    bool NetEaseFailed,
    bool QqFailed)
{
    public static readonly FollowedNewSongResult Empty = new([], 0, 0, false, false);

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

    /// <summary>货架最多展示多少张:再多也没人横向滚到底,还拖慢封面解码。</summary>
    private const int MaxItems = 24;

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
                .Take(MaxItems)
                .ToList();

            var result = new FollowedNewSongResult(merged, neArtists, qqArtists, neFailed, qqFailed);

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

    private static DateOnly? ReleaseDate(Song song)
        => DateOnly.TryParseExact(song.PublishDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
}
