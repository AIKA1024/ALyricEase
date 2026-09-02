using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ALyricEase.Headless;

/// <summary>--pltime:实测歌单接口是否下发"最后修改"类时间字段。
/// 匿名拉公开歌单:网易云 /api/v6/playlist/detail(应用同款明文通道)、
/// QQ musicu uniform_get_Dissinfo(应用同款模块);递归打印所有含 time 的字段
/// 与值(毫秒时间戳换算本地时间),并打印 data 顶层键帮助确认结构。</summary>
public static class PlaylistTimeProbe
{
    private const string NetEaseId = "3778678"; // 云音乐热歌榜(公开,匿名可读)
    private const long QqTid = 7294917175;      // 官方每日30首动态歌单(公开,匿名可读,与 --qqapi 同源)

    public static async Task<int> RunAsync()
    {
        using var http = new HttpClient();
        var fail = 0;

        try
        {
            Console.WriteLine("== 网易云 /api/v6/playlist/detail (id=" + NetEaseId + ") ==");
            using var neReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://music.163.com/api/v6/playlist/detail?id={NetEaseId}");
            neReq.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            neReq.Headers.Referrer = new Uri("https://music.163.com");
            using var neResp = await http.SendAsync(neReq);
            var neBody = await neResp.Content.ReadAsStringAsync();
            using var neDoc = JsonDocument.Parse(neBody);
            Console.WriteLine($"  code={neDoc.RootElement.GetProperty("code").GetInt32()}");
            if (neDoc.RootElement.TryGetProperty("playlist", out var nePlaylist))
            {
                PrintTopKeys(nePlaylist, "playlist");
                PrintTimeFields(nePlaylist, "playlist", 0);
            }
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  网易云失败: " + ex.Message);
        }

        try
        {
            Console.WriteLine("\n== QQ musicu uniform_get_Dissinfo (disstid=" + QqTid + ") ==");
            var json = """
                {"req_0":{"module":"music.srfDissInfo.aiDissInfo","method":"uniform_get_Dissinfo",
                "param":{"disstid":7294917175,"userinfo":1,"tag":1,"orderlist":1,"song_begin":0,
                "song_num":1,"onlysonglist":0,"enc_host_uin":""}},
                "comm":{"uin":"","format":"json","ct":24,"cv":4747474,"g_tk":5381}}
                """;
            using var qqReq = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var qqResp = await http.SendAsync(qqReq);
            var qqBody = await qqResp.Content.ReadAsStringAsync();
            using var qqDoc = JsonDocument.Parse(qqBody);
            var req0 = qqDoc.RootElement.GetProperty("req_0");
            Console.WriteLine($"  code={req0.GetProperty("code").GetInt32()}");
            var data = req0.GetProperty("data");
            PrintTopKeys(data, "data");
            PrintTimeFields(data, "data", 0);
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  QQ 失败: " + ex.Message);
        }

        return fail;
    }

    private static void PrintTopKeys(JsonElement el, string path)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        var keys = el.EnumerateObject().Select(p => p.Name).Take(40).ToList();
        Console.WriteLine($"  [{path}] 顶层键: {string.Join(", ", keys)}");
    }

    /// <summary>递归打印名字含 time 的字段;数值型按毫秒时间戳换算本地时间。</summary>
    private static void PrintTimeFields(JsonElement el, string path, int depth)
    {
        if (depth > 4) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    var childPath = $"{path}.{p.Name}";
                    if (p.Name.Contains("time", StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String
                        && p.Value.ToString() is { } raw && raw.Length > 0)
                        Console.WriteLine($"  {childPath} = {raw}{FormatMs(raw)}");
                    PrintTimeFields(p.Value, childPath, depth + 1);
                }
                break;
            case JsonValueKind.Array when el.GetArrayLength() > 0:
                PrintTimeFields(el.EnumerateArray().First(), path + "[0]", depth + 1);
                break;
        }
    }

    private static string FormatMs(string raw)
    {
        if (!long.TryParse(raw, out var v)) return "";
        // 毫秒(>1e11)与秒(1e9~1e11)两种粒度都换算,QQ dirinfo.mtime 是秒级
        if (v > 100_000_000_000)
            return "  → " + DateTimeOffset.FromUnixTimeMilliseconds(v).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        if (v > 1_000_000_000)
            return "  → " + DateTimeOffset.FromUnixTimeSeconds(v).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        return "";
    }
}
