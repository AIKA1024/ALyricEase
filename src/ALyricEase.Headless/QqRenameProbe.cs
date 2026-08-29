using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ALyricEase.Services.Auth;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>QQ 音乐歌单重命名探针 v2(--qqrename):EditPlaylist 真实签名已从参考实现
/// tlyanyu/multiPlatformMusicApi@0fd583b platforms/qqmusic/module/playlist_update.js 落实:
/// param {dirId, mask, dirNewName, dirNewDesc, dirNewPicUrl, dirNewtaglist}
/// (v1 盲试 dirName/tid 等恒 1101,字段名错非参数缺)。本探针验证三件事:
/// ① dirId 应取创建响应还是用户歌单列表(GetPlaylistByUin)的值(两者可能不同);
/// ② mask 位语义 —— 15=全量改,1=仅名字(若 1 可行,生产改名用它,不碰用户简介/标签);
/// ③ 全程可自清理:v1 用创建响应 dirId=1 删歌单未复检,可能残留,先扫 ALE 前缀清掉。
/// 流程:清残留 → 建 → 写简介+名字(mask 15)→ 复检 → 仅改名(mask 1)→ 复检名字+简介 →
/// 删除 → 复检已消失。每次写操作后留间隔,降低频率风控概率。</summary>
public static class QqRenameProbe
{
    private const string Module = "music.musicasset.PlaylistBaseWrite";

    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qq-rename] 未配置 QQ Cookie,跳过");
            return 0;
        }

        // 0) 残留清理:v1 探针删除未复检,先把 ALE 前缀测试歌单扫掉
        await CleanupLeftoversAsync(api);

        var stamp = DateTime.Now.ToString("MMdd-HHmmss");
        var name0 = $"ALE改名{stamp}";
        var n1 = $"ALE改名A{stamp}";
        var n2 = $"ALE改名B{stamp}";

        // 1) 创建(直接打原始响应:对照创建响应 dirId 与列表 dirId 是否一致)
        long tid, dirFromCreate;
        try
        {
            using var doc = await api.ProbeAssetWriteAsync(Module, "AddPlaylist",
                new Dictionary<string, object?> { ["dirName"] = name0 }, "创建歌单");
            Console.WriteLine($"[qq-rename] 创建原始响应: {Short(doc.RootElement, 400)}");
            var result = TryGetPath(doc.RootElement, "req_0", "data", "result")
                         ?? TryGetPath(doc.RootElement, "req_0", "data");
            tid = PickLong(result, "id", "tid");
            dirFromCreate = PickLong(result, "dirId");
            Console.WriteLine($"[qq-rename] 创建成功 tid={tid} 创建响应dirId={dirFromCreate}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qq-rename][FAIL] 创建失败: {ex.Message}");
            return 1;
        }

        var ok = false;
        long dirId = 0; // 复检到的权威资产目录 id(finally 删除用;列表缺 dirId 时回落创建响应值)
        try
        {
            // 2) 列表复检:GetPlaylistByUin 侧的 dirId 才是参考实现实际采用的取值来源
            await Task.Delay(800);
            var mine = await FindAsync(api, tid);
            if (mine is null)
            {
                Console.WriteLine("[qq-rename][FAIL] 创建后列表未见该歌单");
                return 1;
            }
            dirId = mine.DirId != 0 ? mine.DirId : dirFromCreate;
            Console.WriteLine($"[qq-rename] 列表复检: dirFromList={mine.DirId} (创建响应={dirFromCreate},采用={dirId}) desc=[{mine.Description}]");

            // 3) mask=1 仅名字(生产首选:不携带 desc/pic/tag 字段,零清空风险)。
            //    注:v2 实测 EditPlaylist 改名生效,但简介字段服务端不落库(参考实现作者
            //    同注「更改简介不生效」),故改名探针只以名字复检为判定,desc 仅作观察项。
            string? recipe = null;
            await TryEditAsync(api, new()
            {
                ["dirId"] = dirId, ["mask"] = 1, ["dirNewName"] = n1,
            });
            await Task.Delay(800);
            var after1 = await FindAsync(api, tid);
            Console.WriteLine($"[qq-rename] mask=1 后: name=[{after1?.Name}] (期望[{n1}]) desc=[{after1?.Description}]");
            if (after1?.Name == n1)
                recipe = "EditPlaylist {dirId=列表dirId, mask=1, dirNewName} — 仅名字,简介/标签不受影响";

            // 4) 回落 mask=15 全量(参考实现签名;生产需带原简介,taglist 置空有清标签风险)
            if (recipe is null)
            {
                await TryEditAsync(api, new()
                {
                    ["dirId"] = dirId, ["mask"] = 15,
                    ["dirNewName"] = n2, ["dirNewDesc"] = "",
                    ["dirNewPicUrl"] = "", ["dirNewtaglist"] = "",
                });
                await Task.Delay(800);
                var after15 = await FindAsync(api, tid);
                Console.WriteLine($"[qq-rename] mask=15 后: name=[{after15?.Name}] (期望[{n2}]) desc=[{after15?.Description}]");
                if (after15?.Name == n2)
                    recipe = "EditPlaylist {dirId=列表dirId, mask=15, dirNewName+dirNewDesc+dirNewPicUrl+dirNewtaglist} — 全量;taglist 置空可能清标签";
            }

            Console.WriteLine($"\n[qq-rename] 有效配方: {recipe ?? "无(mask=15 也未命中)"}");
            ok = recipe is not null;
        }
        finally
        {
            // 6) 删除 + 复检消失,账号还原
            try
            {
                await api.DeletePlaylistAsync(dirId != 0 ? dirId : tid);
                await Task.Delay(800);
                var gone = await FindAsync(api, tid) is null;
                Console.WriteLine($"[qq-rename] 已删除测试歌单 tid={tid} (delId={dirId}),复检消失={gone}");
                if (!gone) Console.WriteLine("[qq-rename][WARN] 删除后列表仍见该歌单,需手动清理");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qq-rename][WARN] 删除失败(账号残留 tid={tid}): {ex.Message}");
            }
        }

        return ok ? 0 : 1;
    }

    /// <summary>清理历史探针残留(ALE改名/ALE已改名 前缀的自建歌单),逐个删除并复检。</summary>
    private static async Task CleanupLeftoversAsync(QQMusicApiClient api)
    {
        try
        {
            var lists = await api.GetUserPlaylistsAsync();
            foreach (var p in lists.Where(p => p.Name.StartsWith("ALE改名") || p.Name.StartsWith("ALE已改名")))
            {
                var delId = p.DirId != 0 ? p.DirId : p.Id;
                Console.WriteLine($"[qq-rename] 发现历史残留 [{p.Name}] tid={p.Id} dirId={p.DirId} → 删除");
                try
                {
                    await api.DeletePlaylistAsync(delId);
                    await Task.Delay(800);
                    var still = (await api.GetUserPlaylistsAsync()).Any(x => x.Id == p.Id);
                    Console.WriteLine($"      删除复检: {(still ? "仍存在 ✗" : "已消失 ✓")}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      删除失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qq-rename][WARN] 残留扫描失败: {ex.Message}");
        }
    }

    /// <summary>以给定 param 打 EditPlaylist;业务码非 0 时打印错误但不中断(逐项试错语义)。
    /// 返回是否业务码 0。</summary>
    private static async Task<bool> TryEditAsync(QQMusicApiClient api, Dictionary<string, object?> param)
    {
        try
        {
            using var doc = await api.ProbeAssetWriteAsync(Module, "EditPlaylist", param, "重命名");
            Console.WriteLine($"[qq-rename] EditPlaylist({Label(param)}): 业务码 0 → {Short(doc.RootElement, 260)}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qq-rename] EditPlaylist({Label(param)}): 失败 — {ex.Message}");
            return false;
        }
    }

    private static string Label(Dictionary<string, object?> param) =>
        string.Join(",", param.Select(kv => kv.Key));

    private static async Task<Models.Playlist?> FindAsync(QQMusicApiClient api, long tid) =>
        (await api.GetUserPlaylistsAsync()).FirstOrDefault(p => p.Id == tid);

    private static JsonElement? TryGetPath(JsonElement e, params string[] path)
    {
        var cur = e;
        foreach (var seg in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur))
                return null;
        }
        return cur;
    }

    private static long PickLong(JsonElement? e, params string[] keys)
    {
        if (e is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var k in keys)
            {
                if (obj.TryGetProperty(k, out var v) &&
                    (v.TryGetInt64(out var l) || (v.ValueKind == JsonValueKind.String &&
                                                  long.TryParse(v.GetString(), out l))))
                    return l;
            }
        }
        return 0;
    }

    private static string Short(JsonElement e, int n = 220)
    {
        var s = e.GetRawText();
        return s.Length <= n ? s : s[..n] + "...";
    }
}
