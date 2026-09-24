using System;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Headless;

/// <summary>推荐歌单创建者验证探针(--necreator [id]):拉个性化推荐歌单(或指定 id),
/// 逐个打 v6 概览,打印 API 返回的真实创建者昵称与 DTO 映射结果。
/// 定位"推荐歌单详情页创建者显示成自己"是数据缺失还是展示层写死。</summary>
public static class NetEaseCreatorProbe
{
    public static async Task<int> RunAsync(long? onlyId)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var api = new NetEaseApiClient(new ALyricEase.Services.Crypto.CryptoService(), new CnIpPool(), new CookieStore());

        if (onlyId is { } id)
        {
            return await DumpAsync(api, id, "(指定)");
        }

        var playlists = await api.GetPersonalizedPlaylistsAsync(6);
        if (playlists.Count == 0)
        {
            Console.WriteLine("[necreator][FAIL] 个性化推荐歌单为空(登录态失效或接口被拦)");
            return 1;
        }

        var fail = 0;
        foreach (var p in playlists)
            fail += await DumpAsync(api, p.Id, p.Title);
        return fail;
    }

    private static async Task<int> DumpAsync(NetEaseApiClient api, long id, string title)
    {
        try
        {
            var overview = await api.GetPlaylistTrackOverviewAsync(id);
            var creator = overview.CreatorNickname;
            Console.WriteLine($"[necreator] {title} (id={id}): trackIds={overview.TrackIds.Count} " +
                              $"CreatorNickname='{(creator.Length == 0 ? "(空!)" : creator)}'");
            return creator.Length == 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[necreator] {title} (id={id}): 请求失败 {ex.Message}");
            return 1;
        }
    }
}
