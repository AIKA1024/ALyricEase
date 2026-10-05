using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>列表行和播放器共用的、无需请求播放地址即可确定的可播性判断。</summary>
internal static class PlaybackAvailability
{
    public static bool CanAttempt(Song song, IMusicApi? api)
    {
        if (song.IsPlaybackUnavailable) return false;
        if (song.IsNoCopyright) return false;

        // 已确认有本地音频的离线快照不依赖当前账号在线权益。
        if (song.PreferCachedPlayback) return true;

        // 免费与普通音质免费曲目无需会员。其他收费曲目在能解析账号客户端时，
        // 未登录统一提前禁用（即使模型残留上一账号的已购状态也不能据此放行）。
        if (song.Fee is 0 or 8) return true;
        if (api is not null && !api.IsLoggedIn) return false;

        // 已登录账号明确购买过单曲/数字专辑时，无需 VIP 也可播放。
        if (song.IsPurchased == true) return true;

        // 网易云 fee=4 是单曲/数字专辑单独售卖。明确未购买时提前禁用；
        // 旧缓存或精简接口没给 privilege 时保留可点，交播放地址接口最终确认。
        if (song.Source == MusicSource.NetEase && song.Fee == 4)
            return song.IsPurchased is null;

        // fee=1（以及未知的非零收费类型）按账号会员状态预判。
        // 未加载时不能把 IsVip 的默认 false 当成非会员，否则登录恢复期间会误禁用。
        if (api is null) return true;
        return !api.IsVipLoaded || api.IsVip;
    }
}
