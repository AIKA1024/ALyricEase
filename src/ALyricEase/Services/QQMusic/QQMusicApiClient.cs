using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Services.QQMusic;

/// <summary>QQ 音乐 API 客户端:u.y.qq.com musicu.fcg(JSON POST,一次一模块)+ c.y.qq.com fcg 明文 GET。
/// 上游协议参考开源 qq-music-api(Koa 版)。支持 Cookie 登录(网页版 QQ 音乐的 uin + qqmusic_key,
/// SetCookie 粘贴整段 cookie 即可):登录后 VIP/320k 可播;匿名仅免费歌 128k。失败抛 ApiException。</summary>
public sealed class QQMusicApiClient : IMusicApi
{
    private const string MusicuUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string CBase = "https://c.y.qq.com";
    private const string PlayerReferer = "https://y.qq.com/portal/player.html";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    /// <summary>专辑封面模板(albummid 填充;尺寸段 R300x300 由 CoverLoader 按需改写)。</summary>
    private const string CoverTemplate = "https://y.gtimg.cn/music/photo_new/T002R300x300M000{0}.jpg";

    private readonly HttpClient _http;
    private readonly Auth.CookieStore _cookie;

    /// <summary>vkey 请求 guid(会话内随机数即可,与播放器侧保持一致)。</summary>
    private readonly string _guid =
        ((Random.Shared.NextInt64(int.MaxValue) * Environment.TickCount64) % 1_000_000_000).ToString();

    /// <summary>登录用户数字 uin("0" = 匿名;cookie 里形如 o123456,去前缀 o)。</summary>
    private string _uin = "0";

    /// <summary>登录密钥 qqmusic_key 的值(vkey 请求 authst 参数 + Cookie 头)。</summary>
    private string _authst = "";

    /// <summary>登录态下随请求发送的完整 Cookie 头原文。</summary>
    private string _cookieHeader = "";

    /// <summary>数字 id → songmid 解析缓存(搜索/详情响应顺带填充;播放/歌词按 mid 取地址)。</summary>
    private readonly ConcurrentDictionary<long, string> _midById = new();

    public QQMusicApiClient(Auth.CookieStore cookie)
    {
        _cookie = cookie;
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false, // 手动管理 Cookie 头(跨域域不匹配,自动容器不可靠)
        });
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri(PlayerReferer);
        RestoreCookie();
    }

    public MusicSource Source => MusicSource.QQ;

    public string DisplayName => "QQ音乐";

    /// <summary>是否已设置有效 QQ 音乐登录 Cookie(uin + qqmusic_key 齐全)。</summary>
    public bool IsLoggedIn => _uin != "0" && _authst.Length > 0;

    /// <summary>粘贴 QQ 音乐 Cookie 登录:解析 uin 与 qqmusic_key(容忍贴整段 cookie),
    /// 校验齐全后持久化。格式不对抛 ApiException。</summary>
    public void SetCookie(string rawCookie)
    {
        rawCookie = rawCookie.Trim();
        if (rawCookie.Length == 0) throw new ApiException("QQ Cookie 为空", -1);

        var uin = ExtractCookieValue(rawCookie, "uin");
        if (uin is null || !uin.TrimStart('o').All(char.IsDigit) || uin.TrimStart('o').Length == 0)
            throw new ApiException("Cookie 中未找到有效的 uin", -1);
        var key = ExtractCookieValue(rawCookie, "qqmusic_key") ?? ExtractCookieValue(rawCookie, "Q_H_L_4");
        if (string.IsNullOrEmpty(key))
            throw new ApiException("Cookie 中未找到 qqmusic_key", -1);

        // 容忍只贴了两个键值对或整段 cookie;统一补全为标准形态
        _uin = uin.TrimStart('o');
        _authst = key;
        _cookieHeader = $"uin={uin}; qqmusic_key={key}";
        _cookie.QQCookieRaw = _cookieHeader;
        _cookie.Save();
    }

    /// <summary>退出登录:清除本地持久化的 QQ Cookie。</summary>
    public void ClearCookie()
    {
        _uin = "0";
        _authst = "";
        _cookieHeader = "";
        if (_cookie.QQCookieRaw is not null)
        {
            _cookie.QQCookieRaw = null;
            _cookie.Save();
        }
    }

    private void RestoreCookie()
    {
        try
        {
            if (_cookie.QQCookieRaw is not { Length: > 0 } raw) return;
            var uin = ExtractCookieValue(raw, "uin");
            var key = ExtractCookieValue(raw, "qqmusic_key");
            if (uin is not { Length: > 0 } || string.IsNullOrEmpty(key)) return;
            _uin = uin.TrimStart('o');
            _authst = key!;
            _cookieHeader = raw;
        }
        catch
        {
            // 存档损坏按匿名处理
        }
    }

    /// <summary>从 cookie 串取指定键的值(大小写敏感键名,容忍空格与分号分隔)。</summary>
    private static string? ExtractCookieValue(string raw, string name)
    {
        foreach (var part in raw.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (!kv[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var v = kv[1].Trim();
            if (v.Length > 0) return v;
        }
        return null;
    }

    // ---------- 端点方法 ----------

    public async Task<List<Song>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
    {
        var page = offset / Math.Max(limit, 1) + 1;
        var query = new Dictionary<string, string?>
        {
            ["w"] = keyword,
            ["ct"] = "24",
            ["qqmusic_ver"] = "1298",
            ["remoteplace"] = "txt.yqq.song",
            ["t"] = "0",
            ["aggr"] = "1",
            ["cr"] = "1",
            ["lossless"] = "0",
            ["flag_qc"] = "0",
            ["p"] = page.ToString(),
            ["n"] = limit.ToString(),
        };
        using var doc = await GetCAsync("/soso/fcgi-bin/client_search_cp", query, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQSearchResponse);
        if (resp is null || resp.Code != 0 || resp.Data?.Song?.List is null)
            throw new ApiException("搜索失败", resp?.Code ?? -1);
        return resp.Data.Song.List.Select(MapTrack).ToList();
    }

    /// <summary>播放地址:vkey.GetVkeyServer/CgiGetVkey。按档位取文件名前缀(M500=128k/M800=320k/F000=flac),
    /// 目标档拿不到 purl(VIP 或未登录)自动降级到 M500——与网易云 higher→standard 策略一致。</summary>
    public async Task<PlayUrlItem?> GetPlayUrlAsync(Song song, string level = "higher", CancellationToken ct = default)
    {
        var mid = await EnsureMidAsync(song, ct).ConfigureAwait(false);
        if (mid.Length == 0) return null;

        foreach (var (prefix, ext, br) in Qualities(level))
        {
            var url = await TryVkeyAsync(mid, prefix, ext, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(url))
                return new PlayUrlItem { Id = song.Id, Url = url, Br = br };
        }
        return null; // 全部档位不可播(VIP/版权),由调用方提示
    }

    /// <summary>歌词:主路径 fcg_query_lyric_new.fcg(base64);负业务码或空时回落
    /// musicu.musichallSong.PlayLyricInfo。两通道都无歌词则返回空 LyricResult(UI 显示空态)。</summary>
    public async Task<LyricResult?> GetLyricAsync(Song song, CancellationToken ct = default)
    {
        var mid = await EnsureMidAsync(song, ct).ConfigureAwait(false);
        if (mid.Length == 0)
            throw new ApiException("缺少歌曲 mid,无法获取歌词", -1);

        var original = "";
        var translation = "";
        try
        {
            var query = new Dictionary<string, string?>
            {
                ["songmid"] = mid,
                ["songid"] = song.Id.ToString(),
                ["outCharset"] = "utf-8",
                ["pcachetime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            };
            using var doc = await GetCAsync("/lyric/fcgi-bin/fcg_query_lyric_new.fcg", query, ct).ConfigureAwait(false);
            var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQLyricFcgResponse);
            if (resp is not null && !HasNegativeBizCode(resp.Retcode, resp.Code, resp.Subcode))
            {
                original = DecodeBase64(resp.Lyric);
                translation = DecodeBase64(resp.Trans);
            }
        }
        catch (ApiException)
        {
            // 主通道异常 → 走回落
        }

        if (original.Length == 0 && translation.Length == 0)
        {
            var fallback = await TryLyricByMusicuAsync(mid, song.Id, ct).ConfigureAwait(false);
            if (fallback is not null) return fallback;
        }
        return new LyricResult { Original = original, Translation = translation };
    }

    /// <summary>歌曲详情:music.pf_song_detail_svr/get_song_detail_yqq(数字 id 反查,顺带填 mid 缓存)。</summary>
    public async Task<Song?> GetSongDetailAsync(long id, CancellationToken ct = default)
    {
        using var doc = await PostMusicuAsync(w =>
        {
            WriteModuleReq(w, "music.pf_song_detail_svr", "get_song_detail_yqq", p =>
            {
                p.WriteNumber("song_type", 0);
                p.WriteString("song_mid", "");
                p.WriteNumber("song_id", id);
            });
        }, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQDetailResponse);
        if (resp is null || resp.Code != 0 || resp.Req0?.Data?.TrackInfo is null)
            throw new ApiException("获取歌曲详情失败", resp?.Code ?? -1);
        return MapTrack(resp.Req0.Data.TrackInfo);
    }

    // ---------- 内部 ----------

    /// <summary>level → 文件名前缀链:目标档在前,不可播逐级降级,末档恒为 M500(128k mp3)。</summary>
    private static IEnumerable<(string Prefix, string Ext, int Br)> Qualities(string level) => level.ToLowerInvariant() switch
    {
        "lossless" or "hires" => [("F000", ".flac", 999000), ("M800", ".mp3", 320000), ("M500", ".mp3", 128000)],
        "higher" => [("M800", ".mp3", 320000), ("M500", ".mp3", 128000)],
        _ => [("M500", ".mp3", 128000)],
    };

    private async Task<string> TryVkeyAsync(string mid, string prefix, string ext, CancellationToken ct)
    {
        using var doc = await PostMusicuAsync(w =>
        {
            WriteModuleReq(w, "vkey.GetVkeyServer", "CgiGetVkey", p =>
            {
                p.WriteStartArray("filename");
                p.WriteStringValue(prefix + mid + mid + ext);
                p.WriteEndArray();
                p.WriteString("guid", _guid);
                p.WriteStartArray("songmid");
                p.WriteStringValue(mid);
                p.WriteEndArray();
                p.WriteStartArray("songtype");
                p.WriteNumberValue(0);
                p.WriteEndArray();
                p.WriteString("uin", _uin);
                p.WriteNumber("loginflag", 1);
                p.WriteString("platform", "20");
                if (IsLoggedIn) p.WriteString("authst", _authst); // 登录态密钥(VIP/高码率解锁)
            });
        }, ct).ConfigureAwait(false);

        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQVkeyResponse);
        var data = resp?.Req0?.Data;
        var info = data?.MidUrlInfo?.FirstOrDefault(i => i.SongMid == mid) ?? data?.MidUrlInfo?.FirstOrDefault();
        var purl = info?.Purl;
        if (data?.Sip is null || string.IsNullOrEmpty(purl)) return "";

        var domain = data.Sip.FirstOrDefault(s => s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                     ?? data.Sip.FirstOrDefault(s => !string.IsNullOrEmpty(s) && !s.StartsWith("http://ws"))
                     ?? "";
        if (domain.Length == 0) return "";
        return domain.TrimEnd('/') + "/" + purl.TrimStart('/');
    }

    private async Task<LyricResult?> TryLyricByMusicuAsync(string mid, long songId, CancellationToken ct)
    {
        try
        {
            using var doc = await PostMusicuAsync(w =>
            {
                WriteModuleReq(w, "music.musichallSong.PlayLyricInfo", "GetPlayLyricInfo", p =>
                {
                    p.WriteString("songMID", mid);
                    p.WriteNumber("songID", songId);
                    p.WriteNumber("trans_t", 0);
                    p.WriteNumber("roma_t", 0);
                    p.WriteNumber("qrc_t", 0);
                    p.WriteNumber("crypt", 1);
                    p.WriteNumber("lrc_t", 0);
                    p.WriteNumber("interval", 0);
                });
            }, ct).ConfigureAwait(false);
            var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQMusicuLyricResponse);
            var data = resp?.Req0?.Data;
            if (data is null) return null;
            var original = DecodeBase64(data.Lyric);
            var translation = DecodeBase64(data.Trans);
            if (original.Length == 0 && translation.Length == 0) return null;
            return new LyricResult { Original = original, Translation = translation };
        }
        catch (ApiException)
        {
            return null; // 回落失败不抛,LrcParser 对空文本显示空态
        }
    }

    /// <summary>补全 songmid:Song.Mid 优先,其次 id→mid 缓存,最后详情接口反查。</summary>
    private async Task<string> EnsureMidAsync(Song song, CancellationToken ct)
    {
        if (song.Mid.Length > 0) return song.Mid;
        if (_midById.TryGetValue(song.Id, out var cached) && cached.Length > 0) return cached;
        try
        {
            var detail = await GetSongDetailAsync(song.Id, ct).ConfigureAwait(false);
            return detail?.Mid ?? "";
        }
        catch (ApiException)
        {
            return "";
        }
    }

    // ---------- 请求构造 ----------

    /// <summary>c.y.qq.com fcg 明文 GET:公共参数(g_tk 固定值与开源实现一致)+ 业务参数;登录态带 Cookie。</summary>
    private async Task<JsonDocument> GetCAsync(string path, Dictionary<string, string?> query, CancellationToken ct)
    {
        var url = CBase + path + "?" + BuildQuery(query);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (IsLoggedIn) req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseJson(body);
    }

    /// <summary>musicu.fcg JSON POST:{ 手写请求体(NativeAOT 安全),"loginUin","comm" };登录态带 Cookie。</summary>
    private async Task<JsonDocument> PostMusicuAsync(Action<Utf8JsonWriter> writeReqs, CancellationToken ct)
    {
        string json;
        using (var ms = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                writeReqs(w);
                w.WriteString("loginUin", _uin);
                w.WriteStartObject("comm");
                w.WriteString("uin", _uin);
                w.WriteString("format", "json");
                w.WriteNumber("ct", 24);
                w.WriteNumber("cv", 0);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(ms.ToArray());
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, MusicuUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (IsLoggedIn) req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseJson(body);
    }

    /// <summary>写一个命名模块请求:"req_0":{ module, method, param:{...} }。</summary>
    private static void WriteModuleReq(Utf8JsonWriter w, string module, string method, Action<Utf8JsonWriter> writeParam)
    {
        w.WriteStartObject("req_0");
        w.WriteString("module", module);
        w.WriteString("method", method);
        w.WriteStartObject("param");
        writeParam(w);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private string BuildQuery(Dictionary<string, string?> extras)
    {
        var all = new Dictionary<string, string?>
        {
            ["g_tk"] = "1124214810",
            ["loginUin"] = _uin,
            ["hostUin"] = "0",
            ["inCharset"] = "utf8",
            ["outCharset"] = "utf-8",
            ["notice"] = "0",
            ["platform"] = "yqq.json",
            ["needNewCode"] = "0",
            ["format"] = "json",
        };
        foreach (var kv in extras) all[kv.Key] = kv.Value;
        return string.Join("&", all
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
    }

    private static JsonDocument ParseJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ApiException("QQ 音乐响应为空", -3);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ApiException("QQ 音乐响应不是有效 JSON", -2);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new ApiException($"请求失败 HTTP {(int)resp.StatusCode}: {body}", (int)resp.StatusCode);
    }

    private static bool HasNegativeBizCode(params int[] codes) => codes.Any(c => c < 0);

    /// <summary>歌词字段为 base64;解码失败原样返回(个别端点直接回明文)。</summary>
    private static string DecodeBase64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    /// <summary>统一映射到领域 Song。兼容两种形态:搜索(songname/songmid + 平铺 albumid/albummid/albumname)
    /// 与 track_info(name/mid + 嵌套 album:{id,mid,name})。歌手/专辑 id 不填充:与网易云 id 空间
    /// 不互通,填了会导致歌手/专辑页误跳网易云条目。</summary>
    private Song MapTrack(QQTrackDto t)
    {
        var id = t.SongId != 0 ? t.SongId : t.Id;
        var mid = FirstNonEmpty(t.SongMid, t.Mid);
        var name = FirstNonEmpty(t.SongName, t.Name);
        // 专辑:平铺字段(搜索)优先,嵌套对象(详情)兜底
        var albumId = t.AlbumIdFlat != 0 ? t.AlbumIdFlat : t.Album?.AlbumId ?? t.Album?.Id ?? 0;
        var albumMid = FirstNonEmpty(t.AlbumMidFlat, t.Album?.AlbumMid, t.Album?.Mid);
        var albumName = FirstNonEmpty(t.AlbumNameFlat, t.Album?.AlbumName, t.Album?.Name);

        if (id != 0 && mid.Length > 0) _midById[id] = mid;

        return new Song
        {
            Id = id,
            Mid = mid,
            Name = name,
            Artist = t.Singer is { Count: > 0 } ? string.Join("/", t.Singer.Select(s => s.Name ?? "")) : "",
            Album = albumName,
            CoverUrl = albumMid.Length > 0 ? string.Format(CoverTemplate, albumMid) : "",
            DurationMs = t.Interval > 0 ? t.Interval * 1000 : 0,
            Fee = t.Pay is { PayPlay: 0 } ? 0 : 1,
            Source = MusicSource.QQ,
        };
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}
