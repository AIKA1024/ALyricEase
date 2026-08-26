using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services.NetEase;

namespace ALyricEase.Services.QQMusic;

/// <summary>QQ 音乐 API 客户端:u.y.qq.com musicu.fcg(JSON POST,一次一模块)+ c.y.qq.com fcg 明文 GET。
/// 上游协议参考开源 qq-music-api(Koa 版)。支持 Cookie 登录(网页版 QQ 音乐的 uin + qqmusic_key,
/// SetCookie 粘贴整段 cookie 即可):登录后 VIP/320k 可播;匿名仅免费歌 128k。失败抛 ApiException。</summary>
public sealed class QQMusicApiClient : IMusicApi, IUserMusicApi
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
    /// 校验齐全后把【完整原文】持久化 —— 播放链路只用 uin+qqmusic_key,但歌单/每日推荐等
    /// 账号接口需要完整浏览器 Cookie(含 p_skey 等)才能通过校验。格式不对抛 ApiException。</summary>
    public void SetCookie(string rawCookie)
    {
        rawCookie = rawCookie.Trim();
        if (rawCookie.Length == 0) throw new ApiException("QQ Cookie 为空", -1);

        var uin = ExtractCookieValue(rawCookie, "uin");
        if (uin is null || !uin.TrimStart('o').All(char.IsDigit) || uin.TrimStart('o').Length == 0)
            throw new ApiException("Cookie 中未找到有效的 uin", -1);
        var key = ExtractCookieValue(rawCookie, "qqmusic_key") ?? ExtractCookieValue(rawCookie, "qm_keyst");
        if (string.IsNullOrEmpty(key))
            throw new ApiException("Cookie 中未找到 qqmusic_key(或 qm_keyst)", -1);

        _uin = uin.TrimStart('o');
        _authst = key;
        _cookieHeader = rawCookie; // 保留原文:账号接口依赖 p_skey/euin 等附加字段
        _cookie.QQCookieRaw = rawCookie;
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
            var key = ExtractCookieValue(raw, "qqmusic_key") ?? ExtractCookieValue(raw, "qm_keyst");
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

    /// <summary>按数字 id 取歌曲详情:music.pf_song_detail_svr/get_song_detail_yqq(顺带填 mid 缓存)。</summary>
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

    // ---------- IUserMusicApi 账号能力(需有效登录 Cookie) ----------

    /// <summary>c6.y.qq.com 用户主页:一次返回 creator 资料 + mydiss.list 歌单列表。</summary>
    private const string HomepageBase = "https://c6.y.qq.com";

    /// <summary>用户主页(资料 + 歌单),code=1000 表示登录失效/凭据不完整。</summary>
    private async Task<QQHomepageResponse> GetHomepageAsync(CancellationToken ct)
    {
        var query = new Dictionary<string, string?>
        {
            ["_"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["cv"] = "4747474",
            ["ct"] = "24",
            ["inCharset"] = "utf-8",
            ["uin"] = _uin,
            ["userid"] = _uin,
            ["reqfrom"] = "1",
            ["reqtype"] = "0",
            ["loginUin"] = _uin,
            ["cid"] = "205360838",
        };
        using var doc = await GetCAsync("/rsc/fcgi-bin/fcg_get_profile_homepage.fcg", query, ct,
            HomepageBase, $"https://y.qq.com/portal/profile.html?uin={_uin}").ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQHomepageResponse);
        if (resp is null || resp.Code != 0 || resp.Data is null)
            throw new ApiException(resp?.Code == 1000
                ? "登录已失效或 Cookie 不完整,请重新粘贴完整浏览器 Cookie"
                : "获取用户信息失败", resp?.Code ?? -1);
        return resp;
    }

    public async Task<UserProfile> GetUserProfileAsync(CancellationToken ct = default)
    {
        var home = await GetHomepageAsync(ct).ConfigureAwait(false);
        var c = home.Data?.Creator;
        var nick = FirstNonEmpty(c?.Nick, c?.Nickname);
        return new UserProfile
        {
            UserId = long.TryParse(_uin, out var u) ? u : 0,
            Nickname = nick.Length > 0 ? nick : $"QQ {_uin}",
            AvatarUrl = FirstNonEmpty(c?.Avatar, c?.FaceUrl, c?.Headpic),
        };
    }

    /// <summary>用户歌单:主页 mydiss.list(dissid/dissname/song_count/picurl,字段随版本有漂移取兜底)。</summary>
    public async Task<List<Playlist>> GetUserPlaylistsAsync(CancellationToken ct = default)
    {
        var home = await GetHomepageAsync(ct).ConfigureAwait(false);
        return (home.Data?.Mydiss?.List ?? new List<QQDissItem>())
            .Select(d => new Playlist
            {
                Id = d.DissId != 0 ? d.DissId : d.DissTid,
                Name = FirstNonEmpty(d.DissName, d.Dirname),
                TrackCount = d.SongCount != 0 ? d.SongCount : d.SongcountAlt,
                CoverUrl = FirstNonEmpty(d.Picurl, d.Logo),
                Description = d.Intro ?? "",
            })
            .Where(p => p.Id != 0)
            .ToList();
    }

    /// <summary>歌单全量曲目:fcg_ucc_getcdinfo_byids_cp(new_format=1),曲目字段兼容两套命名。</summary>
    public async Task<List<Song>> GetPlaylistTracksAsync(long id, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["disstid"] = id.ToString(),
            ["type"] = "1",
            ["json"] = "1",
            ["utf8"] = "1",
            ["onlysong"] = "0",
            ["new_format"] = "1",
        };
        using var doc = await GetCAsync("/qzone/fcgi-bin/fcg_ucc_getcdinfo_byids_cp.fcg", query, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQCdListResponse);
        if (resp is null || resp.Code != 0 || resp.Cdlist is null)
            throw new ApiException(resp?.Code == 1000
                ? "登录已失效或 Cookie 不完整,请重新登录"
                : "获取歌单曲目失败", resp?.Code ?? -1);
        var songs = new List<Song>();
        foreach (var cd in resp.Cdlist)
            foreach (var t in cd.Songlist ?? Enumerable.Empty<QQTrackDto>())
            {
                var s = MapTrack(t);
                if (s.Id != 0 || s.Mid.Length > 0) songs.Add(s);
            }
        return songs;
    }

    /// <summary>每日推荐歌曲:首选官方客户端"今日私享"歌单(每日30曲,一次拉全,约 0.5s);
    /// 失败(未登录/页面改版)回落雷达流串行翻页(约 2-3s)。两源均为个性化日推,条目 track_info 同构。</summary>
    public async Task<List<Song>> GetDailyRecommendSongsAsync(CancellationToken ct = default)
    {
        try
        {
            var songs = await GetDailyFromSrfDissAsync(ct).ConfigureAwait(false);
            if (songs.Count > 0) return songs;
        }
        catch (ApiException)
        {
            // 今日私享不可用(未登录/改版):回落雷达
        }
        return await GetDailyFromRadarAsync(ct).ConfigureAwait(false);
    }

    /// <summary>今日私享歌单(官方"每日30曲"):Mac 客户端首页 HTML 解析当日 rid(每日更换,当日缓存),
    /// 再走 music.srfDissInfo.DissInfo/CgiGetDiss 一次拉全(条目为 track_info 同构,复用 MapTrack)。</summary>
    private async Task<List<Song>> GetDailyFromSrfDissAsync(CancellationToken ct)
    {
        var rid = await GetSrfDissRidAsync(ct).ConfigureAwait(false);
        using var doc = await PostMusicuAsync(w =>
        {
            WriteModuleReq(w, "music.srfDissInfo.DissInfo", "CgiGetDiss", p =>
            {
                p.WriteNumber("disstid", rid);
                p.WriteNumber("dirid", 0);
                p.WriteBoolean("tag", true);
                p.WriteNumber("song_begin", 0);
                p.WriteNumber("song_num", 50);
                p.WriteBoolean("userinfo", true);
                p.WriteBoolean("orderlist", true);
                p.WriteBoolean("onlysonglist", false);
            });
        }, ct).ConfigureAwait(false);

        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQCgiGetDissResponse);
        var data = resp?.Req0?.Data;
        if (resp?.Req0 is null || resp.Req0.Code != 0 || data?.Songlist is null)
            throw new ApiException($"获取今日私享失败(code={resp?.Req0?.Code ?? -1})", resp?.Req0?.Code ?? -1);

        var songs = new List<Song>();
        foreach (var t in data.Songlist)
        {
            var s = MapTrack(t);
            if (s.Id != 0 || s.Mid.Length > 0) songs.Add(s);
        }
        return songs;
    }

    /// <summary>今日私享 rid:从 c.y.qq.com Mac 客户端首页 HTML 解析(rid 每日更换,按本地日期缓存)。</summary>
    private (DateOnly Day, long Rid)? _srfRidCache;

    private async Task<long> GetSrfDissRidAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_srfRidCache is { } hit && hit.Day == today) return hit.Rid;

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://c.y.qq.com/node/musicmac/v6/index.html");
        if (IsLoggedIn) req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // "今日私享"文案两侧的 <a data-rid="..."> 即歌单 id;取邻近窗口内首个命中
        long rid = 0;
        var anchor = html.IndexOf("今日私享", StringComparison.Ordinal);
        while (anchor >= 0 && rid == 0)
        {
            var window = html[Math.Max(0, anchor - 600)..Math.Min(html.Length, anchor + 600)];
            var m = Regex.Match(window, "data-rid=\"(\\d+)\"");
            if (m.Success) rid = long.Parse(m.Groups[1].Value);
            anchor = html.IndexOf("今日私享", anchor + 4, StringComparison.Ordinal);
        }
        if (rid == 0) throw new ApiException("未在首页找到今日私享歌单(未登录或页面改版)", -1);
        _srfRidCache = (today, rid);
        return rid;
    }

    /// <summary>雷达流日推(兜底):music.recommend.TrackRelationServer.GetRadarSong,登录后按听歌偏好生成。
    /// 单页仅约 5 首且服务端按时间窗随机洗牌(并发/密集请求返回高度重叠的结果),只能串行翻页拼至 30 首;
    /// 业务码非 0 抛 ApiException(500003 = 未登录/Cookie 失效)。</summary>
    private async Task<List<Song>> GetDailyFromRadarAsync(CancellationToken ct)
    {
        const int TargetCount = 30, MaxPages = 6;
        var songs = new List<Song>();
        var seen = new HashSet<string>();
        for (var page = 1; page <= MaxPages && songs.Count < TargetCount; page++)
        {
            using var doc = await PostMusicuAsync(w =>
            {
                WriteModuleReq(w, "music.recommend.TrackRelationServer", "GetRadarSong", p =>
                {
                    p.WriteNumber("Page", page);
                    p.WriteNumber("ReqType", 0);
                    p.WriteStartArray("FavSongs");
                    p.WriteEndArray();
                    p.WriteStartArray("EntranceSongs");
                    p.WriteEndArray();
                });
            }, ct).ConfigureAwait(false);

            var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQRadarResponse);
            var req = resp?.Req0;
            if (req is null || req.Code != 0)
                throw new ApiException(req?.Code == 500003
                    ? "未登录或登录已失效,每日推荐需要有效的 QQ 音乐 Cookie"
                    : $"获取每日推荐失败(code={req?.Code ?? -1})", req?.Code ?? -1);

            foreach (var entry in req.Data?.VecSongs ?? new List<QQRadarEntry>())
            {
                if (entry.Track is null) continue;
                var s = MapTrack(entry.Track);
                if ((s.Id == 0 && s.Mid.Length == 0) || !seen.Add(s.Mid.Length > 0 ? s.Mid : s.Id.ToString())) continue;
                songs.Add(s);
                if (songs.Count >= TargetCount) break;
            }
            if (req.Data?.HasMore != true) break;
        }
        return songs;
    }

    // ---------- 歌手/专辑(mid 维度,QQ 专属导航页)----------

    /// <summary>歌手热门歌曲:musichall.song_list_server/GetSingerSongList(条目为 songInfo 包裹的标准曲目)。</summary>
    public async Task<List<Song>> GetArtistSongsAsync(string singerMid, int limit = 30, CancellationToken ct = default)
    {
        using var doc = await PostMusicuAsync(w =>
        {
            WriteModuleReq(w, "musichall.song_list_server", "GetSingerSongList", p =>
            {
                p.WriteString("singerMid", singerMid);
                p.WriteNumber("order", 1);
                p.WriteNumber("number", limit);
                p.WriteNumber("begin", 0);
            });
        }, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQSongEntriesResponse);
        return MapSongEntries(resp?.Req0?.Code, resp?.Req0?.Data?.SongList);
    }

    /// <summary>歌手专辑列表:music.musichallAlbum.AlbumListServer/GetAlbumList(封面按 albummid 拼模板)。</summary>
    public async Task<List<ArtistAlbumItem>> GetArtistAlbumsAsync(string singerMid, int limit = 50, CancellationToken ct = default)
    {
        using var doc = await PostMusicuAsync(w =>
        {
            WriteModuleReq(w, "music.musichallAlbum.AlbumListServer", "GetAlbumList", p =>
            {
                p.WriteString("singerMid", singerMid);
                p.WriteNumber("order", 1);
                p.WriteNumber("number", limit);
                p.WriteNumber("begin", 0);
            });
        }, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQAlbumListResponse);
        var req = resp?.Req0;
        if (req is null || req.Code != 0 || req.Data?.AlbumList is null)
            throw new ApiException($"获取歌手专辑失败(code={req?.Code ?? -1})", req?.Code ?? -1);
        return req.Data.AlbumList
            .Where(a => !string.IsNullOrEmpty(a.AlbumMid))
            .Select(a => new ArtistAlbumItem
            {
                Id = a.AlbumId,
                Mid = a.AlbumMid!,
                Name = a.AlbumName ?? "",
                PicUrl = string.Format(CoverTemplate, a.AlbumMid!),
                Size = a.TotalNum,
                Type = a.AlbumType ?? "",
            })
            .ToList();
    }

    /// <summary>专辑全量曲目:music.musichallAlbum.AlbumSongList/GetAlbumSongList(单专辑一次拉全,通常 ≤ 100 首)。</summary>
    public async Task<List<Song>> GetAlbumSongsByMidAsync(string albumMid, int limit = 200, CancellationToken ct = default)
    {
        using var doc = await PostMusicuAsync(w =>
        {
            WriteModuleReq(w, "music.musichallAlbum.AlbumSongList", "GetAlbumSongList", p =>
            {
                p.WriteString("albumMid", albumMid);
                p.WriteNumber("num", limit);
                p.WriteNumber("begin", 0);
            });
        }, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQSongEntriesResponse);
        return MapSongEntries(resp?.Req0?.Code, resp?.Req0?.Data?.SongList);
    }

    /// <summary>专辑基础信息(名字/发行日期/简介):music.musichallAlbum.AlbumInfoServer/GetAlbumDetail。</summary>
    public async Task<QQAlbumInfo> GetAlbumInfoByMidAsync(string albumMid, CancellationToken ct = default)
    {
        using var doc = await PostMusicuAsync(w =>
        {
            WriteModuleReq(w, "music.musichallAlbum.AlbumInfoServer", "GetAlbumDetail",
                p => p.WriteString("albumMid", albumMid));
        }, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQAlbumDetailResponse);
        var b = resp?.Req0?.Data?.BasicInfo;
        if (resp?.Req0 is null || resp.Req0.Code != 0 || b is null)
            throw new ApiException($"获取专辑信息失败(code={resp?.Req0?.Code ?? -1})", resp?.Req0?.Code ?? -1);
        return new QQAlbumInfo(b.AlbumName ?? "", b.PublishDate ?? "", b.Desc ?? "");
    }

    /// <summary>songInfo 包裹层列表统一映射;业务码非 0 抛 ApiException。</summary>
    private List<Song> MapSongEntries(int? code, List<QQSongEntryDto>? list)
    {
        if (code is not 0)
            throw new ApiException(code == 500003
                ? "未登录或登录已失效,请重新登录"
                : $"获取数据失败(code={code ?? -1})", code ?? -1);
        var songs = new List<Song>();
        foreach (var e in list ?? new List<QQSongEntryDto>())
        {
            if (e.SongInfo is null) continue;
            var s = MapTrack(e.SongInfo);
            if (s.Id != 0 || s.Mid.Length > 0) songs.Add(s);
        }
        return songs;
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

    /// <summary>fcg 明文 GET:公共参数(g_tk 按 p_skey 计算)+ 业务参数;登录态带 Cookie。
    /// baseUrl/referer 可覆盖(用户主页在 c6.y.qq.com 且 Referer 必须是 profile 页)。</summary>
    private async Task<JsonDocument> GetCAsync(string path, Dictionary<string, string?> query, CancellationToken ct,
        string? baseUrl = null, string? referer = null)
    {
        var url = (baseUrl ?? CBase) + path + "?" + BuildQuery(query);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (IsLoggedIn) req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        if (referer is not null) req.Headers.TryAddWithoutValidation("Referer", referer);
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
            ["g_tk"] = ComputeGtk().ToString(),
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

    /// <summary>g_tk(bkn):有 p_skey 时按标准哈希计算(账号接口校验用),否则退回开源实现用的固定值。</summary>
    private int ComputeGtk()
    {
        var pskey = ExtractCookieValue(_cookieHeader, "p_skey");
        if (string.IsNullOrEmpty(pskey)) return 1124214810;
        unchecked
        {
            var hash = 5381;
            foreach (var ch in pskey) hash += (hash << 5) + ch;
            return hash & 0x7fffffff;
        }
    }

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
            Fee = t.Pay is { EffectivePayPlay: 0 } ? 0 : 1,
            ArtistNames = t.Singer?.Select(s => s.Name ?? "").Where(n => n.Length > 0).ToList() ?? new List<string>(),
            ArtistMids = t.Singer?.Select(s => s.Mid ?? "").Where(m => m.Length > 0).ToList() ?? new List<string>(),
            AlbumId = albumId,
            AlbumMid = albumMid,
            Source = MusicSource.QQ,
        };
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}
