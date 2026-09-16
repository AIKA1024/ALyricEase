using System.Diagnostics;
using System.Net;
using ALyricEase.Models;
using ALyricEase.Services;

namespace ALyricEase.Headless;

/// <summary>
/// 缓存**读**路径是否被 `_mutationGate` 堵住的实测。
///
/// 背景:该 gate 由写入/删除/裁剪共享,而容量裁剪要 stat + 排序整个缓存目录
/// (真实用户上万文件,实测单次空跑就是 550ms 级),每次封面落盘的 `EnsureSpaceFor`
/// 也要扫同一遍目录。读路径若也排队在这把锁上,表现就是**偶发卡顿** ——
/// 卡不卡取决于当时有没有写/裁剪撞上来,所以"偶发"。
/// 生产里最典型的一处:歌单页深层返回(内存快照被淘汰)回退常规加载,要读
/// `p-*.tracks` 与扫一遍 `a-*`,两个读都在 gate 上等。
///
/// 判据:一次持续 ≥ <see cref="MinimumMutationWindowMs"/> 的裁剪进行中,
/// 每个读的耗时都必须仍在 <see cref="BusyReadBudgetMs"/> 以内(即"没在排队")。
/// 改动前这里会精确测到≈剩余裁剪时长。
///
/// 开关:`ALY_CACHE_FILES`(填充文件数,默认 4000,决定扫描成本)、
/// `ALY_GATE_ROUNDS`(轮数,默认 3)、`ALY_PROBE_LOG`(同时追加到文件)。
/// </summary>
internal static class CacheReadGateProbe
{
    /// <summary>裁剪进行中,单次读的耗时预算。设得很松:只要"没在等锁",读本身是毫秒级。</summary>
    private const int BusyReadBudgetMs = 50;

    /// <summary>裁剪窗口小于这个值就没法判定(锁释放太快),只报告不断言。</summary>
    private const int MinimumMutationWindowMs = 150;

    public static async Task<int> RunAsync()
    {
        var fillerCount = EnvInt("ALY_CACHE_FILES", 4000);
        var rounds = EnvInt("ALY_GATE_ROUNDS", 3);
        var root = Path.Combine(Path.GetTempPath(), $"alyric-cachegate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient(new StubHandler());
            var cache = new MusicCacheService(1024, root, http);

            // 五种读形态,全部走公开 API 准备数据(不依赖内部文件命名规则)。
            const string coverUrl = "https://example.test/gate.jpg?size=300";
            var coverBytes = new byte[] { 1, 3, 5, 7, 9, 11, 13, 15 };
            var lyric = new LyricResult { Original = "[00:01]原文", Translation = "[00:01]译文" };
            var audioSong = new Song { Id = 4242, Source = MusicSource.NetEase, Name = "读锁探针" };
            var playlist = new Playlist
            {
                Id = 4242,
                Source = MusicSource.NetEase,
                Name = "读锁探针歌单",
                TrackCount = 32,
            };
            var tracks = Enumerable.Range(1, 32).Select(index => new Song
            {
                Id = 424_200 + index,
                Source = MusicSource.NetEase,
                Name = $"读锁探针歌曲 {index:D2}",
                Artist = "读锁探针歌手",
                Album = "读锁探针专辑",
                DurationMs = 180_000,
                ArtistIds = [61],
                ArtistNames = ["读锁探针歌手"],
                AlbumId = 71,
            }).ToList();

            await cache.CacheCoverAsync(coverUrl, coverBytes);
            await cache.CacheLyricAsync(audioSong, lyric);
            await cache.CachePlaylistListAsync(MusicSource.NetEase, "读锁探针用户", [playlist]);
            await cache.CachePlaylistTracksAsync(playlist, tracks);
            await cache.CacheAsync(audioSong, "standard", "https://example.test/gate.mp3");

            // 每次调用都既计时又做内容断言 —— 不能为了量延迟放过正确性。
            (string Name, Func<Task<bool>> Action)[] reads =
            [
                ("封面字节(读盘)", async () =>
                    await cache.TryGetCoverAsync(coverUrl) is { } bytes && bytes.SequenceEqual(coverBytes)),
                ("歌词字节(读盘)", async () =>
                    await cache.TryGetLyricAsync(audioSong) is { } cached
                    && cached.Original == lyric.Original && cached.Translation == lyric.Translation),
                ("歌单曲目(读盘)", async () =>
                    (await cache.TryGetPlaylistTracksAsync(playlist)).Count == tracks.Count),
                ("离线歌单索引(纯内存)", () => Task.FromResult(
                    cache.TryGetPlaylistLibrary(MusicSource.NetEase)?.Playlists.Count == 1)),
                ("音频存在性(扫目录)", () => Task.FromResult(
                    cache.GetAudioCacheAvailability([audioSong])[0])),
            ];

            async Task<List<Sample>> ReadAllAsync()
            {
                var result = new List<Sample>();
                foreach (var read in reads)
                    result.Add(await TimeAsync(read.Name, read.Action));
                return result;
            }

            var idle = await ReadAllAsync();
            Log("[gate-read] 空闲时读耗时: " + Describe(idle));
            if (idle.Any(sample => !sample.Ok)) return Fail("空闲读的内容断言失败: " + Describe(idle));

            // 把目录撑到生产量级:裁剪的成本几乎全在"枚举 + stat + 排序"上,与文件内容无关。
            var seedWatch = Stopwatch.StartNew();
            var filler = new byte[4096];
            for (var index = 0; index < fillerCount; index++)
                await File.WriteAllBytesAsync(Path.Combine(root, $"filler-{index:D6}.bin"), filler);
            seedWatch.Stop();
            Log($"[gate-read] 填充 {fillerCount} 个文件(共 {fillerCount * 4 / 1024}MB)用时 {seedWatch.ElapsedMilliseconds}ms");

            // 每个读都配一次**独立**的裁剪窗口:第一次读会把整个等待吃掉,
            // 若共用一次裁剪,后面的读量到的都是"锁已释放"的 0ms —— 那会掩盖问题。
            var busy = new List<Sample>();
            var windows = new List<long>();
            for (var round = 1; round <= rounds; round++)
            {
                foreach (var read in reads)
                {
                    // 触发一次强制裁剪:与改容量上限是同一条路径,整段扫描都在 gate 里。
                    var windowWatch = Stopwatch.StartNew();
                    var mutation = Task.Run(async () =>
                    {
                        await cache.SetMaximumSizeMbAsync(1024).ConfigureAwait(false);
                        windowWatch.Stop();
                    });
                    // 让裁剪先进 gate,再发读请求 —— 这样量到的就是"读在等锁"。
                    await Task.Delay(15).ConfigureAwait(false);

                    var sample = await TimeAsync(read.Name, read.Action);
                    await mutation.ConfigureAwait(false);
                    var window = windowWatch.ElapsedMilliseconds;
                    busy.Add(sample);
                    windows.Add(window);
                    Log($"[gate-read] 第 {round} 轮 {sample.Name}: 裁剪占锁 {window}ms → 读 {sample.Ms}ms" +
                        (sample.Ok ? "" : " (内容不符!)"));
                }
            }

            var widest = windows.Count == 0 ? 0 : windows.Max();
            var worst = WorstByName(busy);
            Log("[gate-read] 裁剪进行中各读最坏耗时: " + Describe(worst));

            if (busy.Any(sample => !sample.Ok)) return Fail("裁剪进行中的读内容断言失败: " + Describe(busy));
            if (widest < MinimumMutationWindowMs)
            {
                Log($"[gate-read] 提示: 裁剪只占锁 {widest}ms,窗口太小无法判定(可提高 ALY_CACHE_FILES);" +
                    "未做断言");
            }
            else
            {
                var over = worst.FirstOrDefault(sample => sample.Ms > BusyReadBudgetMs);
                if (over.Name is not null)
                    return Fail($"裁剪占锁 {widest}ms 期间「{over.Name}」被堵 {over.Ms}ms" +
                                $"(预算 {BusyReadBudgetMs}ms → 读仍在排队等 _mutationGate)");
            }

            Log($"[gate-read] PASS: 裁剪占锁 {widest}ms 期间所有读都在 {BusyReadBudgetMs}ms 内完成");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static async Task<Sample> TimeAsync(string name, Func<Task<bool>> action)
    {
        var watch = Stopwatch.StartNew();
        var ok = false;
        try { ok = await action().ConfigureAwait(false); }
        catch { ok = false; }
        watch.Stop();
        return new Sample(name, watch.ElapsedMilliseconds, ok);
    }

    /// <summary>同名折叠成最坏一条 —— 报告要看的是"卡得最久的那次"。</summary>
    private static List<Sample> WorstByName(List<Sample> samples) =>
        samples.GroupBy(sample => sample.Name)
            .Select(group => group.OrderByDescending(sample => sample.Ms).First())
            .ToList();

    private static string Describe(IEnumerable<Sample> samples) =>
        string.Join(" | ", samples.Select(sample =>
            $"{sample.Name}={sample.Ms}ms{(sample.Ok ? "" : "(内容不符)")}"));

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static int Fail(string message)
    {
        Log("[gate-read] FAIL: " + message);
        return 1;
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { }
    }

    private readonly record struct Sample(string Name, long Ms, bool Ok);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent([1, 2, 3, 4, 5]);
            content.Headers.ContentType = new("audio/mpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }
}
