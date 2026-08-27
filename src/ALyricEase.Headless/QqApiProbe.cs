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
}
