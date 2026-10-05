using System;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Models;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>QQ 音乐 API 冒烟探针(--qqapi):无 UI 直接打真实接口,验证歌单链路修复。
/// 匿名态覆盖:公开歌单曲目(uniform_get_Dissinfo + 分页终止)、未登录用户歌单/资料的错误语义。</summary>
public static class QqApiProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        var exit = 0;

        // 1) 公开歌单曲目:2026-08 实测仍有效的官方"每日30首"动态歌单(匿名可读)
        const long publicDissTid = 7294917175;
        try
        {
            var songs = await api.GetPlaylistTracksAsync(publicDissTid);
            Console.WriteLine($"[qqapi] 歌单 {publicDissTid} 曲目数={songs.Count}(期望约30)");
            foreach (var s in songs.Take(3))
                Console.WriteLine($"[qqapi]   #{s.Id}/{s.Mid} {s.Name} - {s.Artist} 专辑=[{s.Album}] 封面={(s.CoverUrl.Length > 0 ? "有" : "无")} fee={s.Fee}");
            if (songs.Count == 0)
            {
                Console.WriteLine("[qqapi][FAIL] 公开歌单返回空列表 —— 歌单详情链路未修复");
                exit = 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqapi][FAIL] 拉取公开歌单异常: {ex.GetType().Name}: {ex.Message}");
            exit = 1;
        }

        // 2) 用户歌单(侧边栏"QQ音乐"分组数据源):asset 双模块拉取,打印与 UI 同源的名称;
        //    本机有持久化 Cookie 时应列出真实歌单;无凭据时回落 homepage 后报明确 ApiException。
        try
        {
            var pls = await api.GetUserPlaylistsAsync();
            Console.WriteLine($"[qqapi] 用户歌单 {pls.Count} 个:");
            foreach (var p in pls)
                Console.WriteLine($"[qqapi]   id={p.Id} 名=[{p.Name}] {p.TrackCount}首 封面={(p.CoverUrl.Length > 0 ? "有" : "无")}");

            var first = pls.FirstOrDefault(p => p.TrackCount != 0);
            if (first is not null)
            {
                var tracks = await api.GetPlaylistTracksAsync(first.Id);
                Console.WriteLine($"[qqapi] 点开 [{first.Name}]: 曲目 {tracks.Count} 首(歌单标称 {first.TrackCount}),首曲 [{tracks.FirstOrDefault()?.Name} - {tracks.FirstOrDefault()?.Artist}]");
                if (tracks.Count == 0)
                {
                    Console.WriteLine("[qqapi][FAIL] 用户歌单曲目为空 —— 详情链路仍有问题");
                    exit = 1;
                }
            }
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqapi] 无凭据时用户歌单报错符合预期: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqapi][FAIL] 用户歌单异常类型不对 {ex.GetType().Name}: {ex.Message}");
            exit = 1;
        }

        // 3) 用户资料(BaseUserInfoServer 优先、homepage 回落):同样验证两种结局都合理
        try
        {
            var profile = await api.GetUserProfileAsync();
            Console.WriteLine($"[qqapi] 用户资料: 昵称=[{profile.Nickname}] 头像={(profile.AvatarUrl.Length > 0 ? "有" : "无")}");
            if (profile.Nickname.Length == 0)
            {
                Console.WriteLine("[qqapi][FAIL] 资料昵称为空 —— 名称链路仍有问题");
                exit = 1;
            }
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqapi] 无凭据时资料报错符合预期: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqapi][FAIL] 资料接口异常类型不对 {ex.GetType().Name}: {ex.Message}");
            exit = 1;
        }

        Console.WriteLine(exit == 0 ? "[qqapi] 全部通过" : "[qqapi] 存在失败项");
        return exit;
    }

    /// <summary>搜索冒烟探针(--qqsearch):client_search_cp 已死(HTTP 500,2026-09-30 实测),
    /// 对上游 SearchCgiService/DoSearchForQQMusicMobile 做传输变体二分,打印各变体真实服务端码。</summary>
    public static async Task<int> RunSearchProbeAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        Console.WriteLine($"[qqsearch] IsLoggedIn={api.IsLoggedIn}");
        var cookie = new CookieStore();
        var raw = cookie.QQCookieRaw ?? "";
        var musicKey = Cv(raw, "qm_keyst") ?? Cv(raw, "qqmusic_key") ?? "";
        var uin = (Cv(raw, "uin") ?? Cv(raw, "qqmusic_uin") ?? "0").TrimStart('o');
        var loginType = long.TryParse(Cv(raw, "tmeLoginType"), out var lt) ? lt : 0;

        string SearchParam(string keyword, int num, int page) =>
            "{\"searchid\":\"" + (Random.Shared.NextInt64(1, 21) * 18014398509481984 +
                Random.Shared.NextInt64(0, 4194304) * 4294967296 + Random.Shared.NextInt64(1, int.MaxValue)) +
                "\",\"query\":\"" + keyword + "\",\"search_type\":0,\"num_per_page\":" + num +
                ",\"page_num\":" + page + ",\"highlight\":true,\"grp\":true,\"selectors\":{},\"vec_selectors\":[]}";

        string AndroidComm() => "{" +
            $"\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\",\"qq\":\"{uin}\"," +
            $"\"authst\":\"{musicKey}\",\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":{loginType}," +
            "\"QIMEI36\":\"\",\"OpenUDID\":\"0123456789abcdef0123456789abcdef\",\"udid\":\"0123456789abcdef0123456789abcdef\"}";

        string WebComm() => "{\"ct\":24,\"cv\":4747474,\"format\":\"json\",\"inCharset\":\"utf-8\"," +
            $"\"outCharset\":\"utf-8\",\"notice\":0,\"platform\":\"yqq.json\",\"needNewCode\":1,\"uin\":\"{uin}\"}}";

        string DesktopComm() => "{\"ct\":19,\"cv\":1859,\"uin\":\"\"}";

        async Task TestAsync(string tag, string method, string comm, string ua)
        {
            try
            {
                var json = "{\"comm\":" + comm + ",\"req_0\":{\"module\":\"music.search.SearchCgiService\"," +
                           "\"method\":\"" + method + "\",\"param\":" + SearchParam("任然", 10, 1) + "}}";
                using var http = new System.Net.Http.HttpClient();
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
                {
                    Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/n/ryqq/search");
                req.Headers.TryAddWithoutValidation("User-Agent", ua);
                if (api.IsLoggedIn) req.Headers.TryAddWithoutValidation("Cookie", raw);
                using var resp = await http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                var code = root.TryGetProperty("req_0", out var r0) && r0.TryGetProperty("code", out var c)
                    ? c.GetInt32() : -1;
                var count = 0;
                string? first = null;
                if (code == 0 &&
                    root.TryGetProperty("req_0", out var r0b) && r0b.TryGetProperty("data", out var d) &&
                    d.TryGetProperty("body", out var b) && b.TryGetProperty("song", out var sg) &&
                    sg.TryGetProperty("list", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    count = list.GetArrayLength();
                    if (count > 0 && list[0].TryGetProperty("name", out var n1))
                        first = n1.GetString();
                }
                else if (code == 0)
                {
                    // 形状漂移:打印 req_0 下的键帮助定位
                    var keys = root.TryGetProperty("req_0", out var r0c) && r0c.TryGetProperty("data", out var dc)
                        ? string.Join(",", dc.EnumerateObject().Select(p => p.Name))
                        : "(无 data)";
                    Console.WriteLine($"[qqsearch] {tag}: code=0 但形状异常 data 键=[{keys}]");
                    return;
                }
                Console.WriteLine($"[qqsearch] {tag}: HTTP {(int)resp.StatusCode} code={code} 数量={count} 首曲=[{first}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qqsearch] {tag}: FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        await TestAsync("V1 Mobile+Android comm+QQ UA", "DoSearchForQQMusicMobile", AndroidComm(),
            "QQMusic 14090008(android 14)");
        await TestAsync("V2 Mobile+web comm+Chrome UA", "DoSearchForQQMusicMobile", WebComm(),
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        await TestAsync("V3 Desktop+ct19cv1859 comm(匿名)", "DoSearchForQQMusicDesktop", DesktopComm(),
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");

        // 4) 生产路径端到端:修复后的 SearchAsync / SearchAllAsync
        try
        {
            var songs = await api.SearchAsync("任然", 10);
            Console.WriteLine($"[qqsearch] 生产 SearchAsync: {songs.Count} 首,首曲=[{songs.FirstOrDefault()?.Name} - {songs.FirstOrDefault()?.Artist}]");
            var all = await api.SearchAllAsync("任然", SearchKind.All, 10);
            Console.WriteLine($"[qqsearch] 生产 SearchAllAsync: 歌曲 {all?.Songs.Count ?? -1} 首 / 歌单 {all?.Playlists.Count ?? -1} 个");
            // 歌单通道稳定性:多关键词 × 重复,定位"搜索歌单失败"的触发条件
            foreach (var kw in new[] { "流行", "周杰伦", "粤语", "睡前", "任然" })
            {
                try
                {
                    var pls = await api.SearchAllAsync(kw, SearchKind.Playlist, 10);
                    Console.WriteLine($"[qqsearch] 歌单Tab[{kw}] limit10: {pls?.Playlists.Count ?? -1} 个,首个=[{pls?.Playlists.FirstOrDefault()?.Name}]");
                }
                catch (ApiException ex)
                {
                    Console.WriteLine($"[qqsearch] 歌单Tab[{kw}] FAIL code={ex.Code}: {ex.Message}");
                }
            }

            // 登录态歌单搜索:带 Cookie + comm uin(生产 SearchPlaylistsAsync 目前匿名)连续 6 发
            for (var i = 1; i <= 6; i++)
            {
                try
                {
                    var json = "{\"comm\":{\"ct\":19,\"cv\":1859,\"uin\":\"" + uin + "\"},\"req_1\":{\"module\":\"music.search.SearchCgiService\"," +
                               "\"method\":\"DoSearchForQQMusicDesktop\",\"param\":{\"search_type\":3,\"query\":\"歌单" + i + "\"," +
                               "\"page_num\":1,\"num_per_page\":10}}}";
                    using var http = new System.Net.Http.HttpClient();
                    using var req = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
                    {
                        Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                    };
                    req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/n/ryqq/search");
                    if (api.IsLoggedIn) req.Headers.TryAddWithoutValidation("Cookie", raw);
                    using var resp = await http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var code = doc.RootElement.TryGetProperty("req_1", out var r1) && r1.TryGetProperty("code", out var c)
                        ? c.GetInt32() : -1;
                    var n = 0;
                    if (code == 0 &&
                        doc.RootElement.TryGetProperty("req_1", out var r1b) && r1b.TryGetProperty("data", out var d) &&
                        d.TryGetProperty("body", out var b) && b.TryGetProperty("songlist", out var sl) &&
                        sl.TryGetProperty("list", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
                        n = list.GetArrayLength();
                    Console.WriteLine($"[qqsearch] 登录态歌单第{i}发: code={code} 数量={n}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[qqsearch] 登录态歌单第{i}发 FAIL: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return songs.Count > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqsearch] 生产路径 FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
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

    /// <summary>红心端到端探针(--qqlike):取"我喜欢"tid → 对一首未喜欢曲目 AddSonglist → 云端复检 →
    /// DelSonglist 恢复原状(结束状态与开始一致,零净副作用)。需要本机已存有效 QQ Cookie。
    /// 选曲规则:优先从公开每日30首歌单取首曲(不在用户喜欢集合内 → 实测 Add 路径),
    /// 失败再走对既有喜欢集合尾曲的 cancel→restore 路线。</summary>
    public static async Task<int> RunLikeProbeAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qqlike][FAIL] 本机无有效 QQ Cookie,无法实测红心写入");
            return 1;
        }
        var exit = 0;
        try
        {
            // 1) 读链路:定位"我喜欢" + 已喜欢集合
            await api.EnsureLikedIdsAsync();
            Console.WriteLine($"[qqlike] EnsureLikedIdsAsync 完成,CanToggleLike={api.CanToggleLike}");
            const long dailyTid = 7294917175; // 公开每日30首动态歌单(与 --qqapi 同源,匿名可读)
            var probeSong = (await api.GetPlaylistTracksAsync(dailyTid)).FirstOrDefault(s => s.Id != 0);
            if (probeSong is null)
            {
                Console.WriteLine("[qqlike][FAIL] 每日歌单无可选样例曲目");
                return 1;
            }
            Console.WriteLine($"[qqlike] 样例 #{probeSong.Id}/{probeSong.Mid} [{probeSong.Name} - {probeSong.Artist}] 当前红心={api.IsLiked(probeSong.Id)}(期望 False)");

            // 找到"我喜欢"tid(用于云端复检;仅供观察,校验以 QQMusicApiClient 内部同一路径为准)
            var likedPl = (await api.GetUserPlaylistsAsync()).FirstOrDefault(p => p.Name == "我喜欢");
            Console.WriteLine($"[qqlike] 我喜欢歌单 tid={likedPl?.Id.ToString() ?? "(未找到)"} 标称{likedPl?.TrackCount ?? -1}首");

            // 2) Add:应返回 true 且云端出现
            var added = await api.LikeToggleAsync(probeSong.Id);
            Console.WriteLine($"[qqlike] AddSonglist 返回={added}(期望 True),内存集合含它={api.IsLiked(probeSong.Id)}(期望 True)");
            var afterAdd = await api.GetPlaylistTracksAsync(likedPl!.Id);
            bool inCloud = afterAdd.Any(t => t.Id == probeSong.Id);
            Console.WriteLine($"[qqlike] 云端复检(Add 后): 曲目 {afterAdd.Count} 首, 含样例={inCloud}(期望 True)");

            // 3) Del:恢复原状
            var removed = await api.LikeToggleAsync(probeSong.Id);
            Console.WriteLine($"[qqlike] DelSonglist 返回={removed}(期望 False)");
            var afterDel = await api.GetPlaylistTracksAsync(likedPl.Id);
            bool goneCloud = !afterDel.Any(t => t.Id == probeSong.Id);
            Console.WriteLine($"[qqlike] 云端复检(Del 后): 曲目 {afterDel.Count} 首, 含样例={(!goneCloud)}(期望 False)");

            exit = added && inCloud && !removed && goneCloud ? 0 : 1;
            Console.WriteLine(exit == 0 ? "[qqlike] 全部通过(状态已还原)" : "[qqlike][FAIL] 校验未全过(请核对上面的期望标注)");
            if (!(added && inCloud)) exit |= 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqlike][FAIL] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            exit = 1;
        }
        return exit;
    }

    /// <summary>探针(--qqdumppl):dump 用户歌单两通道(自建/收藏)的原始 JSON,确认服务端字段名。</summary>
    public static async Task<int> RunDumpPlaylistsAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qqdumppl][FAIL] 本机无有效 QQ Cookie");
            return 1;
        }
        var raw = await api.DumpUserPlaylistsRawAsync();
        Console.WriteLine($"[qqdumppl] 用户歌单原始响应 {raw.Length} 字节,写入 $TEMP/qqdumppl.json");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qqdumppl.json"), raw);
        var detail = await api.DumpSonglistDetailRawAsync(2660913991); // 听歌吧
        Console.WriteLine($"[qqdumppl] 歌单详情原始响应 {detail.Length} 字节,写入 $TEMP/qqdissdetail.json");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qqdissdetail.json"), detail);

        // 批量播放量补拉实测:全部用户歌单的 tid 一次喂进去
        var pls = await api.GetUserPlaylistsAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var counts = await api.GetPlaylistPlayCountsAsync(pls.Select(p => p.Id));
        sw.Stop();
        Console.WriteLine($"[qqdumppl] 播放量补拉: {counts.Count}/{pls.Count} 个歌单拿到,耗时 {sw.ElapsedMilliseconds}ms");
        foreach (var p in pls)
            Console.WriteLine($"[qqdumppl]   [{p.Name}] listennum={counts.GetValueOrDefault(p.Id)}");
        return 0;
    }

    /// <summary>用户歌单逐个试读探针(--qqplaylist [tid]):无参列出全部歌单(id/dirId/标称曲数),
    /// 再对每个歌单调 GetPlaylistTrackPageAsync 读首页,逐个打印成功/失败与服务端错误码。
    /// 带 tid 参数时对指定歌单做 GetPlaylistTracksAsync 全量拉取计时(复现 UI 打开路径)。</summary>
    public static async Task<int> RunPlaylistProbeAsync(long? fullTid = null)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qqplaylist][FAIL] 本机无有效 QQ Cookie");
            return 1;
        }
        var exit = 0;
        try
        {
            if (fullTid is { } tid)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var total = 0;
                for (var begin = 0; ; begin += 300)
                {
                    var page = await api.GetPlaylistTrackPageAsync(tid, begin, 300);
                    Console.WriteLine($"[qqplaylist]   begin={begin}: 本页{page.Songs.Count}首 total={page.TotalCount} HasMore={page.HasMore} 首曲=[{page.Songs.FirstOrDefault()?.Name}] 尾曲=[{page.Songs.LastOrDefault()?.Name}]");
                    total += page.Songs.Count;
                    if (!page.HasMore) break;
                    if (begin > 10000) { Console.WriteLine("[qqplaylist]   超过 10 页,疑似死循环,中断"); break; }
                }
                sw.Stop();
                Console.WriteLine($"[qqplaylist] 全量 tid={tid}: 合计 {total} 首,耗时 {sw.ElapsedMilliseconds}ms");
                return 0;
            }

            var pls = await api.GetUserPlaylistsAsync();
            Console.WriteLine($"[qqplaylist] 用户歌单 {pls.Count} 个:");
            foreach (var p in pls)
                Console.WriteLine($"[qqplaylist]   id={p.Id} dirId={p.DirId} 名=[{p.Name}] 标称{p.TrackCount}首 可加歌={p.CanAddTracks}");

            foreach (var p in pls)
            {
                try
                {
                    var page = await api.GetPlaylistTrackPageAsync(p.Id, 0, 5);
                    Console.WriteLine($"[qqplaylist]   [{p.Name}] tid={p.Id}: OK 首页{page.Songs.Count}首 total={page.TotalCount} 首曲=[{page.Songs.FirstOrDefault()?.Name}]");
                }
                catch (Exception ex)
                {
                    var code = (ex as ApiException)?.Code;
                    Console.WriteLine($"[qqplaylist]   [{p.Name}] tid={p.Id}: FAIL {ex.GetType().Name} code={code} {ex.Message}");
                    exit = 1;
                }
            }
            Console.WriteLine(exit == 0 ? "[qqplaylist] 全部歌单可读" : "[qqplaylist] 存在读取失败的歌单");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqplaylist][FAIL] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            exit = 1;
        }
        return exit;
    }
}
