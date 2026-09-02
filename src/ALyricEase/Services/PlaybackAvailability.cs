using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>列表行和播放器共用的、无需请求播放地址即可确定的可播性判断。</summary>
internal static class PlaybackAvailability
{
    public static bool CanAttempt(Song song, IMusicApi? api)
    {
        if (song.IsPlaybackUnavailable) return false;
        if (song.Fee == 0 || api is null) return true;
        if (!api.IsLoggedIn) return false;

        return !api.IsVipLoaded || api.IsVip;
    }
}
