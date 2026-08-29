using System;
using System.Linq;
using System.Threading.Tasks;
using ALyricEase.Services;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>重命名歌单端到端探针(--renamepl):走生产代码路径(IUserMusicApi.RenamePlaylistAsync)
/// 验证两平台。各建一个带时间戳的测试歌单 → 从用户列表取权威歌单对象(含 QQ 资产目录 dirId)→
/// 重命名 → 复检列表名字已变 → 删除 → 复检已移除,账号还原。分享链接拼装(PlaylistShareLinks)
/// 一并打印留档。</summary>
public static class RenamePlaylistProbe
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
        var api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[ne-rename] 未配置 MUSIC_U,跳过网易云重命名探针");
            return 0;
        }

        var name = $"ALE改名{stamp}";
        var newName = $"ALE已改名{stamp}";
        Console.WriteLine($"[ne-rename] 创建歌单:[{name}]");
        try
        {
            var created = await api.CreatePlaylistAsync(name, isPrivate: true);
            Console.WriteLine($"[ne-rename] 创建成功 id={created.Id} 分享链接={PlaylistShareLinks.For(created)}");

            var uid = (await api.GetUserProfileAsync()).UserId;
            var target = (await api.GetUserPlaylistsAsync(uid)).First(p => p.Id == created.Id);

            await api.RenamePlaylistAsync(target, newName);
            var got = (await api.GetUserPlaylistsAsync(uid)).FirstOrDefault(p => p.Id == created.Id)?.Name;
            Console.WriteLine(got == newName
                ? "[ne-rename] 复检:名字已变,命中 ✓"
                : $"[ne-rename][FAIL] 复检名字=[{got}] (期望[{newName}])");

            await api.DeletePlaylistAsync(created.Id);
            var left = (await api.GetUserPlaylistsAsync(uid)).Any(p => p.Id == created.Id);
            Console.WriteLine(left ? "[ne-rename][FAIL] 删除后仍可见" : "[ne-rename] 已删除还原,账号干净");
            return got == newName && !left ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ne-rename][FAIL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> QQAsync(string stamp)
    {
        var api = new QQMusicApiClient(new CookieStore());
        if (!api.IsLoggedIn)
        {
            Console.WriteLine("[qq-rename] 未配置 QQ Cookie,跳过 QQ 重命名探针");
            return 0;
        }

        var name = $"ALE改名{stamp}";
        var newName = $"ALE已改名{stamp}";
        Console.WriteLine($"[qq-rename] 创建歌单:[{name}]");
        try
        {
            var created = await api.CreatePlaylistAsync(name);
            Console.WriteLine($"[qq-rename] 创建成功 tid={created.Id} dirId={created.DirId} 分享链接={PlaylistShareLinks.For(created)}");

            // 侧栏行来自用户歌单列表,与生产一致:取列表侧权威对象(dirId 语义以列表为准)
            var target = (await api.GetUserPlaylistsAsync()).First(p => p.Id == created.Id);

            await api.RenamePlaylistAsync(target, newName);
            var got = (await api.GetUserPlaylistsAsync()).FirstOrDefault(p => p.Id == created.Id)?.Name;
            Console.WriteLine(got == newName
                ? "[qq-rename] 复检:名字已变,命中 ✓"
                : $"[qq-rename][FAIL] 复检名字=[{got}] (期望[{newName}])");

            var delId = target.DirId != 0 ? target.DirId : target.Id;
            await api.DeletePlaylistAsync(delId);
            var left = (await api.GetUserPlaylistsAsync()).Any(p => p.Id == created.Id);
            Console.WriteLine(left ? "[qq-rename][FAIL] 删除后仍可见" : "[qq-rename] 已删除还原,账号干净");
            return got == newName && !left ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qq-rename][FAIL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
