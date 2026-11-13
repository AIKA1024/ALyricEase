using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Infrastructure;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>
/// QQ 音乐有没有"私人FM"对标物 —— 联网只读探针(--qq-fm)。
///
/// 网易云那边一条 <c>/api/v1/radio/get</c> 就是无限流;QQ 侧公开资料里至少有三条候选通道
/// (新版 radio 模块的 get_radio_track / 老 pf.radiosvr 的 GetRadiosonglist / 雷达流
/// GetRadarSong 的其它 ReqType)。本探针把候选形状各打一次,只看**回包里有没有成批的曲目 mid**,
/// 并连打两次同一形状看是否"每批都不一样"(私人FM 的前提)。
/// </summary>
internal static class QqPersonalFmProbe
{
    private sealed record Shape(string Label, string Module, string Method, Action<Utf8JsonWriter> Param);

    public static async Task<int> RunAsync()
    {
        var api = ServiceLocator.Get<QQMusicApiClient>();
        var cookie = ReadQqCookie();
        if (!string.IsNullOrEmpty(cookie)) api.SetCookie(cookie);
        Console.WriteLine($"[qq-fm] 登录={api.IsLoggedIn}");

        var shapes = new[]
        {
            new Shape("radio.RadioServer get_radio_track radioId=99", "music.radio.RadioServer", "get_radio_track", p =>
            {
                p.WriteNumber("radioId", 99);
                p.WriteNumber("num", 10);
                p.WriteNumber("page", 1);
            }),
            new Shape("radio.RadioServer get_radio_track 无 id", "music.radio.RadioServer", "get_radio_track", p =>
            {
                p.WriteNumber("num", 10);
            }),
            new Shape("pf.radiosvr GetRadiosonglist id=99", "pf.radiosvr", "GetRadiosonglist", p =>
            {
                p.WriteNumber("id", 99);
                p.WriteNumber("firstplay", 1);
                p.WriteNumber("num", 10);
            }),
            new Shape("TrackRelation GetRadarSong ReqType=1", "music.recommend.TrackRelationServer", "GetRadarSong", p =>
            {
                p.WriteNumber("Page", 1);
                p.WriteNumber("ReqType", 1);
                p.WriteStartArray("FavSongs"); p.WriteEndArray();
                p.WriteStartArray("EntranceSongs"); p.WriteEndArray();
            }),
            new Shape("TrackRelation GetRadarSong ReqType=3", "music.recommend.TrackRelationServer", "GetRadarSong", p =>
            {
                p.WriteNumber("Page", 1);
                p.WriteNumber("ReqType", 3);
                p.WriteStartArray("FavSongs"); p.WriteEndArray();
                p.WriteStartArray("EntranceSongs"); p.WriteEndArray();
            }),
        };

        var hits = new List<Shape>();
        foreach (var shape in shapes)
        {
            var tracks = await FetchAsync(api, shape);
            if (tracks is null) continue;
            Console.WriteLine($"[qq-fm] {shape.Label} → 曲目={tracks.Count}");
            foreach (var t in tracks.Take(5)) Console.WriteLine($"[qq-fm]    {t}");
            if (tracks.Count > 0) hits.Add(shape);
        }

        if (hits.Count == 0)
        {
            Console.WriteLine("[qq-fm] FAIL 五条候选都没回曲目 —— QQ 私人FM 需要抓客户端包再补形状");
            return 1;
        }

        // 私人FM 的前提:同一形状连打两次应当是两批不同的歌(网易云 /radio/get 就是这样)
        var best = hits[0];
        var first = await FetchAsync(api, best);
        var second = await FetchAsync(api, best);
        var a = first ?? [];
        var b = second ?? [];
        var overlap = a.Select(Key).Intersect(b.Select(Key), StringComparer.Ordinal).Count();
        Console.WriteLine($"[qq-fm] 连打两次({best.Label}): 第一批={a.Count} 第二批={b.Count} 重叠={overlap}");
        Console.WriteLine($"[qq-fm]   第一批首={First(a)}");
        Console.WriteLine($"[qq-fm]   第二批首={First(b)}");

        var infinite = a.Count > 0 && b.Count > 0 && overlap < Math.Min(a.Count, b.Count);
        Console.WriteLine(infinite
            ? "[qq-fm] PASS 可用作无限流(每批都是新的歌)"
            : "[qq-fm] WARN 能回曲目但两批高度重复,做无限流需要换形状/加游标");
        return infinite ? 0 : 1;
    }

    /// <summary>打一个形状;回 null 表示请求本身失败(业务码非 0 / 异常),回空列表表示成功但没歌。</summary>
    private static async Task<List<string>?> FetchAsync(QQMusicApiClient api, Shape shape)
    {
        try
        {
            using var doc = await api.ProbeModuleAsync(
                shape.Module, shape.Method, shape.Param, CancellationToken.None);
            var root = doc.RootElement;
            var code = ReadReqCode(root);
            var tracks = CollectTracks(root);
            if (code != 0)
            {
                Console.WriteLine($"[qq-fm] {shape.Label} → code={code} 原始={Preview(root)}");
                return null;
            }
            return tracks;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qq-fm] {shape.Label} 异常 {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static int ReadReqCode(JsonElement root)
    {
        if (!root.TryGetProperty("req_0", out var req)) return -999;
        if (!req.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number) return -998;
        return code.GetInt32();
    }

    /// <summary>递归收集"看起来像曲目"的对象:必须带非空 mid(QQ 曲目的稳定标识)。
    /// 只要名字/数字 id 的对象不算,避免把货架卡、电台条目误判成歌。</summary>
    private static List<string> CollectTracks(JsonElement root)
    {
        var found = new List<string>();
        Walk(root, found);
        return found;
    }

    private static void Walk(JsonElement el, List<string> found)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryReadTrack(el) is { } track)
                {
                    found.Add(track);
                    return;
                }
                foreach (var p in el.EnumerateObject()) Walk(p.Value, found);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Walk(item, found);
                break;
        }
    }

    private static string? TryReadTrack(JsonElement obj)
    {
        var mid = ReadString(obj, "mid") ?? ReadString(obj, "songmid") ?? ReadString(obj, "songMid");
        if (string.IsNullOrEmpty(mid)) return null;
        var name = ReadString(obj, "name") ?? ReadString(obj, "title")
                   ?? ReadString(obj, "songname") ?? ReadString(obj, "songName") ?? "(无名)";
        return $"{name} [{mid}]";
    }

    private static string? ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Key(string track) => track;

    private static string First(List<string> list) => list.Count > 0 ? list[0] : "(空)";

    private static string Preview(JsonElement root)
    {
        var raw = root.GetRawText();
        return raw.Length <= 260 ? raw : raw[..260] + "…";
    }

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
