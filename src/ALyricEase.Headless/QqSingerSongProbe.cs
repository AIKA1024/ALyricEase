using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>
/// QQ 侧"关注歌手新歌"到底能不能凑出来 —— 联网只读探针（--qq-singer-songs）。
///
/// 要回答两件事：
///   ① <c>GetFollowSingerList</c> 的 <c>MID</c> 能不能直接喂给 <c>GetSingerSongList</c>；
///   ② <c>order</c> 哪个值是按发行时间倒序（1 是热度，项目歌手页一直用它；
///      若 2 才是时间，就能像网易云那样"关注列表 + 每位歌手最新一首"拼出新歌流）。
/// 输出每首歌的 <see cref="ALyricEase.Models.Song.PublishDate"/> 作为排序证据。
/// </summary>
internal static class QqSingerSongProbe
{
    public static async Task<int> RunAsync()
    {
        var api = ServiceLocator.Get<QQMusicApiClient>();
        var cookie = ReadQqCookie();
        if (string.IsNullOrEmpty(cookie))
        {
            Console.WriteLine("[qq-singer] SKIP 没找到已保存的 QQ Cookie");
            return 2;
        }

        api.SetCookie(cookie);
        Console.WriteLine($"[qq-singer] 登录={api.IsLoggedIn}");

        var page = await api.GetFollowSingersAsync("", 0, 6, CancellationToken.None);
        Console.WriteLine($"[qq-singer] 关注歌手 total={page.Total} 本页={page.Users.Count}");
        if (page.Users.Count == 0)
        {
            Console.WriteLine("[qq-singer] SKIP 关注列表为空");
            return 2;
        }

        var any = false;
        foreach (var singer in page.Users.Take(3))
        {
            foreach (var order in new[] { 1, 2 })
            {
                var songs = await api.GetArtistSongsAsync(singer.SingerMid, 3, CancellationToken.None, order);
                if (songs.Count > 0) any = true;
                var line = string.Join(" | ", songs.Select(s => $"{s.Name}({s.PublishDate})"));
                Console.WriteLine($"[qq-singer] {singer.Nickname} order={order} n={songs.Count} {line}");
            }
        }

        Console.WriteLine(any
            ? "[qq-singer] PASS 关注歌手 MID 可直接查歌"
            : "[qq-singer] FAIL 查不到任何歌曲(检查 MID 或 comm)");
        return any ? 0 : 1;
    }

    /// <summary>从应用配置里取 QQ Cookie 原文（与 <c>CookieStore</c> 同一个文件）。</summary>
    private static string ReadQqCookie()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALyricEase.Data", "config", "cookie.json");
        if (!File.Exists(path)) return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("QQCookieRaw", out var value)
                ? value.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
