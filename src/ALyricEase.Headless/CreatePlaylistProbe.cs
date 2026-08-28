using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>创建/删除歌单端到端探针(--createpl):需本机有效登录 Cookie。
/// 两平台各建一个带时间戳的"ALyricEase探针"隐私歌单 → 复检用户歌单列表包含 → 删除 →
/// 复检已移除,账号还原。QQ 删除参数用创建响应里的资产目录 dirId(与网易云的 pid 语义不同)。</summary>
public static class CreatePlaylistProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var stamp = DateTime.Now.ToString("MMdd-HHmmss");
        var exit = await NetEaseAsync(stamp);
        exit |= await QQAsync(stamp);
        return exit;
    }

    private static async Task<int> NetEaseAsync(string stamp)
    {
        var name = $"ALyricEase探针{stamp}";
        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[ne-create] 未配置 MUSIC_U,跳过网易云创建探针");
            return 0;
        }

        Console.WriteLine($"[ne-create] 创建隐私歌单:[{name}]");
        try
        {
            var sw = Stopwatch.StartNew();
            var created = await api.CreatePlaylistAsync(name, isPrivate: true);
            Console.WriteLine($"[ne-create] 创建成功 id={created.Id}({sw.ElapsedMilliseconds}ms)");

            var uid = (await api.GetUserProfileAsync()).UserId;
            var ok = (await api.GetUserPlaylistsAsync(uid)).Any(p => p.Id == created.Id);
            Console.WriteLine(ok
                ? "[ne-create] 复检:用户歌单列表已包含新歌单"
                : "[ne-create][FAIL] 用户歌单列表未见新歌单");
            var deleted = false;
            try
            {
                await api.DeletePlaylistAsync(created.Id);
                deleted = true;
                Console.WriteLine("[ne-create] 已删除还原");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ne-create][WARN] 删除失败(账号残留测试歌单 id={created.Id}): {ex.Message}");
            }

            if (!ok || !deleted) return 1;
            var left = (await api.GetUserPlaylistsAsync(uid)).Any(p => p.Id == created.Id);
            Console.WriteLine(left ? "[ne-create][FAIL] 删除后仍可见" : "[ne-create] 复检:歌单已移除,账号还原");
            return left ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ne-create][FAIL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> QQAsync(string stamp)
    {
        var name = $"ALyricEase探针{stamp}";
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qq-create] 未配置 QQ Cookie,跳过 QQ 创建探针");
            return 0;
        }

        Console.WriteLine($"[qq-create] 创建歌单:[{name}]");
        try
        {
            var sw = Stopwatch.StartNew();
            var created = await api.CreatePlaylistAsync(name);
            Console.WriteLine($"[qq-create] 创建成功 tid={created.Id} dirId={created.DirId}({sw.ElapsedMilliseconds}ms)");

            var playlists = await api.GetUserPlaylistsAsync();
            var ok = playlists.Any(p => p.Id == created.Id);
            Console.WriteLine(ok
                ? "[qq-create] 复检:用户歌单列表已包含新歌单"
                : "[qq-create][FAIL] 用户歌单列表未见新歌单");

            var delKey = created.DirId != 0 ? created.DirId : created.Id;
            var deleted = false;
            try
            {
                await api.DeletePlaylistAsync(delKey);
                deleted = true;
                Console.WriteLine($"[qq-create] 已删除还原(dirId={delKey})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[qq-create][WARN] 删除失败(账号残留测试歌单 tid={created.Id}): {ex.Message}");
            }

            if (!ok || !deleted) return 1;
            var left = (await api.GetUserPlaylistsAsync()).Any(p => p.Id == created.Id);
            Console.WriteLine(left ? "[qq-create][FAIL] 删除后仍可见" : "[qq-create] 复检:歌单已移除,账号还原");
            return left ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qq-create][FAIL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
