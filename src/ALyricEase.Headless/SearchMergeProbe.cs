using ALyricEase.Models;
using ALyricEase.Services;

namespace ALyricEase.Headless;

/// <summary>综合搜索跨平台录音匹配的纯内存回归，不访问网络。</summary>
public static class SearchMergeProbe
{
    public static void Run()
    {
        var failures = 0;

        var sunnyNe = Song(MusicSource.NetEase, 1, "晴天", ["周杰伦"], 269_000, "叶惠美");
        var sunnyQq = Song(MusicSource.QQ, 2, "晴天", ["周杰伦"], 270_000, "叶惠美", original: 1);
        var merged = SongSearchMerger.Merge([sunnyNe], [sunnyQq], 30, song => song.Source == MusicSource.QQ ? 10 : 0);
        Check(ref failures, merged.Count == 1, "同名/同歌手/近似时长应合并");
        Check(ref failures, merged[0].Source == MusicSource.QQ && merged[0].AlternateRecordings.Count == 1,
            "主音源偏好与备用录音应保留");

        var combinedSong = merged[0];
        Check(ref failures,
            SongRecordingResolver.Resolve(combinedSong, MusicSource.QQ)?.Id == sunnyQq.Id
            && SongRecordingResolver.Resolve(combinedSong, MusicSource.NetEase)?.Id == sunnyNe.Id,
            "合并结果应能按目标歌单平台定位对应录音");
        var singleSourceSong = Song(MusicSource.NetEase, 11, "后来", ["刘若英"], 341_000, "我等你");
        Check(ref failures,
            CombinedLikeTargetResolver.Resolve(singleSourceSong, null) == MusicSource.NetEase,
            "单平台结果的红心应直接路由到该平台");
        Check(ref failures,
            CombinedLikeTargetResolver.Resolve(combinedSong, MusicSource.QQ) == MusicSource.QQ,
            "已保存默认平台时红心应优先路由到默认平台");
        Check(ref failures,
            CombinedLikeTargetResolver.Resolve(combinedSong, null) is null,
            "两个平台均可写且未选择默认平台时应要求用户选择");

        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "ALyricEase.SearchMergeProbe",
            Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(stateDirectory, "state.json");
        try
        {
            var state = new AppStateStore(statePath);
            state.SetPreferredCombinedLikeSource(MusicSource.QQ);
            state.SetPreferredSearchSource(SearchSourceMode.QQ);
            var restored = new AppStateStore(statePath);
            Check(ref failures, restored.PreferredCombinedLikeSource == MusicSource.QQ,
                "综合搜索默认红心平台应持久化并恢复");
            Check(ref failures, restored.PreferredSearchSource == SearchSourceMode.QQ,
                "搜索来源应持久化并恢复");
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }

        Check(ref failures,
            SongSearchMerger.Merge(
                [sunnyNe],
                [Song(MusicSource.QQ, 3, "晴天 (Live Version)", ["周杰伦"], 270_000, "演唱会")],
                30).Count == 2,
            "Live 与录音室版本不得合并");

        Check(ref failures,
            SongSearchMerger.Merge(
                [sunnyNe],
                [Song(MusicSource.QQ, 4, "晴天（伴奏）", ["周杰伦"], 269_500, "叶惠美")],
                30).Count == 2,
            "伴奏与原唱不得合并");

        Check(ref failures,
            SongSearchMerger.Merge(
                [Song(MusicSource.NetEase, 5, "合唱", ["甲", "乙"], 200_000, "专辑")],
                [Song(MusicSource.QQ, 6, "合唱", ["乙", "甲"], 201_000, "专辑")],
                30).Count == 1,
            "歌手顺序不同仍应合并");

        Check(ref failures,
            SongSearchMerger.Merge(
                [Song(MusicSource.NetEase, 7, "光年之外", ["G.E.M. 邓紫棋"], 235_000, "新的心跳")],
                [Song(MusicSource.QQ, 8, "光年之外", ["邓紫棋"], 236_000, "新的心跳")],
                30).Count == 1,
            "常见中英文艺名包含关系应匹配");

        Check(ref failures,
            SongSearchMerger.Merge(
                [sunnyNe],
                [Song(MusicSource.QQ, 9, "晴天", ["另一位歌手"], 270_000, "叶惠美")],
                30).Count == 2,
            "同名不同歌手不得合并");

        Check(ref failures,
            SongSearchMerger.Merge(
                [sunnyNe],
                [Song(MusicSource.QQ, 10, "晴天", ["周杰伦"], 278_000, "叶惠美")],
                30).Count == 2,
            "时长差过大不得合并");

        Console.WriteLine(failures == 0
            ? "[searchmerge] PASS 综合搜索匹配回归全部通过"
            : $"[searchmerge] FAIL {failures} 项失败");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static Song Song(
        MusicSource source,
        long id,
        string name,
        IReadOnlyList<string> artists,
        int duration,
        string album,
        string subtitle = "",
        int? original = null)
        => new()
        {
            Source = source,
            Id = id,
            Mid = source == MusicSource.QQ ? $"mid-{id}" : "",
            Name = name,
            Artist = string.Join('/', artists),
            ArtistNames = artists,
            DurationMs = duration,
            Album = album,
            Subtitle = subtitle,
            OriginalVersion = original,
        };

    private static void Check(ref int failures, bool condition, string message)
    {
        Console.WriteLine($"[searchmerge] {(condition ? "PASS" : "FAIL")} {message}");
        if (!condition) failures++;
    }
}
