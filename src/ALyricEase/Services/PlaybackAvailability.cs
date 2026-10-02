using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>列表行和播放器共用的、无需请求播放地址即可确定的可播性判断。</summary>
internal static class PlaybackAvailability
{
    public static bool CanAttempt(Song song, IMusicApi? api)
    {
        if (song.IsPlaybackUnavailable) return false;
        // 服务端权益判定无版权(privilege.st<0):fee 常为 0,旧判据会误判可点;
        // 与账号/VIP 无关,任何登录态下都必然失败(2026-10-02 实测:周杰伦专辑匿名=VIP 逐字段一致)
        if (song.IsNoCopyright) return false;
        if (song.Fee == 0 || api is null) return true;
        if (!api.IsLoggedIn) return false;

        return !api.IsVipLoaded || api.IsVip;
    }
}
