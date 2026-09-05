using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>歌曲公开网页链接，供播放条菜单的“分享/复制链接”共用。</summary>
public static class SongShareLinks
{
    public static string? For(Song song)
    {
        if (song.Source == MusicSource.QQ)
            return song.Mid.Length == 0 ? null : $"https://y.qq.com/n/ryqq/songDetail/{song.Mid}";

        return song.Id == 0 ? null : $"https://music.163.com/song?id={song.Id}";
    }
}
