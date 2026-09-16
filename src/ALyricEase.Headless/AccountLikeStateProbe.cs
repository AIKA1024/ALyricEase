using System.Reflection;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>换号后红心状态回归(--accountlike),全程不触网、不碰用户真实登录态。
///
/// 要守住的不变量:SetMusicUCookie / SetCookie / ClearCookie 之后,上一个账号的
/// "我喜欢的音乐"归属与已喜欢集合必须清空,且登录身份代次必须推进。
/// 旧实现只重置 VIP 状态,漏了这三项 —— 于是新账号会用旧账号的歌单 id 配旧账号的红心集合,
/// IsLiked 给出错误结果、点赞请求还会带错 userid。
///
/// 断言方式:先用反射把"旧账号状态"塞进私有字段(否则离线环境下这些字段恒为空,
/// 断言会假通过),再执行换号,看它们有没有被清掉。代次推进则顺带覆盖了
/// "在途加载结果必须作废"这条路径的机制。</summary>
internal static class AccountLikeStateProbe
{
    private const long OldLikedPlaylistId = 777;
    private const long OldLikedSongId = 42;
    private const long OldUserId = 999;
    private const long OldQqLikedTid = 555;

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var dir = Path.Combine(Path.GetTempPath(), $"aly-accountlike-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var failures = 0;
        try
        {
            failures += CheckNetEase(Path.Combine(dir, "ne-cookie.json"));
            failures += CheckQq(Path.Combine(dir, "qq-cookie.json"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(failures == 0
            ? "[accountlike] PASS 换号清空红心状态(网易云: likedPlaylistId/currentUserId/likedIds;QQ: likedDissTid/likedIds),代次推进"
            : $"[accountlike] FAIL: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int CheckNetEase(string cookiePath)
    {
        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), new CookieStore(cookiePath));
        var failures = 0;

        // 模拟"已经登录账号 A 并加载过红心"
        Seed(api, "_likedPlaylistId", OldLikedPlaylistId);
        Seed(api, "_currentUserId", OldUserId);
        Seed(api, "_likedIds", new HashSet<long> { OldLikedSongId });
        var generationBefore = api.AccountGeneration;
        FailIf(ref failures, !api.IsLiked(OldLikedSongId), "用例前提:注入的旧账号红心未被识别");

        // 换号:粘贴账号 B 的 MUSIC_U
        api.SetMusicUCookie("MUSIC_U=probe-account-b-value");

        FailIf(ref failures, api.AccountGeneration <= generationBefore, "网易云换号没有推进登录身份代次");
        FailIf(ref failures, api.LikedPlaylistId != 0, "网易云换号后仍保留上一账号的喜欢歌单 id");
        FailIf(ref failures, api.IsLiked(OldLikedSongId), "网易云换号后 IsLiked 仍命中上一账号的红心集合");
        FailIf(ref failures, api.CanToggleLike, "网易云换号后仍报告可以红心(会拿旧歌单 id 发点赞)");

        // 登出同样要清
        Seed(api, "_likedPlaylistId", OldLikedPlaylistId);
        Seed(api, "_likedIds", new HashSet<long> { OldLikedSongId });
        var generationAtLogout = api.AccountGeneration;
        api.ClearCookie();
        FailIf(ref failures, api.AccountGeneration <= generationAtLogout, "网易云登出没有推进登录身份代次");
        FailIf(ref failures, api.LikedPlaylistId != 0 || api.IsLiked(OldLikedSongId),
            "网易云登出后仍保留红心状态");

        return failures;
    }

    private static int CheckQq(string cookiePath)
    {
        var api = new QQMusicApiClient(new CookieStore(cookiePath));
        var failures = 0;

        Seed(api, "_likedDissTid", OldQqLikedTid);
        Seed(api, "_likedIds", new HashSet<long> { OldLikedSongId });
        var generationBefore = api.AccountGeneration;
        FailIf(ref failures, !api.IsLiked(OldLikedSongId), "用例前提:注入的旧账号红心未被识别");

        api.SetCookie("uin=123456789; qqmusic_key=probe-key-value; p_skey=probe-pskey");

        FailIf(ref failures, api.AccountGeneration <= generationBefore, "QQ 换号没有推进登录身份代次");
        FailIf(ref failures, api.IsLiked(OldLikedSongId), "QQ 换号后 IsLiked 仍命中上一账号的红心集合");

        Seed(api, "_likedDissTid", OldQqLikedTid);
        Seed(api, "_likedIds", new HashSet<long> { OldLikedSongId });
        var generationAtLogout = api.AccountGeneration;
        api.ClearCookie();
        FailIf(ref failures, api.AccountGeneration <= generationAtLogout, "QQ 登出没有推进登录身份代次");
        FailIf(ref failures, api.IsLiked(OldLikedSongId), "QQ 登出后仍保留红心集合");

        return failures;
    }

    /// <summary>反射写私有字段,模拟"上一个账号留下的缓存状态"。</summary>
    private static void Seed(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"字段 {fieldName} 不存在(实现已重命名,请同步探针)");
        field.SetValue(target, value);
    }

    private static void FailIf(ref int failures, bool isFailure, string message)
    {
        if (!isFailure) return;
        failures++;
        Console.Error.WriteLine($"[accountlike][FAIL] {message}");
    }
}
