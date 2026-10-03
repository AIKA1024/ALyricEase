using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>
/// 综合搜索歌曲的“我喜欢”目标解析。单平台结果直接返回；合并结果只有在用户
/// 明确保存过默认平台时才返回目标，否则返回 null，由界面询问。
/// </summary>
public static class CombinedLikeTargetResolver
{
    public static IReadOnlyList<MusicSource> GetSources(Song song)
        => SongRecordingResolver.GetSources(song);

    public static MusicSource? Resolve(
        Song song,
        MusicSource? preferredSource)
    {
        var sources = GetSources(song);
        if (sources.Count == 1) return sources[0];

        if (preferredSource is { } preferred && sources.Contains(preferred))
            return preferred;

        return null;
    }
}
