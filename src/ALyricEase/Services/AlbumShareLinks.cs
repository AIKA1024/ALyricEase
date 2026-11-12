using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>专辑公开网页链接（专辑页头部的分享/复制链接，浏览页「关注新专」菜单也用它）。
/// 两个平台都是公开路由，拼 id 即可：网易云 `/album?id=`、QQ `/ryqq/albumDetail/{albumMid}`。</summary>
public static class AlbumShareLinks
{
    public static string? For(Song song)
        => song.Source == MusicSource.QQ ? ForMid(song.AlbumMid) : ForId(song.AlbumId);

    public static string? ForId(long albumId)
        => albumId > 0 ? $"https://music.163.com/album?id={albumId}" : null;

    public static string? ForMid(string albumMid)
        => albumMid.Length == 0 ? null : $"https://y.qq.com/n/ryqq/albumDetail/{albumMid}";
}
