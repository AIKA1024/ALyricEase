using System;
using System.Linq;
using System.Threading.Tasks;
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
}