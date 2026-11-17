using System;
using System.Collections.Generic;
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
    /// <summary>匿名只读验证《融雪铭春》的详情及下载音质查询，不获取音频。</summary>
    public static async Task<int> RunQualityProbeAsync()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aly-qq-quality-{Guid.NewGuid():N}");
        var api = new QQMusicApiClient(new CookieStore(System.IO.Path.Combine(root, "cookie.json")));
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        var song = await api.GetSongDetailAsync(660493677, timeout.Token)
            ?? throw new InvalidOperationException("歌曲详情为空");
        using var http = new System.Net.Http.HttpClient();
        var service = new SongDownloadService(new MusicApiProvider([api]),
            new MusicCacheService(128, System.IO.Path.Combine(root, "cache"), http),
            new DirectorySongDownloadStorage(() => System.IO.Path.Combine(root, "downloads")));
        foreach (var candidate in new[] { song, new Song { Source = MusicSource.QQ, Id = song.Id, Mid = song.Mid } })
        {
            var qualities = await service.GetAvailableQualitiesAsync(candidate, timeout.Token);
            if (qualities is null || !qualities.Select(q => q.Level)
                    .SequenceEqual(["standard", "higher", "lossless", "hires"]))
                throw new InvalidOperationException("详情或缺失元数据补查未返回完整四档");
        }
        Console.WriteLine($"PASS: {song.Name} - {song.Artist} ({song.Id}/{song.Mid}): standard, higher, lossless, hires; detail and download fallback.");
        return 0;
    }

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
            Console.WriteLine($"[qqsearch] 生产 SearchAllAsync: 歌曲 {all?.Songs.Count ?? -1} 首 / 歌单 {all?.Playlists.Count ?? -1} 个"
                + $" / 歌手 {all?.Artists.Count ?? -1} / 专辑 {all?.Albums.Count ?? -1} / 用户 {all?.Users.Count ?? -1}");
            foreach (var (kind, label) in new[]
                     {
                         (SearchKind.Album, "专辑"), (SearchKind.Artist, "歌手"),
                         (SearchKind.Playlist, "歌单"), (SearchKind.User, "用户"),
                     })
            {
                try
                {
                    var r = await api.SearchAllAsync("任然", kind, 10);
                    var n = kind switch
                    {
                        SearchKind.Album => r?.Albums.Count ?? -1,
                        SearchKind.Artist => r?.Artists.Count ?? -1,
                        SearchKind.Playlist => r?.Playlists.Count ?? -1,
                        _ => r?.Users.Count ?? -1,
                    };
                    Console.WriteLine($"[qqsearch] {label}Tab: {n} 条");
                }
                catch (ApiException ex)
                {
                    Console.WriteLine($"[qqsearch] {label}Tab FAIL code={ex.Code}: {ex.Message}");
                }
            }
            // 他人主页(网页 /n/ryqq_v2/profile 同源):userid 直接收 encrypt_uin
            foreach (var euin in new[] { "owvP7KnP7e6q", "7eoP7e4koi4qNn**" })
            {
                try
                {
                    var page = await api.GetUserHomepageAsync(euin);
                    Console.WriteLine($"[qqsearch] 他人主页 {euin}: nick=[{page.Nickname}] 关注={page.Follows} " +
                        $"粉丝={page.Followeds} 喜欢=[{page.Liked?.Name}] 自建={page.Created.Count} 收藏={page.Collected.Count}");
                }
                catch (ApiException ex)
                {
                    Console.WriteLine($"[qqsearch] 他人主页 {euin} FAIL code={ex.Code}: {ex.Message}");
                }
            }
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

    /// <summary>QQ 歌单"失效条目"探针(--qqdiss &lt;tid&gt;):官方 songlist 会把失效/下架条目剔掉
    /// (标称 1082 只回 1077),但官方客户端里这类歌往往仍带着"已下架/无版权"标记置灰显示。
    /// 这里把几个候选通道都打一遍,看有没有哪个能拿到完整条目 —— 决定我们该隐藏还是显示+禁用。</summary>
    public static async Task<int> RunDissVariantsProbeAsync(long tid)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qqdiss][FAIL] 本机无有效 QQ Cookie");
            return 1;
        }

        async Task TryAsync(string tag, string module, string method, Action<System.Text.Json.Utf8JsonWriter> write)
        {
            try
            {
                using var doc = await api.ProbeModuleAsync(module, method, write);
                var root = doc.RootElement;
                if (!root.TryGetProperty("req_0", out var req) || req.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    Console.WriteLine($"[qqdiss] {tag}: 响应没有 req_0");
                    return;
                }
                var code = req.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? codeEl.GetInt64()
                    : -1;
                if (!req.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    Console.WriteLine($"[qqdiss] {tag}: code={code} 无 data 段");
                    return;
                }
                var total = data.TryGetProperty("total_song_num", out var totalEl)
                            && totalEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? totalEl.GetInt64()
                    : -1;
                var count = -1;
                string first = "", last = "";
                if (data.TryGetProperty("songlist", out var songlist)
                    && songlist.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    count = songlist.GetArrayLength();
                    if (count > 0)
                    {
                        first = TrackName(songlist[0]);
                        last = TrackName(songlist[count - 1]);
                    }
                }
                Console.WriteLine($"[qqdiss] {tag}: code={code} total={total} songlist={count} 首=[{first}] 尾=[{last}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qqdiss] {tag}: FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 1) 生产通道,一次要全部(看看是不是"一页只给 300"之外还剔条目)
        await TryAsync("uniform 全量(2000)", "music.srfDissInfo.aiDissInfo", "uniform_get_Dissinfo", p =>
        {
            p.WriteNumber("disstid", tid);
            p.WriteNumber("userinfo", 1);
            p.WriteNumber("tag", 1);
            p.WriteNumber("orderlist", 1);
            p.WriteNumber("song_begin", 0);
            p.WriteNumber("song_num", 2000);
            p.WriteNumber("onlysonglist", 0);
            p.WriteString("enc_host_uin", "");
        });

        // 2) onlysonglist=1:只要歌单条目,不要 dirinfo(部分实现走不同取数分支)
        await TryAsync("uniform onlysonglist", "music.srfDissInfo.aiDissInfo", "uniform_get_Dissinfo", p =>
        {
            p.WriteNumber("disstid", tid);
            p.WriteNumber("userinfo", 1);
            p.WriteNumber("tag", 1);
            p.WriteNumber("orderlist", 1);
            p.WriteNumber("song_begin", 0);
            p.WriteNumber("song_num", 300);
            p.WriteNumber("onlysonglist", 1);
            p.WriteString("enc_host_uin", "");
        });

        // 3) 老 CgiGetDiss:注释里记录过"缺 userinfo/tag/orderlist 会空",这里参数给全再试
        await TryAsync("CgiGetDiss 全量", "music.srfDissInfo.DissInfo", "CgiGetDiss", p =>
        {
            p.WriteNumber("disstid", tid);
            p.WriteNumber("dirid", 0);
            p.WriteNumber("tag", 1);
            p.WriteNumber("song_begin", 0);
            p.WriteNumber("song_num", 2000);
            p.WriteNumber("userinfo", 1);
            p.WriteNumber("orderlist", 1);
            p.WriteNumber("onlysonglist", 0);
        });

        // 4) CgiGetDiss 单页对照
        await TryAsync("CgiGetDiss 300", "music.srfDissInfo.DissInfo", "CgiGetDiss", p =>
        {
            p.WriteNumber("disstid", tid);
            p.WriteNumber("dirid", 0);
            p.WriteNumber("tag", 1);
            p.WriteNumber("song_begin", 0);
            p.WriteNumber("song_num", 300);
            p.WriteNumber("userinfo", 1);
            p.WriteNumber("orderlist", 1);
            p.WriteNumber("onlysonglist", 0);
        });

        // 5) Web 端另一条详情通道(歌单页 SSR 数据用):给不到就当没有
        await TryAsync("PlaylistInfoServer", "music.playlist.PlaylistInfoServer", "GetDetail", p =>
        {
            p.WriteNumber("disstid", tid);
            p.WriteNumber("song_begin", 0);
            p.WriteNumber("song_num", 300);
        });

        // 6) 缺的条目到底是"内容层面不存在"还是"每页按请求切片后过滤"?前者怎么切都少,
        //    后者把页切小就能把它们捞回来(那样才谈得上显示+禁用)。
        var chunkTotal = 0;
        var chunkSizes = new List<string>();
        for (var begin = 0; begin < 300; begin += 15)
        {
            var page = await api.GetPlaylistTrackPageAsync(tid, begin, 15);
            chunkTotal += page.Songs.Count;
            if (page.Songs.Count != 15) chunkSizes.Add($"{begin}:{page.Songs.Count}");
        }
        Console.WriteLine($"[qqdiss] 前 300 位按 15 条切页: 合计 {chunkTotal} 条" +
                          (chunkSizes.Count > 0 ? $"; 不足 15 的位置 [{string.Join(',', chunkSizes)}]" : "; 每页都是满的"));

        return 0;
    }

    private static string TrackName(System.Text.Json.JsonElement track)
    {
        if (track.ValueKind != System.Text.Json.JsonValueKind.Object) return "?";
        foreach (var key in new[] { "name", "songname", "title", "songName" })
            if (track.TryGetProperty(key, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String)
                return value.GetString() ?? "";
        return "?";
    }

    /// <summary>QQ 歌单分页语义探针(--qqpage &lt;tid&gt; [num]):同一歌单按两种推进方式各拉一遍——
    /// A 用"本页实际返回条数"推进 song_begin,B 用"请求条数"推进。比较两组的曲目集合/重复,
    /// 判定服务端把 song_begin 当原始列表下标还是过滤后可见列表下标(2026-11 修"QQ 歌单只加载一页")。</summary>
    public static async Task<int> RunPlaylistPageProbeAsync(long tid, int num = 300)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qqpage][FAIL] 本机无有效 QQ Cookie");
            return 1;
        }

        async Task<(List<long> Ids, List<int> Sizes, long Total)> PullByCountAsync()
        {
            var ids = new List<long>();
            var sizes = new List<int>();
            var total = 0L;
            for (var begin = 0; begin < 20000; )
            {
                var page = await api.GetPlaylistTrackPageAsync(tid, begin, num);
                sizes.Add(page.Songs.Count);
                total = page.TotalCount;
                foreach (var s in page.Songs) if (s.Id != 0) ids.Add(s.Id);
                Console.WriteLine($"[qqpage][A] begin={begin} 本页={page.Songs.Count} total={page.TotalCount} HasMore={page.HasMore} 首=[{page.Songs.FirstOrDefault()?.Name}] 尾=[{page.Songs.LastOrDefault()?.Name}]");
                if (page.Songs.Count == 0) break;
                begin += page.Songs.Count;
            }
            return (ids, sizes, total);
        }

        async Task<(List<long> Ids, List<int> Sizes, long Total)> PullByRequestAsync()
        {
            var ids = new List<long>();
            var sizes = new List<int>();
            var total = 0L;
            for (var begin = 0; begin < 20000; begin += num)
            {
                var page = await api.GetPlaylistTrackPageAsync(tid, begin, num);
                sizes.Add(page.Songs.Count);
                total = page.TotalCount;
                foreach (var s in page.Songs) if (s.Id != 0) ids.Add(s.Id);
                Console.WriteLine($"[qqpage][B] begin={begin} 本页={page.Songs.Count} total={page.TotalCount} HasMore={page.HasMore} 首=[{page.Songs.FirstOrDefault()?.Name}] 尾=[{page.Songs.LastOrDefault()?.Name}]");
                if (page.Songs.Count == 0) break;
            }
            return (ids, sizes, total);
        }

        try
        {
            var a = await PullByCountAsync();
            var b = await PullByRequestAsync();
            Console.WriteLine($"[qqpage] A(按返回条数推进): 行数={a.Ids.Count} 去重={a.Ids.Distinct().Count()} 页={a.Sizes.Count} 各页={string.Join('/', a.Sizes)}");
            Console.WriteLine($"[qqpage] B(按请求条数推进): 行数={b.Ids.Count} 去重={b.Ids.Distinct().Count()} 页={b.Sizes.Count} 各页={string.Join('/', b.Sizes)}");
            var onlyA = a.Ids.Distinct().Except(b.Ids.Distinct()).Count();
            var onlyB = b.Ids.Distinct().Except(a.Ids.Distinct()).Count();
            Console.WriteLine($"[qqpage] 仅 A 有={onlyA} 仅 B 有={onlyB} 交集={a.Ids.Distinct().Intersect(b.Ids.Distinct()).Count()} 标称={a.Total}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqpage][FAIL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
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

    /// <summary>指定用户主页歌单逐个试读(--qq-user-pl [昵称|euin]):省略/给昵称时在本账号粉丝名单里
    /// 找人。打印主页三段歌单(喜欢 / 自建 / 收藏)的 id+dirId+标称曲数,再对每个歌单读首页,
    /// 逐个打印成败与服务端错误码 —— 用于定位"某个歌单点进去加载不出歌曲"。
    /// 末尾 dump 主页原始响应与首个疑似隐私歌单的详情,确认 mydiss 是否自带隐私标记。</summary>
    public static async Task<int> RunUserPlaylistsProbeAsync(string? who)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qq-user-pl][FAIL] 本机无有效 QQ Cookie");
            return 1;
        }

        var euin = "";
        if (!string.IsNullOrWhiteSpace(who))
        {
            // 先当昵称：在自己粉丝名单里找（粉丝总数一般很小，一页足够）
            var fans = await api.GetFansAsync("", 0, 100);
            Console.WriteLine($"[qq-user-pl] 自己的粉丝 n={fans.Users.Count} total={fans.Total}");
            var hit = fans.Users.FirstOrDefault(u =>
                u.Nickname.Contains(who, StringComparison.OrdinalIgnoreCase));
            euin = hit?.EncryptUin ?? (who.Length >= 10 ? who : "");
            if (hit is null && euin.Length == 0)
            {
                Console.WriteLine($"[qq-user-pl][FAIL] 粉丝名单里没找到昵称含 \"{who}\" 的用户");
                return 1;
            }
        }

        var homeRaw = await api.DumpUserHomepageRawAsync(euin);
        var homePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qq-user-home.json");
        System.IO.File.WriteAllText(homePath, homeRaw);
        Console.WriteLine($"[qq-user-pl] 主页原始响应 {homeRaw.Length}B → {homePath}");

        var home = await api.GetUserHomepageAsync(euin);
        Console.WriteLine($"[qq-user-pl] 用户={home.Nickname} 关注={home.Follows} 粉丝={home.Followeds}");

        var all = new List<(string Group, Playlist P)>();
        if (home.Liked is { } liked) all.Add(("喜欢", liked));
        all.AddRange(home.Created.Select(p => ("自建", p)));
        all.AddRange(home.Collected.Select(p => ("收藏", p)));

        Console.WriteLine($"[qq-user-pl] 歌单 {all.Count} 个:");
        var exit = 0;
        foreach (var (group, p) in all)
        {
            // 直接看服务端 dirinfo 的可见性标记(dir_show=2 即隐私),与曲目读取结果对照
            var raw = await api.DumpSonglistDetailRawAsync(p.Id);
            var flag = "?";
            using (var doc = System.Text.Json.JsonDocument.Parse(raw))
            {
                if (doc.RootElement.TryGetProperty("req_0", out var req)
                    && req.TryGetProperty("data", out var data)
                    && data.TryGetProperty("dirinfo", out var dir))
                {
                    var show = dir.TryGetProperty("dir_show", out var s) ? s.GetInt32() : 0;
                    var title = dir.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var songNum = dir.TryGetProperty("songnum", out var n) ? n.GetInt64() : 0;
                    var list = data.TryGetProperty("songlist", out var l) ? l.GetArrayLength() : -1;
                    var total = data.TryGetProperty("total_song_num", out var ts) ? ts.GetInt64() : 0;
                    flag = $"dir_show={show} songnum={songNum} list={list} total={total}" +
                           (title is { Length: > 0 } ? $" title=[{title}]" : "");
                }
            }

            try
            {
                var page = await api.GetPlaylistTrackPageAsync(p.Id, 0, 5);
                Console.WriteLine($"[qq-user-pl]   [{group}] 名=[{p.Name}] id={p.Id} dirId={p.DirId} " +
                                  $"标称{p.TrackCount}首 {flag} → OK 首页{page.Songs.Count}首");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qq-user-pl]   [{group}] 名=[{p.Name}] id={p.Id} dirId={p.DirId} " +
                                  $"标称{p.TrackCount}首 {flag} → FAIL {ex.Message}");
                exit = 1;
            }
        }
        return exit;
    }
}
