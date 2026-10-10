using ALyricEase.Models;

namespace ALyricEase.Infrastructure;

/// <summary>歌曲快照是否可继续使用原有展示行及其导航/播放信息。</summary>
internal static class SongMetadata
{
    public static bool Matches(Song a, Song b) => ReferenceEquals(a, b)
        || a.Source == b.Source && a.Id == b.Id && a.Mid == b.Mid
        && a.Name == b.Name && a.Artist == b.Artist && a.Album == b.Album
        && a.CoverUrl == b.CoverUrl && a.DurationMs == b.DurationMs
        && a.Fee == b.Fee && a.IsPurchased == b.IsPurchased
        && a.AlbumId == b.AlbumId && a.AlbumMid == b.AlbumMid
        && a.LocalFilePath == b.LocalFilePath
        && a.IsPlaybackUnavailable == b.IsPlaybackUnavailable && a.IsNoCopyright == b.IsNoCopyright
        && a.ArtistIds.SequenceEqual(b.ArtistIds) && a.ArtistNames.SequenceEqual(b.ArtistNames)
        && a.ArtistMids.SequenceEqual(b.ArtistMids);
}
