using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.NetEase;
using ALyricEase.ViewModels;
using ALyricEase.Views;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ALyricEase.Headless;

/// <summary>
/// "打开别的歌单时,曲目封面又闪了一遍"探针(--pl-cover-flash)。
///
/// 用户现象:在歌单页点开另一个歌单,新歌单的曲目封面**一开始马上就出来了**,
/// 约 0.5 秒后**又闪了一下、看起来重新加载了一遍**;而歌单封面(头部大图)不会。
///
/// 代码侧的解释(见 PlaylistViewModel.OpenPlaylistAsync):打开歌单是**两遍装载** ——
///   ① <c>RestoreCachedTracksAsync</c> 把该歌单的磁盘快照(每歌单一个 JSON)立刻上屏,
///      封面命中原有缓存,所以"马上就加载好了";
///   ② 网络 <c>GetPlaylistTrackOverviewAsync</c> 回来后 <c>ClearTrackRows()</c> +
///      <c>AppendKnownTracks()</c> **把整张表清空重建**。
/// 而歌单封面走的是 <c>playlist.RefreshCover()</c>,只在 **URL 变了**才重载 —— 所以它不闪。
/// 0.5 秒正好是那次网络往返。
///
/// 本探针要证的不是"有两遍",而是**第二遍为什么会看见闪**。被检验的机制是:
/// 清空(Rst)会把行容器交还回收池,容器复用过程中 DataContext 先被清成 null ⇒
/// 封面附加属性收到 null 就把已解码的图丢掉 ⇒ 新 DataContext 落下来时再走一遍异步
/// SetSource ⇒ 中间那几帧露出行模板底下的 <c>ArtFallback</c> 占位色。
///
/// 三档单变量对照(同一进程、同一份数据、同一批已解码封面):
///   · 基线   —— 什么都不做(证明观测装置本身能读到 0)
///   · A 现状 —— <c>Tracks.Clear()</c> + <c>ReplaceAll</c>(与生产的清空+重建同构)
///   · B 候选 —— 逐位 <c>Tracks[i] = 新行</c>(容器不离树,封面 URL 未变)
///
/// ⚠ 观测量刻意**不是**"实化行控件数":容器被销毁的那几帧控件数为 0,
/// 只看"源为 null 的行数"会读成 0 —— 把最严重的闪报成"没闪"。
/// 这里量的是**"本该显示封面、实际没显示的行数"**(以基线实化行数为期望值),
/// 容器被销毁与"容器在但源为 null"两种形态都能计到。
/// </summary>
internal static class PlaylistCoverFlashProbe
{
    public static async Task<int> RunRealAsync()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log($"[flash] UI 线程未处理异常: {e.Exception}");
            e.Handled = true;
        };
        try { return await RunCoreAsync(); }
        catch (Exception ex) { Log($"[flash] 异常: {ex}"); return 1; }
    }

    private static async Task<int> RunCoreAsync()
    {
        var rowCount = EnvInt("ALY_FLASH_ROWS", 150);
        var coverUrls = EnvInt("ALY_FLASH_COVERS", 40);
        var sampleMs = EnvInt("ALY_FLASH_SAMPLE_MS", 600);
        var rounds = EnvInt("ALY_FLASH_ROUNDS", 2);

        var main = ServiceLocator.Get<MainViewModel>();
        var playlist = ServiceLocator.Get<PlaylistViewModel>();

        var covers = await FetchRealCoverUrlsAsync(coverUrls);
        var songs = CreateSongs(rowCount, covers);
        Log($"[flash] 数据: 行={rowCount} 真实封面 URL={covers.Count} 个(循环使用 ⇒ 两次装载用的是同一批 URL)");

        var window = new TestMainWindow { DataContext = main, ShowActivated = false };
        window.Show();
        window.Topmost = true;   // 保证不被遮挡(与其它真窗口探针一致;虽然 Image.Source 不依赖合成,保持同口径)
        window.Activate();
        await DrainAsync(700);

        // 走与 --idle-cpu-real 同一条注入路径(页面快照恢复,不触网),数据摆好后切到歌单页。
        await InjectAsync("初始", songs);
        main.ActivePage = "Favorites";
        await DrainAsync(1600);
        Log($"[flash] 上屏: 歌单页行={playlist.RetainedTrackRowCount} 实化行控件={CountRows(window)}");

        var coversReady = await WaitForCoversAsync(window, 10_000);
        var expected = CountRows(window);   // 基线实化行数 = "本该有多少行的封面在屏幕上"
        var baselineMissing = CountMissingCovers(window, expected);
        Log($"[flash] 预热: 封面就绪={coversReady} 期望行数={expected} 基线缺图={baselineMissing} " +
            $"解码图缓存={DescribeCache()}");
        if (expected == 0)
        {
            Log("[flash] ⚠ 一行都没实化,观测装置没跑起来,本轮结论不可信");
            return 1;
        }
        if (baselineMissing > 0)
            Log($"[flash] ⚠ 基线本身就有 {baselineMissing} 行缺图 —— 下面的尖峰要减去这个基数才准");

        // 每个档位前都**重新注入一次**(每次都用新的快照键):模式 A/B 只动 Tracks、不动 _allTrackRows,
        // 连着测会让后面档位的起始状态不是同一份,所以一档一次清场。
        var modes = new (string Label, Action Trigger)[]
        {
            ("基线(不动)", () => { }),
            ("C 生产:原地换绑", () => ApplyFreshRows(playlist)),
            ("A 现状:Clear+Reset", () => RebuildByClear(playlist)),
            ("B 候选:逐位替换", () => RebuildInPlace(playlist)),
        };

        var runs = new List<Run>();
        for (var round = 1; round <= rounds; round++)
        {
            foreach (var (label, trigger) in modes)
            {
                await InjectAsync($"R{round}·{label}", songs);
                await WaitForCoversAsync(window, 10_000);
                runs.Add(await MeasureAsync(window, label, round, CountRows(window), sampleMs, trigger));
                await Task.Delay(150);
            }
        }

        Report(runs);
        return 0;
    }

    /// <summary>生产的通知序列:<c>ClearTrackRows()</c>(两个集合各 Clear)之后由
    /// <c>RefreshVisibleTracks</c> 走 <c>ReplaceAll</c>(Clear + 重填 + 一次 Reset)。</summary>
    private static void RebuildByClear(PlaylistViewModel playlist)
    {
        var fresh = BuildFreshRows(playlist);
        playlist.Tracks.Clear();
        playlist.Tracks.ReplaceAll(fresh);
    }

    /// <summary>候选修法:逐位替换。容器不离树 ⇒ DataContext 直接换人 ⇒
    /// 绑定推来的 <c>Song.CoverUrl</c> 字符串没变 ⇒ ManagedCoverImage 的附加属性无变更通知 ⇒
    /// Image 保留已解码的那张图。多出来的尾巴从末尾摘掉(它们本来也会被重建)。
    /// ⚠ 实测结论:这条**不成立** —— Avalonia 的 Replace 通知同样会让 ItemsControl 整批换容器,
    /// 所以保留它作对照,别当成可用的修法。</summary>
    private static void RebuildInPlace(PlaylistViewModel playlist)
    {
        var fresh = BuildFreshRows(playlist);
        var tracks = playlist.Tracks;
        var common = Math.Min(tracks.Count, fresh.Count);
        for (var i = 0; i < common; i++) tracks[i] = fresh[i];
        while (tracks.Count > fresh.Count) tracks.RemoveAt(tracks.Count - 1);
        if (fresh.Count > common) tracks.AddRange(fresh.Skip(common).ToList());
    }

    /// <summary>**生产路径**:直接调 <c>PlaylistViewModel.ApplyFreshRows</c> ——
    /// 共同前缀的行只换内部播放绑定,行对象与集合元素都不动。这是要验的修法本身。</summary>
    private static void ApplyFreshRows(PlaylistViewModel playlist)
    {
        var songs = playlist.Tracks.Select(row => CloneSong(row.Song)).ToList();
        playlist.ApplyFreshRows(songs,
            (song, index) => new SongItemViewModel(song, _ => Task.FromResult(true), index + 1));
    }

    /// <summary>与已上屏的行同歌同封面,但**是新对象** —— 模拟网络结果落地时新建的行 VM。</summary>
    private static List<SongItemViewModel> BuildFreshRows(PlaylistViewModel playlist) =>
        playlist.Tracks
            .Select(row => new SongItemViewModel(CloneSong(row.Song), song => Task.FromResult(true), row.Index))
            .ToList();

    /// <summary>同一首歌的"网络副本":字段一致、<c>CoverUrl</c> 一字不差(这正是"不该重新下载"的依据)。</summary>
    private static Song CloneSong(Song source) => new()
    {
        Id = source.Id,
        Source = source.Source,
        Mid = source.Mid,
        Name = source.Name,
        Artist = source.Artist,
        Album = source.Album,
        CoverUrl = source.CoverUrl,
        DurationMs = source.DurationMs,
        Fee = source.Fee,
        ArtistIds = source.ArtistIds,
        ArtistNames = source.ArtistNames,
        ArtistMids = source.ArtistMids,
        AlbumId = source.AlbumId,
        AlbumMid = source.AlbumMid,
    };

    private static async Task<Run> MeasureAsync(
        TestMainWindow window, string label, int round,
        int expected, int sampleMs, Action trigger)
    {
        var missing = new List<int>();
        var realized = new List<int>();
        var fingerprints = new List<int>();
        var cacheBefore = CoverImagePipeline.MemoryCacheStats;

        trigger();

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < sampleMs)
        {
            missing.Add(CountMissingCovers(window, expected));
            realized.Add(CountRows(window));
            fingerprints.Add(FingerprintRows(window));
            await Task.Delay(5);
        }
        stopwatch.Stop();

        var cacheAfter = CoverImagePipeline.MemoryCacheStats;
        return new Run(
            label, round, expected, missing, realized, fingerprints,
            cacheBefore, cacheAfter, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>本该显示封面、实际没显示的行数。期望值取基线实化行数:
    /// 容器被销毁(实化行数掉到 0)与"容器在但源为 null"都会计进来。</summary>
    private static int CountMissingCovers(TestMainWindow window, int expected)
    {
        var painted = 0;
        foreach (var row in window.GetVisualDescendants().OfType<TrackRow>())
        {
            if ((row.DataContext as SongItemViewModel)?.Song.CoverUrl is not { Length: > 0 }) continue;
            if (row.GetVisualDescendants().OfType<Image>().FirstOrDefault()?.Source is not null) painted++;
        }
        return Math.Max(0, expected - painted);
    }

    private static int CountRows(TestMainWindow window) =>
        window.GetVisualDescendants().OfType<TrackRow>().Count();

    /// <summary>已实化行控件的身份指纹:变了说明容器整批换人(重建),
    /// 不变说明容器就地复用(只换了 DataContext)。这是"到底重建没重建"的直接证据。</summary>
    private static int FingerprintRows(TestMainWindow window)
    {
        var hash = new HashCode();
        foreach (var row in window.GetVisualDescendants().OfType<TrackRow>())
            hash.Add(RuntimeHelpers.GetHashCode(row));
        return hash.ToHashCode();
    }

    /// <summary>等封面都加载出来(基线必须在"全都有图"的状态下取,否则尖峰被自己的加载尾巴污染)。</summary>
    private static async Task<bool> WaitForCoversAsync(TestMainWindow window, int timeoutMs)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        var lastMissing = int.MaxValue;
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            var rows = CountRows(window);
            var missing = CountMissingCovers(window, rows);
            if (rows > 0 && missing == 0)
            {
                if (++stable >= 6) return true;   // 连续 6 次采样(≈30ms)都齐才算稳
            }
            else
            {
                stable = 0;
            }
            lastMissing = missing;
            await Task.Delay(5);
        }
        Log($"[flash] 等封面超时: 仍缺 {lastMissing} 行");
        return false;
    }

    private static void Report(List<Run> runs)
    {
        Log("[flash] ===== 判定:开歌单时封面“闪一下”的成因 =====");
        Log("[flash] 档位                       轮  期望行  峰值缺图  缺图帧数/总帧  空树帧  容器整批换人   解码图缓存");
        foreach (var run in runs)
        {
            var peak = run.Missing.Count == 0 ? 0 : run.Missing.Max();
            var missingFrames = run.Missing.Count(value => value > 0);
            var blankFrames = run.Realized.Count(value => value == 0);
            Log($"[flash] {run.Label,-26} {run.Round,2}  {run.Expected,6}  {peak,8}  " +
                $"{missingFrames,6}/{run.Missing.Count,-6}  {blankFrames,6}  " +
                $"{(run.FingerprintChanged ? "是(重建)" : "否(就地)"),-12}  " +
                $"{run.CacheBefore.Items}→{run.CacheAfter.Items}项");
        }

        Log("[flash] ===== 结论 =====");
        Log($"[flash] ① 观测装置自证(基线应为 0): 峰值缺图={Peak(runs, "基线")}");
        Log($"[flash] ② 现状 A 清空+重建 : 峰值缺图={Peak(runs, "A")}  容器整批换人={AnyRebuilt(runs, "A")}");
        Log($"[flash] ③ 对照 B 逐位替换 : 峰值缺图={Peak(runs, "B")}  容器整批换人={AnyRebuilt(runs, "B")}");
        Log($"[flash] ④ 生产 C 原地换绑 : 峰值缺图={Peak(runs, "C")}  容器整批换人={AnyRebuilt(runs, "C")}");

        var baselinePeak = Peak(runs, "基线");
        var productionPeak = Peak(runs, "C");
        if (baselinePeak > 0)
            Log("[flash] ⇒ 基线自身就缺图,观测装置不成立,本轮结论不可信");
        else if (productionPeak == 0)
            Log($"[flash] ⇒ PASS: 生产路径(C)全程 0 行缺图;清空重建(A)峰值 {Peak(runs, "A")} 行、" +
                $"逐位替换(B)峰值 {Peak(runs, "B")} 行 ⇒ “图片又加载了一遍”的成因是**行容器被重建**," +
                $"修法是让行对象与集合都不动、只换行内部的播放绑定");
        else
            Log($"[flash] ⇒ 生产路径仍缺 {productionPeak} 行:修法不够,继续查");
    }

    private static int Peak(List<Run> runs, string prefix) =>
        runs.Where(run => run.Label.StartsWith(prefix))
            .Select(run => run.Missing.Count == 0 ? 0 : run.Missing.Max())
            .DefaultIfEmpty(0).Max();

    private static bool AnyRebuilt(List<Run> runs, string prefix) =>
        runs.Any(run => run.Label.StartsWith(prefix) && run.FingerprintChanged);

    private static async Task InjectAsync(string tag, List<Song> songs)
    {
        var playlist = ServiceLocator.Get<PlaylistViewModel>();
        var cache = ServiceLocator.Get<MusicCacheService>();
        var cacheKey = Guid.NewGuid().ToString("N");
        var payload = new PlaylistPageCacheData(
            songs.Select(song => new NavigationPageCacheTrack(song, true, true)).ToList(),
            songs.Select(song => song.Id).ToArray(),
            songs.Count,
            null);
        _ = cache.CachePlaylistPageSnapshotAsync(cacheKey, payload);
        var snapshot = new PlaylistNavigationSnapshot(
            cacheKey,
            PlaylistPageKind.NetEase,
            new Playlist
            {
                Id = 78,
                Source = MusicSource.NetEase,
                Name = "封面闪烁探针",
                TrackCount = songs.Count,
            },
            null,
            "封面闪烁探针",
            "测试用户",
            0,
            "",
            false,
            0);
        await playlist.RestoreNavigationSnapshotAsync(snapshot);
        Log($"[flash] 注入[{tag}]: 行={playlist.RetainedTrackRowCount} 队列={playlist.RetainedQueueSongCount}");
    }

    private static async Task<List<string>> FetchRealCoverUrlsAsync(int wanted)
    {
        var result = new List<string>();
        try
        {
            var api = ServiceLocator.Get<NetEaseApiClient>();
            foreach (var keyword in new[] { "周杰伦", "林俊杰", "陈奕迅", "五月天", "邓紫棋" })
            {
                var songs = await api.SearchAsync(keyword, 30).ConfigureAwait(false);
                foreach (var song in songs)
                    if (song.CoverUrl.Length > 0 && !result.Contains(song.CoverUrl))
                        result.Add(song.CoverUrl);
                if (result.Count >= wanted) break;
            }
        }
        catch (Exception ex)
        {
            Log($"[flash] 真实封面 URL 获取失败(网络?): {ex.Message}");
        }
        return result.Take(wanted).ToList();
    }

    private static List<Song> CreateSongs(int count, IReadOnlyList<string> covers) =>
        Enumerable.Range(1, count).Select(index => new Song
        {
            Id = 860_000 + index,
            Source = MusicSource.NetEase,
            Name = $"封面闪烁歌曲 {index:D4}",
            Artist = "探针歌手",
            Album = "探针专辑",
            DurationMs = 180_000,
            ArtistIds = [9101],
            ArtistNames = ["探针歌手"],
            AlbumId = 9201,
            CoverUrl = covers.Count > 0 ? covers[index % covers.Count] : "",
        }).ToList();

    private static string DescribeCache()
    {
        var stats = CoverImagePipeline.MemoryCacheStats;
        return $"{stats.Items}项/{stats.Bytes / 1024.0 / 1024.0:F1}MB";
    }

    private static async Task DrainAsync(int milliseconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) await Task.Delay(5);
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static void Log(string message)
    {
        Console.WriteLine(message);
        var path = Environment.GetEnvironmentVariable("ALY_PROBE_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try { File.AppendAllText(path, message + Environment.NewLine); }
        catch { }
    }

    private sealed record Run(
        string Label,
        int Round,
        int Expected,
        List<int> Missing,
        List<int> Realized,
        List<int> Fingerprints,
        (long Bytes, int Items) CacheBefore,
        (long Bytes, int Items) CacheAfter,
        double ElapsedMs)
    {
        /// <summary>指纹是否中途换过(出现过两个以上不同值)。</summary>
        public bool FingerprintChanged => Fingerprints.Distinct().Count() > 1;
    }
}
