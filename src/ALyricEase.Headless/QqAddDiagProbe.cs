using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>QQ 收藏(AddSonglist)被拒分变体诊断探针(--qqadd)。实测结论(2026-09-30):
/// AddSonglist 要求 Android 客户端身份 comm(ct=11/cv=14090008/authst/tmeLoginType/qq),
/// web comm(uin+g_tk)一律回 80105 而 DelSonglist 宽松 —— 即"取消收藏成功、收藏报登录过期"的根因。
/// 本探针固定一首未喜欢曲目,先 Del 对照打通写通道,再逐变体打 Add 打印真实服务端码,
/// 最后做清理还原(循环 Del 到 code=0)并验证 PlaylistBaseWrite/AddPlaylist 是否同病。</summary>
public static class QqAddDiagProbe
{
    private const long SamplePlaylistId = 7294917175; // 与 --qqdiag 同一样例歌单
    private const string DetailWrite = "music.musicasset.PlaylistDetailWrite";
    private const string BaseWrite = "music.musicasset.PlaylistBaseWrite";

    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cookie = new CookieStore();
        var api = new QQMusicApiClient(cookie);
        Console.WriteLine($"[qqadd] IsLoggedIn={api.IsLoggedIn}");

        // 0) 常规凭证校验(非强制续期)
        try
        {
            await api.EnsureCredentialValidAsync();
            Console.WriteLine("[qqadd] 0) EnsureCredentialValidAsync: 通过");
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqadd] 0) EnsureCredentialValidAsync: FAIL code={ex.Code} msg={ex.Message}");
        }

        // 0b) 清理历史探针歌单(AddPlaylist 对不存在 dirId 的 DelPlaylist 也回 code=0,
        //     上轮创建的"ALY探针*"可能残留):走生产删除路径按名称匹配
        try
        {
            var pls = await api.GetUserPlaylistsAsync();
            Console.WriteLine($"[qqadd] 当前歌单 {pls.Count} 个: " +
                string.Join(" | ", pls.Select(p => $"{p.Name}(id={p.Id},dirId={p.DirId})")));
            foreach (var p in pls.Where(p => p.Name.StartsWith("ALY探针")).ToList())
            {
                await api.DeletePlaylistAsync(p.DirId != 0 ? p.DirId : p.Id);
                Console.WriteLine($"[qqadd] 已清理残留探针歌单 [{p.Name}] id={p.Id} dirId={p.DirId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqadd] 残留歌单清理失败:{ex.Message}(不影响后续诊断)");
        }

        // 1) 选样:样例歌单里第一首未喜欢的曲目
        var tracks = await api.GetPlaylistTracksAsync(SamplePlaylistId);
        await api.EnsureLikedIdsAsync();
        var song = tracks.FirstOrDefault(t => t.Id != 0 && !api.IsLiked(t.Id))
                   ?? tracks.FirstOrDefault(t => t.Id != 0);
        if (song is null) { Console.WriteLine("[qqadd] 样例歌单曲目获取失败"); return 1; }
        Console.WriteLine($"[qqadd] 样例曲目 id={song.Id} [{song.Name} - {song.Artist}] wasLiked={api.IsLiked(song.Id)}");

        // 凭证摘要(Android comm 需要 authst/tmeLoginType/qq)
        var rawCookie = cookie.QQCookieRaw ?? "";
        var musicKey = Cv(rawCookie, "qm_keyst") ?? Cv(rawCookie, "qqmusic_key") ?? "";
        var uin = (Cv(rawCookie, "uin") ?? Cv(rawCookie, "qqmusic_uin") ?? "0").TrimStart('o');
        var loginType = long.TryParse(Cv(rawCookie, "tmeLoginType"), out var lt) ? lt : 0;
        var guid = Guid.NewGuid().ToString("N");
        Console.WriteLine($"[qqadd] 凭证摘要 uin={uin} tmeLoginType={loginType} keyLen={musicKey.Length}");

        Action<Utf8JsonWriter> webComm = w => WriteWebComm(w, uin);
        Action<Utf8JsonWriter> androidComm = w => WriteAndroidComm(w, uin, musicKey, loginType, guid);
        Action<Utf8JsonWriter> addParam = w => WriteUpstreamParam(w, song.Id, song.Mid);

        // 2) 对照:DelSonglist(web comm,带 Cookie)—— 现行生产行为,预期 code=0
        await TryWrite(api, DetailWrite, "DelSonglist", "对照 web comm", addParam, webComm);

        // 3) Add:web comm(现行生产,预期 80105) vs Android comm(上游客户端身份,预期 0)
        var addDoc = await TryWrite(api, DetailWrite, "AddSonglist", "A1 web comm(现行)", addParam, webComm);
        if (addDoc is null && musicKey.Length > 0)
            addDoc = await TryWrite(api, DetailWrite, "AddSonglist", "A2 Android comm", addParam, androidComm);
        var addOk = addDoc is not null;
        addDoc?.Dispose();

        // 4) 清理还原:等落库后 Del(Android comm)对照一次,再循环 Del(web comm)到成功
        if (addOk)
        {
            await Task.Delay(3000);
            await TryWrite(api, DetailWrite, "DelSonglist", "还原对照 Android comm", addParam, androidComm);
            for (var i = 0; i < 3; i++)
            {
                if (await TryWrite(api, DetailWrite, "DelSonglist", "还原 web comm", addParam, webComm) is not null)
                    break;
                await Task.Delay(2000);
            }
        }

        // 5) AddPlaylist(创建歌单)是否同样要求 Android comm:双 comm 各建一个,各用双形状删除还原
        if (musicKey.Length > 0)
        {
            var name = $"ALY探针{DateTime.Now:HHmmss}";
            using var docA = await TryWrite(api, BaseWrite, "AddPlaylist", "B1 Android comm",
                w => w.WriteString("dirName", name), androidComm);
            var dirIdA = docA is null ? 0 : PickLong(docA, "req_0", "data", "result", "dirId");
            if (docA is not null)
                Console.WriteLine($"[qqadd] 创建响应原文(Android): {docA.RootElement.GetRawText()}");
            using var docB = await TryWrite(api, BaseWrite, "AddPlaylist", "B2 web comm",
                w => w.WriteString("dirName", name + "b"), webComm);
            var dirIdB = docB is null ? 0 : PickLong(docB, "req_0", "data", "result", "dirId");
            if (docB is not null)
                Console.WriteLine($"[qqadd] 创建响应原文(web): {docB.RootElement.GetRawText()}");
            Console.WriteLine($"[qqadd] 创建结果 dirIdA(Android)={dirIdA} dirIdB(web)={dirIdB}");

            foreach (var (dirId, tag) in new[] { (dirIdA, "A"), (dirIdB, "B") })
            {
                if (dirId == 0) continue;
                var delParam = (long id) => (Action<Utf8JsonWriter>)(w => w.WriteNumber("dirId", id));
                if (await TryWrite(api, BaseWrite, "DelPlaylist", $"还原{tag} web comm",
                        delParam(dirId), webComm) is null)
                    await TryWrite(api, BaseWrite, "DelPlaylist", $"还原{tag} Android comm",
                        delParam(dirId), androidComm);
            }
        }

        // 6) 生产路径端到端:LikeToggleAsync(Add→Android comm)应成功,再 toggle 还原
        try
        {
            var nowLiked = await api.LikeToggleAsync(song.Id);
            Console.WriteLine($"[qqadd] 6) 生产 LikeToggleAsync(Add): 成功={nowLiked}");
            if (nowLiked)
            {
                // 落库有秒级延迟,过早 Del 回 2001(暂不存在):循环重试到还原成功
                for (var i = 1; i <= 6; i++)
                {
                    await Task.Delay(3000);
                    try
                    {
                        if (!await api.LikeToggleAsync(song.Id))
                        {
                            Console.WriteLine($"[qqadd] 6) 生产 LikeToggleAsync(Del 还原): 成功(第{i}次)");
                            break;
                        }
                    }
                    catch (ApiException ex) when (ex.Code == 2001)
                    {
                        Console.WriteLine($"[qqadd] 6) 还原第{i}次: 2001(落库延迟,重试)");
                        if (i == 6) Console.WriteLine("[qqadd] 6) ⚠ 还原未成功,请手动检查红心状态!");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqadd] 6) 生产 LikeToggleAsync: FAIL {ex.GetType().Name}: {ex.Message}");
        }

        // 7) 终态校验:重载已喜欢集合,把历轮样例曲目(含上轮还原失败的)逐一确保未喜欢
        try
        {
            foreach (var probeId in new long[] { song.Id, 726533222, 724809138 })
            {
                var liked = await api.ReloadLikedIdsForProbeAsync();
                if (liked is not HashSet<long> set || !set.Contains(probeId)) continue;
                Console.WriteLine($"[qqadd] 7) 发现残留红心 id={probeId},执行清理...");
                for (var i = 1; i <= 6; i++)
                {
                    try
                    {
                        if (!await api.LikeToggleAsync(probeId))
                        {
                            Console.WriteLine($"[qqadd] 7) id={probeId} 已还原(第{i}次)");
                            break;
                        }
                    }
                    catch (ApiException ex) when (ex.Code == 2001 && i < 6)
                    {
                        await Task.Delay(3000);
                    }
                }
            }
            Console.WriteLine("[qqadd] 7) 终态校验完成");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqadd] 7) 终态校验失败:{ex.Message}(请手动核对红心)");
        }

        Console.WriteLine("[qqadd] 完成(逐行输出即判定)");
        return 0;
    }

    /// <summary>执行一次写并打印结果;成功返回解密响应文档(调用方释放),失败返回 null。</summary>
    private static async Task<JsonDocument?> TryWrite(QQMusicApiClient api, string module, string method,
        string tag, Action<Utf8JsonWriter> writeParam, Action<Utf8JsonWriter>? writeComm)
    {
        try
        {
            var doc = await api.SecureAssetWriteAsync(module, method, writeParam, $"探针:{method}({tag})", default,
                sendCookie: true, writeComm: writeComm);
            Console.WriteLine($"[qqadd] {method}({tag}): code=0 成功");
            return doc;
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqadd] {method}({tag}): code={ex.Code} msg={ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqadd] {method}({tag}): FAIL {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>web comm(现行生产形状):cv=4747474/ct=24/yqq.json/uin+g_tk。</summary>
    private static void WriteWebComm(Utf8JsonWriter w, string uin)
    {
        w.WriteNumber("cv", 4747474);
        w.WriteNumber("ct", 24);
        w.WriteString("format", "json");
        w.WriteString("inCharset", "utf-8");
        w.WriteString("outCharset", "utf-8");
        w.WriteNumber("notice", 0);
        w.WriteString("platform", "yqq.json");
        w.WriteNumber("needNewCode", 1);
        w.WriteString("uin", uin);
    }

    /// <summary>Android 客户端 comm(上游 VersionPolicy.build_comm(ANDROID) 形状):
    /// ct=11/cv=14090008/authst=musickey/tmeLoginType/qq;设备类字段合成,uid/sid/QIMEI 省略。</summary>
    private static void WriteAndroidComm(Utf8JsonWriter w, string uin, string musicKey, long loginType, string guid)
    {
        w.WriteNumber("ct", 11);
        w.WriteNumber("cv", 14090008);
        w.WriteNumber("v", 14090008);
        w.WriteString("chid", "10003505");
        w.WriteString("qq", uin);
        w.WriteString("authst", musicKey);
        w.WriteString("tmeAppID", "qqmusic");
        w.WriteNumber("tmeLoginType", loginType);
        w.WriteString("QIMEI36", "");
        w.WriteString("OpenUDID", guid);
        w.WriteString("udid", guid);
    }

    /// <summary>上游精确 param 形状:{dirId:201, tid:0, bFmtUtf8:true, v_songInfo:[{songId,songType:0}]}。</summary>
    private static void WriteUpstreamParam(Utf8JsonWriter w, long songId, string mid)
    {
        w.WriteNumber("dirId", QQMusicApiClient.LikedDirId);
        w.WriteNumber("tid", 0);
        w.WriteBoolean("bFmtUtf8", true);
        w.WriteStartArray("v_songInfo");
        w.WriteStartObject();
        w.WriteNumber("songId", songId);
        w.WriteNumber("songType", 0);
        if (mid.Length > 0) w.WriteString("songMid", mid);
        w.WriteEndObject();
        w.WriteEndArray();
    }

    private static long PickLong(JsonDocument doc, params string[] path)
    {
        var el = doc.RootElement;
        foreach (var seg in path)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(seg, out el)) return 0;
        }
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : 0;
    }

    private static string? Cv(string raw, string name)
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
