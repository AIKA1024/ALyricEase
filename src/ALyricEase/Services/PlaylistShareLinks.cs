using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>歌单分享链接(侧栏右键"复制链接"、歌单 more 菜单):两平台均为公开网页路由,拼 id 即可,
/// 无专用接口。QQ 用数字歌单 id(tid,即 y.qq.com 歌单页路由的 dissid;资产目录 dirId 不用于网页路由),
/// 网易云用歌单 id。⚠ QQ 榜单不是歌单:路由是 /toplist/{topId},拿 topId 当 dissid 会拼出 404 页面。</summary>
public static class PlaylistShareLinks
{
    public static string For(Playlist playlist) => playlist.Source == MusicSource.QQ
        ? playlist.TopId > 0
            ? $"https://y.qq.com/n/ryqq/toplist/{playlist.TopId}"
            : $"https://y.qq.com/n/ryqq/playlist/{playlist.Id}"
        : $"https://music.163.com/playlist/{playlist.Id}";
}
