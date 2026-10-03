using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>在综合搜索合并项中按平台定位真实录音，供收藏、歌单写入等操作复用。</summary>
public static class SongRecordingResolver
{
    public static Song? Resolve(Song song, MusicSource source)
    {
        if (song.Source == source) return song;
        return song.AlternateRecordings.FirstOrDefault(candidate => candidate.Source == source);
    }

    public static IReadOnlyList<MusicSource> GetSources(Song song)
        => new[] { song.Source }
            .Concat(song.AlternateRecordings.Select(static candidate => candidate.Source))
            .Distinct()
            .ToArray();
}
