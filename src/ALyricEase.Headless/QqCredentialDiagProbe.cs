using System;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>QQ 凭证过期诊断探针(--qqdiag):对同一份本机 Cookie 分别打
/// 凭证校验(EnsureCredentialValidAsync)、账号摘要(设置页刷新路径)、红心写入三条链路,
/// 打印各自真实结果与错误码 —— 定位"读接口全放行、写接口 80105 拒绝"的过期形态。</summary>
public static class QqCredentialDiagProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cookie = new CookieStore();
        var api = new QQMusicApiClient(cookie);
        Console.WriteLine($"[qqdiag] IsLoggedIn={api.IsLoggedIn} CanToggleLike={api.CanToggleLike}");
        DumpCredentialTimes(cookie);

        // 1) 凭证校验(登录/恢复登录时服务端确认链路)
        try
        {
            await api.EnsureCredentialValidAsync();
            Console.WriteLine("[qqdiag] 1) EnsureCredentialValidAsync: 通过(服务端认为凭证有效)");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqdiag] 1) EnsureCredentialValidAsync: FAIL code={ex.Code} msg={ex.Message}");
        }
        DumpCredentialTimes(cookie, "校验后");

        // 2) 账号摘要 = 设置页"刷新账号信息"的完整路径
        try
        {
            var s = await api.GetAccountSummaryAsync();
            Console.WriteLine($"[qqdiag] 2) GetAccountSummaryAsync: 成功 昵称=[{s.Nickname}] vip={s.IsVip} 会员={s.MembershipName}");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqdiag] 2) GetAccountSummaryAsync: FAIL code={ex.Code} msg={ex.Message}");
        }

        // 3) 红心写入(ag-1 加密写通道):取每日30首首曲做 Add→Del 还原
        Song? song = null;
        try
        {
            song = (await api.GetPlaylistTracksAsync(7294917175)).FirstOrDefault(s => s.Id != 0);
            if (song is null)
            {
                Console.WriteLine("[qqdiag] 3) 样例曲目获取失败");
                return 1;
            }
            var added = await api.LikeToggleAsync(song.Id);
            Console.WriteLine($"[qqdiag] 3) LikeToggleAsync(Add): 成功={added}");
            if (added) await api.LikeToggleAsync(song.Id); // 还原
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqdiag] 3) LikeToggleAsync: FAIL code={ex.Code} msg={ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqdiag] 3) LikeToggleAsync: FAIL {ex.GetType().Name}: {ex.Message}");
        }

        // 3b) 明文签名通道对照(参考 L-1124/QQMusicApi 的 musics.fcg 传输层):同参同签名不加密
        if (song is not null)
        {
            try
            {
                using var doc = await api.ProbeSignedPlaintextWriteAsync(
                    "music.musicasset.PlaylistDetailWrite", "AddSonglist", p =>
                    {
                        p.WriteNumber("dirId", QQMusicApiClient.LikedDirId);
                        p.WriteNumber("tid", 0);
                        p.WriteBoolean("bFmtUtf8", true);
                        p.WriteStartArray("v_songInfo");
                        p.WriteStartObject();
                        p.WriteNumber("songType", 0);
                        p.WriteNumber("songId", song.Id);
                        p.WriteEndObject();
                        p.WriteEndArray();
                    }, default);
                var code = doc.RootElement.TryGetProperty("req_0", out var r0) &&
                           r0.TryGetProperty("code", out var c) && c.TryGetInt32(out var v) ? v : -1;
                Console.WriteLine($"[qqdiag] 3b) 明文签名通道 AddSonglist: code={code}" +
                                  (code == 0 ? "(写通道活着!80105 是 ag-1 传输层问题)" : "(明文签名通道也被拒)"));
                if (code == 0)
                {
                    using var del = await api.ProbeSignedPlaintextWriteAsync(
                        "music.musicasset.PlaylistDetailWrite", "DelSonglist", p =>
                        {
                            p.WriteNumber("dirId", QQMusicApiClient.LikedDirId);
                            p.WriteNumber("tid", 0);
                            p.WriteBoolean("bFmtUtf8", true);
                            p.WriteStartArray("v_songInfo");
                            p.WriteStartObject();
                            p.WriteNumber("songType", 0);
                            p.WriteNumber("songId", song.Id);
                            p.WriteEndObject();
                            p.WriteEndArray();
                        }, default);
                    Console.WriteLine("[qqdiag] 3b) 已用明文签名通道 DelSonglist 还原红心");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qqdiag] 3b) 明文签名通道: FAIL {ex.GetType().Name}: {ex.Message}");
            }

            // 3c) 强制续期(续期会占用登录设备名额,仅在用户明确要求诊断时跑):看新 key 能否恢复写权限
            try
            {
                Console.WriteLine("[qqdiag] 3c) 强制续期 RefreshCredentialCoreAsync ...");
                await api.RefreshCredentialForProbeAsync(default);
                DumpCredentialTimes(cookie, "续期后");
            }
            catch (ApiException ex)
            {
                Console.WriteLine($"[qqdiag] 3c) 强制续期: FAIL code={ex.Code} msg={ex.Message}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qqdiag] 3c) 强制续期: FAIL {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
            try
            {
                var added = await api.LikeToggleAsync(song.Id);
                Console.WriteLine($"[qqdiag] 3c) 续期后 LikeToggleAsync(Add): 成功={added}" +
                                  (added ? "(续期恢复写权限!80105 应触发自动续期重试)" : "(续期后的 key 仍无写权限)"));
                if (added) await api.LikeToggleAsync(song.Id); // 还原
            }
            catch (ApiException ex)
            {
                Console.WriteLine($"[qqdiag] 3c) 续期后 LikeToggleAsync: FAIL code={ex.Code}(续期无法恢复写权限)");
            }
        }
        // 4) 每日推荐(RecommendFeed → 每日30首 → CgiGetDiss,失败回落雷达流)
        try
        {
            var songs = await api.GetDailyRecommendSongsAsync(default);
            Console.WriteLine($"[qqdiag] 4) 每日推荐: {songs.Count} 首" +
                              (songs.Count > 0 ? $",首曲 [{songs[0].Name} - {songs[0].Artist}]" : "(空列表)"));
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqdiag] 4) 每日推荐: FAIL code={ex.Code} msg={ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqdiag] 4) 每日推荐: FAIL {ex.GetType().Name}: {ex.Message}");
        }
        return 0;
    }

    /// <summary>打印凭证时间戳字段(时间戳非敏感;key 值一律打码)。</summary>
    private static void DumpCredentialTimes(CookieStore cookie, string tag = "当前")
    {
        var raw = cookie.QQCookieRaw ?? "";
        Console.WriteLine($"[qqdiag]   [{tag}] tmeLoginType={Val(raw, "tmeLoginType") ?? "(缺)"} " +
                          $"keyCreateTime={Val(raw, "psrf_musickey_createtime") ?? "(缺)"} " +
                          $"accessTokenExpiresAt={Val(raw, "psrf_access_token_expiresAt") ?? "(缺)"} " +
                          $"refreshKey={(Val(raw, "psrf_qqrefresh_key") is { Length: > 0 } ? "有" : "缺")} " +
                          $"refreshToken={(Val(raw, "psrf_qqrefresh_token") is { Length: > 0 } ? "有" : "缺")} " +
                          $"qm_keyst={CookieStore.Mask(Val(raw, "qm_keyst"))}");
    }

    private static string? Val(string raw, string name)
    {
        foreach (var part in raw.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return null;
    }
}
