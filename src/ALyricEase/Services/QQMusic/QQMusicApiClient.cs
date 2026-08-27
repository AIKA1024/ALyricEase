using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        // 换号后已喜欢集合/tid/会员状态缓存全部失效
        _likedDissTid = 0;
        _likedIds = null;
        _vipLoaded = false;
        _vipLoading = false;
        IsVip = false;
        _cookie.QQCookieRaw = rawCookie;
        _cookie.Save();
    }

    /// <summary>退出登录:清除本地持久化的 QQ Cookie。</summary>
    public void ClearCookie()
    {
        _uin = "0";
        _authst = "";
        _cookieHeader = "";
        // 登出后已喜欢集合/tid/会员状态缓存失效
        _likedDissTid = 0;
        _likedIds = null;
        _vipLoaded = false;
        _vipLoading = false;
        IsVip = false;
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
                // 缺省返回 MusicJsonCallback(...) JSONP 包裹,纯 JSON 解析会失败
                ["format"] = "json",
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

    // ---------- 红心/喜欢(音乐资产目录 dirId=201,即"我喜欢") ----------

    /// <summary>QQ 音乐"我喜欢"资产目录 id:网页版红心的固定写入目标(musicasset 写接口的 dirId)。
    /// 用户歌单列表(GetPlaylistByUin)中"我喜欢"一项的 dirId 也是它;普通歌单的 dirId 与其 tid 一致。</summary>
    public const int LikedDirId = 201;

    /// <summary>"我喜欢"歌单 tid(用于经歌单详情拉取已喜欢曲目集合;0 = 未识别到)。</summary>
    private long _likedDissTid;

    /// <summary>已喜欢曲目 songid 集合(null = 未加载)。</summary>
    private HashSet<long>? _likedIds;

    private bool _likedLoading;
    private Task? _likedLoadTask;

    public bool CanToggleLike => IsLoggedIn;

    // ---------- 会员状态(VipLogin.VipLoginInter/vip_login_base)----------

    private bool _vipLoaded;
    private bool _vipLoading;
    private Task? _vipLoadTask;

    /// <summary>当前登录用户是否为 QQ 音乐会员(绿钻 identity.vip/huge_vip;未登录/未加载为 false)。
    /// 播放 VIP 歌曲失败消息用;经 EnsureVipStatusAsync 惰性加载。</summary>
    public bool IsVip { get; private set; }

    /// <summary>会员状态是否已确认(vip_login_base 已返回或未登录)。</summary>
    public bool IsVipLoaded => _vipLoaded || !IsLoggedIn;

    /// <summary>确保会员状态已加载(幂等,单飞):首次发 vip_login_base,失败按非会员处理不抛。</summary>
    public Task EnsureVipStatusAsync(CancellationToken ct = default)
    {
        if (_vipLoaded || !IsLoggedIn) return Task.CompletedTask;
        if (_vipLoading) return _vipLoadTask ?? Task.CompletedTask;
        _vipLoading = true;
        return _vipLoadTask = LoadVipStatusAsync(ct);
    }

    private async Task LoadVipStatusAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await PostMusicuAsync(w =>
                WriteModuleReq(w, "VipLogin.VipLoginInter", "vip_login_base", _ => { }), ct).ConfigureAwait(false);
            var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQVipLoginResponse);
            var identity = resp?.Req0?.Data?.Identity;
            IsVip = resp is { Code: 0 } && resp.Req0 is { Code: 0 } &&
                    ((identity?.Vip ?? 0) != 0 || (identity?.HugeVip ?? 0) != 0);
        }
        catch
        {
            IsVip = false; // 网络失败按非会员(播放消息兜底;下次登录重载)
        }
        finally
        {
            _vipLoaded = true;
            _vipLoading = false;
        }
    }

    /// <summary>懒加载已喜欢集合(幂等,单飞):在用户歌单中定位"我喜欢"(优先按资产目录
    /// dirId=201 判定,其次名称匹配——个别账号可改名)拿 tid,再走歌单详情按页收集 songid。</summary>
    public Task EnsureLikedIdsAsync(CancellationToken ct = default)
    {
        if (_likedIds is not null || !IsLoggedIn) return Task.CompletedTask;
        if (_likedLoading) return _likedLoadTask ?? Task.CompletedTask;
        _likedLoading = true;
        return _likedLoadTask = LoadLikedIdsAsync(ct);
    }

    private async Task LoadLikedIdsAsync(CancellationToken ct)
    {
        try
        {
            var playlists = await GetUserPlaylistsAsync(ct).ConfigureAwait(false);
            // 权威标识是资产目录 dirId=201;找不到(改名/字段漂移)再按名称匹配。
            // 绝不能回退到"任取第一个歌单":那是普通用户歌单,拿它当喜欢集合会显示错误红心,
            // 还会把集合污染进后续 toggle 的目标判定。
            var liked = playlists.FirstOrDefault(p => p.DirId == LikedDirId)
                        ?? playlists.FirstOrDefault(p => p.Name == "我喜欢");
            if (liked is null)
            {
                _likedIds ??= new HashSet<long>(); // 空账号/未识别:视为空集(合法状态,不再重试)
                return;
            }
            _likedDissTid = liked.Id;
            var tracks = await GetPlaylistTracksAsync(liked.Id, ct).ConfigureAwait(false);
            _likedIds = new HashSet<long>(tracks.Where(t => t.Id != 0).Select(t => t.Id));
        }
        catch
        {
            // 拉取失败降级空集:初始态一律未喜欢,toggle 以"添加"为先(fail-safe 不会误删用户的收藏)
            _likedIds ??= new HashSet<long>();
        }
        finally
        {
            _likedLoading = false;
        }
    }

    /// <summary>已喜欢集合是否含该曲目(集合未加载时返回 false)。</summary>
    public bool IsLiked(long id) => _likedIds?.Contains(id) ?? false;

    /// <summary>切换红心:返回切换后的状态;失败抛 ApiException(调用方回滚 UI)。
    /// 协议参考开源 multiPlatformMusicApi 的 qqmusic like 模块:music.musicasset.PlaylistDetailWrite
    /// 的 AddSonglist/DelSonglist,param 为 { dirId:201("我喜欢"), v_songInfo:[{songType:0,songId}] }。
    /// 注意 QQ 的红心不是"加入某个用户歌单",而是音乐资产的固定喜欢目录操作:
    /// 写目标恒为 dirId=201,"我喜欢"歌单 tid 只用于读取已喜欢集合,绝不能当写目标
    /// (AddSonglist 按资产目录定位,传 tid 会指向不存在的目录或误写其他歌单)。</summary>
    public async Task<bool> LikeToggleAsync(long id, CancellationToken ct = default)
    {
        if (!IsLoggedIn)
            throw new ApiException("QQ音乐未登录,无法切换红心", -1);
        if (id == 0)
            throw new ApiException("无效曲目 id", -1);

        await EnsureLikedIdsAsync(ct).ConfigureAwait(false); // 尽力加载;失败按空集处理 → 目标为添加
        var target = !IsLiked(id);
        var mid = _midById.TryGetValue(id, out var m) ? m : "";

        await SecureLikeRequestAsync(target, id, mid, LikedDirId, ct).ConfigureAwait(false);

        _likedIds ??= new HashSet<long>();
        if (target) _likedIds.Add(id); else _likedIds.Remove(id);
        return target;
    }

    // ---------- 红心写操作的加密签名通道(musics.fcg / ag-1) ----------
    // 网页版对写类 asset 接口走加密通道:明文 musicu.fcg 网关对 PlaylistDetailWrite/AddSonglist 返回
    // 80105(明文写被拒)。此处端口移植开源 multiPlatformMusicApi 的 qqmusic 请求封装:
    //   comm + req_0(module=music.musicasset.PlaylistDetailWrite, method=AddSonglist/DelSonglist,
    //   param={ dirId:201, v_songInfo:[{songType:0, songId}] }) 整体 AES-128-GCM 加密 →
    //   zzcSign 派生签名 → POST u6.y.qq.com/cgi-bin/musics.fcg?encoding=ag-1&sign=...;
    //   响应体按固定 21B 密钥循环 XOR 解出明文 JSON。
    private const string MusicsUrl = "https://u6.y.qq.com/cgi-bin/musics.fcg";
    private const string GAlertRequestKeyHex = "bd305f10d0ff74b6ef54dab835b5e1cf"; // 16B AES-128-GCM 密钥
    private const string Ag1ResponseKeyHex = "7a3f8c1d5e9b2f0a6c4d7e8b1f3a5c9d0e2b6f4a81"; // 21B XOR 响应密钥

    /// <summary>向"我喜欢"目录写红心。失败(code!=0 / 网络 / 解密失败)抛 ApiException。</summary>
    private async Task SecureLikeRequestAsync(bool add, long songId, string songMid, long dirId, CancellationToken ct)
    {
        // 1) 明文 JSON:comm 全量字段(参考实现) + req_0(module/method/param)
        var dataStr = BuildAg1Json(add, songId, songMid, dirId).Replace("\r", "").Replace("\n", "");
        var sign = BuildZzcSign(dataStr);

        // 2) AES-128-GCM 加密 → base64(IV||CT||TAG)
        var body = AesGcmEncrypt(dataStr);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{MusicsUrl}?_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&encoding=ag-1&sign={sign}");
        req.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
        req.Headers.TryAddWithoutValidation("Content-Type", "text/plain");
        req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
        if (IsLoggedIn) req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ApiException($"红心{(add ? "收藏" : "取消")}失败:HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
        var raw = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var json = Ag1XorDecrypt(raw);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ApiException("红心响应无法解析", -1); }
        using (doc)
        {
            var code = PickReqCode(doc.RootElement);
            if (code != 0)
            {
                var detail = DescribeFailure(doc.RootElement);
                throw new ApiException(
                    TryPickMessage(doc.RootElement) is { } msg
                        ? $"红心{(add ? "收藏" : "取消")}失败:{msg}"
                        : $"红心{(add ? "收藏" : "取消")}失败(code={code}){detail}",
                    code);
            }
        }
    }

    /// <summary>构造 ag-1 请求 JSON(comm 全量 + 单个 req_0),与参考实现逐字段对齐。
    /// param 对齐现网(L-1124/QQMusicApi 实测形态):dirId + tid(红心恒 0)+ bFmtUtf8:true
    /// (必须保留布尔原形,服务端按真布尔校验,缺失/整型会回 80105/500026)+ v_songInfo。
    /// songMid 尽量带上:服务端对部分资产目录要求 mid,否则回 500026。</summary>
    private string BuildAg1Json(bool add, long songId, string songMid, long dirId)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("comm");
            w.WriteNumber("cv", 4747474);
            w.WriteNumber("ct", 24);
            w.WriteString("format", "json");
            w.WriteString("inCharset", "utf-8");
            w.WriteString("outCharset", "utf-8");
            w.WriteNumber("notice", 0);
            w.WriteString("platform", "yqq.json");
            w.WriteNumber("needNewCode", 1);
            w.WriteString("uin", _uin);
            w.WriteNumber("g_tk_new_20200303", ComputeGtk());
            w.WriteNumber("g_tk", ComputeGtk());
            w.WriteEndObject();
            w.WriteStartObject("req_0");
            w.WriteString("module", "music.musicasset.PlaylistDetailWrite");
            w.WriteString("method", add ? "AddSonglist" : "DelSonglist");
            w.WriteStartObject("param");
            w.WriteNumber("dirId", dirId);
            w.WriteNumber("tid", 0);
            w.WriteBoolean("bFmtUtf8", true);
            w.WriteStartArray("v_songInfo");
            w.WriteStartObject();
            w.WriteNumber("songType", 0);
            w.WriteNumber("songId", songId);
            if (songMid.Length > 0) w.WriteString("songMid", songMid);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject(); // param
            w.WriteEndObject(); // req_0
            w.WriteEndObject(); // root
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>zzcSign:对请求明文做 SHA1,按固定索引/混淆表派生,输出以 zzc 开头、小写。</summary>
    private static string BuildZzcSign(string payload)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(payload)));

        // JS 对索引 <40 才取字符(40 越界跳过);逐字拼接以复刻该剪裁
        var part1 = "";
        int[] part1Idx = { 23, 14, 6, 36, 16, 40, 7, 19 };
        foreach (var i in part1Idx) if (i < hash.Length) part1 += hash[i];

        var part2 = "";
        int[] part2Idx = { 16, 1, 32, 12, 19, 27, 8, 5 };
        foreach (var i in part2Idx) part2 += hash[i];

        int[] scramble = { 89, 39, 179, 150, 218, 82, 58, 252, 177, 52, 186, 123, 120, 64, 242, 133, 143, 161, 121, 179 };
        var part3 = new byte[scramble.Length];
        for (var i = 0; i < scramble.Length; i++)
        {
            var hashByte = Convert.ToByte(hash.Substring(i * 2, 2), 16);
            part3[i] = (byte)(scramble[i] ^ hashByte);
        }
        var b64 = Convert.ToBase64String(part3).Replace("/", "").Replace("+", "").Replace("=", "");

        return $"zzc{new string(part1)}{b64}{new string(part2)}".ToLowerInvariant();
    }

    /// <summary>AES-128-GCM 加密请求体 → base64(IV||CT||TAG)。</summary>
    private static string AesGcmEncrypt(string plaintext)
    {
        using var aes = new AesGcm(Convert.FromHexString(GAlertRequestKeyHex), 16);
        var iv = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        aes.Encrypt(iv, pt, ct, tag);
        var outb = new byte[iv.Length + ct.Length + tag.Length];
        Buffer.BlockCopy(iv, 0, outb, 0, iv.Length);
        Buffer.BlockCopy(ct, 0, outb, iv.Length, ct.Length);
        Buffer.BlockCopy(tag, 0, outb, iv.Length + ct.Length, tag.Length);
        return Convert.ToBase64String(outb);
    }

    /// <summary>响应体按固定 21B 密钥循环 XOR 解出 UTF-8 明文(16 进制串解码为 21 字节)。</summary>
    private static string Ag1XorDecrypt(byte[] data)
    {
        var key = Convert.FromHexString(Ag1ResponseKeyHex); // hex 解码 → 21B
        var outb = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            outb[i] = (byte)(data[i] ^ key[i % key.Length]);
        return Encoding.UTF8.GetString(outb);
    }

    /// <summary>musicu.fcg 单模块业务码:req_0.code,缺失时回退 req_0.data.code(不同 asset 模块层级不一)。</summary>
    private static int PickReqCode(JsonElement root)
    {
        if (root.TryGetProperty("req_0", out var r0) && r0.ValueKind == JsonValueKind.Object)
        {
            if (r0.TryGetProperty("code", out var c) && c.TryGetInt32(out var v)) return v;
            if (r0.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                d.TryGetProperty("code", out var dc) && dc.TryGetInt32(out var dv)) return dv;
        }
        return -1;
    }

    /// <summary>从写请求响应里捞人读错误信息(data.code_msg / data.msg),缺省 null。</summary>
    private static string? TryPickMessage(JsonElement root)
    {
        if (root.TryGetProperty("req_0", out var r0) &&
            r0.ValueKind == JsonValueKind.Object &&
            r0.TryGetProperty("data", out var d) &&
            d.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "code_msg", "msg", "message" })
                if (d.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.String &&
                    m.GetString() is { Length: > 0 } s)
                    return s;
        }
        return null;
    }

    /// <summary>诊断:拼出响应紧凑摘要(优先 req_0 原文,缺省整棵根),便于定位网关拒绝原因。</summary>
    private static string DescribeFailure(JsonElement root)
    {
        try
        {
            JsonElement target = root;
            if (root.TryGetProperty("req_0", out var r0) && r0.ValueKind == JsonValueKind.Object)
                target = r0;
            var text = target.GetRawText();
            return text.Length > 200 ? $" |resp:{text[..200]}" : $" |resp:{text}";
        }
        catch { /* 忽略 */ }
        return "";
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
        // 官方 asset 通道:userInfo.BaseUserInfoServer/get_user_baseinfo_v2,按 uin 键回 map_userinfo。
        try
        {
            using var doc = await PostMusicuAsync(w =>
            {
                WriteModuleReq(w, "userInfo.BaseUserInfoServer", "get_user_baseinfo_v2", p =>
                {
                    p.WriteStartArray("vec_uin");
                    p.WriteStringValue(_uin);
                    p.WriteEndArray();
                });
            }, ct).ConfigureAwait(false);
            var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQUserInfoResponse);
            if (resp?.Req0?.Code == 0 &&
                resp.Req0.Data?.MapUserinfo is { } map && map.TryGetValue(_uin, out var me))
            {
                var assetNick = FirstNonEmpty(me.Nick);
                if (assetNick.Length > 0)
                {
                    // 资料返回时同步确认会员状态:登录/启动后歌单与歌曲行渲染前即可预判可播性
                    await EnsureVipStatusAsync(ct).ConfigureAwait(false);
                    return new UserProfile
                    {
                        UserId = long.TryParse(_uin, out var assetUin) ? assetUin : 0,
                        Nickname = assetNick,
                        AvatarUrl = FirstNonEmpty(me.HeadUrl, me.IfPicUrl),
                    };
                }
            }
        }
        catch (ApiException)
        {
            // asset 通道失败(凭据形态漂移等)→ 回落用户主页 fcg
        }

        var home = await GetHomepageAsync(ct).ConfigureAwait(false);
        var c = home.Data?.Creator;
        var nick = FirstNonEmpty(c?.Nick, c?.Nickname);
        await EnsureVipStatusAsync(ct).ConfigureAwait(false); // 同上:会员状态随资料确认
        return new UserProfile
        {
            UserId = long.TryParse(_uin, out var u) ? u : 0,
            Nickname = nick.Length > 0 ? nick : $"QQ {_uin}",
            AvatarUrl = FirstNonEmpty(c?.Avatar, c?.FaceUrl, c?.Headpic),
        };
    }

    /// <summary>用户歌单:官方 asset 双模块一次复合拉取 —— PlaylistBaseRead/GetPlaylistByUin(自建,
    /// tid/dirName/picUrl/songNum)+ PlaylistFavRead/GetPlaylistFavInfo(收藏,tid/name/logo/songnum),
    /// 参考开源 multiPlatformMusicApi。老 homepage mydiss 通道对部分账号不再下发歌单名(界面呈现空名),
    /// 仅作为 asset 整体失败时的回落。</summary>
    public async Task<List<Playlist>> GetUserPlaylistsAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await PostMusicuMultiAsync(
            [
                w => WriteModuleReq(w, "req_0", "music.musicasset.PlaylistBaseRead", "GetPlaylistByUin",
                    p => p.WriteString("uin", _uin)),
                w => WriteModuleReq(w, "req_1", "music.musicasset.PlaylistFavRead", "GetPlaylistFavInfo",
                    p => p.WriteString("uin", _uin)),
            ], ct).ConfigureAwait(false);

            var root = doc.RootElement;
            var created = root.TryGetProperty("req_0", out var c)
                ? c.Deserialize(QQMusicJsonContext.Default.QQCreatedPlaylistsResponse) : null;
            var fav = root.TryGetProperty("req_1", out var f)
                ? f.Deserialize(QQMusicJsonContext.Default.QQFavPlaylistsResponse) : null;

            // 任一模块回负/非零业务码(80030/80050=凭据残缺或失效)→ 抛出走 homepage 回落;
            // 双码皆 0 才认作成功(新账号无歌单返回空列表是合法状态)。
            if (created is null || fav is null || created.Code != 0 || fav.Code != 0)
                throw new ApiException(
                    $"获取用户歌单失败(created={created?.Code ?? -1},fav={fav?.Code ?? -1})",
                    PickErrorCode(created?.Code, fav?.Code));

            var list = new List<Playlist>();
            foreach (var d in created.Data?.VPlaylist ?? [])
            {
                if (d.Tid == 0) continue;
                list.Add(new Playlist
                {
                    Id = d.Tid,
                    DirId = d.DirId,
                    Source = MusicSource.QQ,
                    Name = OrUnnamed(FirstNonEmpty(d.DirName)),
                    TrackCount = d.SongNum,
                    CoverUrl = FirstNonEmpty(d.PicUrl),
                    Description = d.Desc ?? "",
                });
            }
            foreach (var d in fav.Data?.VList ?? [])
            {
                if (d.Tid == 0) continue;
                list.Add(new Playlist
                {
                    Id = d.Tid,
                    Source = MusicSource.QQ,
                    Name = OrUnnamed(FirstNonEmpty(d.Name)),
                    TrackCount = d.SongNum,
                    CoverUrl = FirstNonEmpty(d.Logo),
                    Description = d.Desc ?? "",
                });
            }
            return list;
        }
        catch (ApiException)
        {
            return await GetUserPlaylistsViaHomepageAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>回落:老主页通道 mydiss.list(字段随版本漂移取兜底;可能缺名,置占位文案)。</summary>
    private async Task<List<Playlist>> GetUserPlaylistsViaHomepageAsync(CancellationToken ct)
    {
        var home = await GetHomepageAsync(ct).ConfigureAwait(false);
        return (home.Data?.Mydiss?.List ?? new List<QQDissItem>())
            .Select(d => new Playlist
            {
                Id = d.DissId != 0 ? d.DissId : d.DissTid,
                Source = MusicSource.QQ,
                Name = OrUnnamed(FirstNonEmpty(d.DissName, d.Dirname)),
                TrackCount = d.SongCount != 0 ? d.SongCount : d.SongcountAlt,
                CoverUrl = FirstNonEmpty(d.Picurl, d.Logo),
                Description = d.Intro ?? "",
            })
            .Where(p => p.Id != 0)
            .ToList();
    }

    /// <summary>歌单全量曲目:music.srfDissInfo.aiDissInfo/uniform_get_Dissinfo(与网页端同源,
    /// 条目为 track_info 同构复用 MapTrack)。旧 DissInfo/CgiGetDiss 与 qzone fcg_ucc 均已失效 ——
    /// CgiGetDiss 现网对缺 userinfo/tag/orderlist 的请求只回 code=0 但 songlist 空(详情页白屏);
    /// 按 song_begin/song_num 分页拉齐 total_song_num。</summary>
    public async Task<List<Song>> GetPlaylistTracksAsync(long id, CancellationToken ct = default)
    {
        const int PageSize = 300;
        var songs = new List<Song>();
        for (var begin = 0; ; begin += PageSize)
        {
            using var doc = await PostMusicuAsync(w =>
            {
                WriteModuleReq(w, "music.srfDissInfo.aiDissInfo", "uniform_get_Dissinfo", p =>
                {
                    p.WriteNumber("disstid", id);
                    p.WriteNumber("userinfo", 1);
                    p.WriteNumber("tag", 1);
                    p.WriteNumber("orderlist", 1);
                    p.WriteNumber("song_begin", begin);
                    p.WriteNumber("song_num", PageSize);
                    p.WriteNumber("onlysonglist", 0);
                    p.WriteString("enc_host_uin", "");
                });
            }, ct).ConfigureAwait(false);

            var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQUniformDissResponse);
            var req = resp?.Req0;
            if (resp is null || req is null || req.Code != 0 || req.Data is null || req.Data.Code != 0)
                throw new ApiException(req?.Code == 1000 || req is { Code: 0, Data.Code: 1000 }
                    ? "登录已失效或 Cookie 不完整,请重新登录"
                    : $"获取歌单曲目失败(code={req?.Code ?? -1}/{req?.Data?.Code ?? -1},可能是无效或不可见的歌单)",
                    PickErrorCode(req?.Code, req?.Data?.Code));

            var pageSonglist = req.Data.Songlist ?? [];
            foreach (var t in pageSonglist)
            {
                var s = MapTrack(t);
                if (s.Id != 0 || s.Mid.Length > 0) songs.Add(s);
            }

            // 终止:本页不满一页(没有更多);total 已知且已达总量同理(total 缺省为 0 时靠页长判断)
            var total = req.Data.TotalSongNum;
            var hasMore = pageSonglist.Count >= PageSize && (total <= 0 || songs.Count < total);
            if (!hasMore) break;
        }
        return songs;
    }

    /// <summary>每日推荐歌曲:官方"每日30首"歌单(与 PC 客户端同源)——RecommendFeed 首页货架取
    /// "每日30首"卡片的当日动态 disstid,再走 CgiGetDiss 拉全曲目。接口异常/卡片缺失回落
    /// 雷达流 GetRadarSong;两条路径都失败才抛 ApiException。</summary>
    public async Task<List<Song>> GetDailyRecommendSongsAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetDaily30FromFeedAsync(ct).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            return await GetDailyFromRadarAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>官方每日30首:feed 货架 → "每日30首"卡片 → 当日 disstid → CgiGetDiss。
    /// disstid 每日轮换;当日个别曲目下架时歌单可少于 30 首,空歌单按异常处理走回落。</summary>
    private async Task<List<Song>> GetDaily30FromFeedAsync(CancellationToken ct)
    {
        long dissTid;
        using (var doc = await PostMusicuAsync(w =>
        {
            WriteModuleReq(w, "music.recommend.RecommendFeed", "get_recommend_feed", p =>
            {
                p.WriteNumber("direction", 0);
                p.WriteNumber("page", 1);
                p.WriteStartArray("v_cache");
                p.WriteEndArray();
                p.WriteStartArray("v_uniq");
                p.WriteEndArray();
                p.WriteNumber("s_num", 0);
            });
        }, ct).ConfigureAwait(false))
        {
            var resp = doc.RootElement.Deserialize(QQMusicJsonContext.Default.QQRecommendFeedResponse);
            var req = resp?.Req0;
            if (req is null || req.Code != 0)
                throw new ApiException($"获取每日推荐入口失败(code={req?.Code ?? -1})", req?.Code ?? -1);
            dissTid = (req.Data?.VShelf ?? [])
                .SelectMany(s => s.VNiche ?? [])
                .SelectMany(n => n.VCard ?? [])
                .FirstOrDefault(c => c.Title == "每日30首" && c.Id != 0)?.Id ?? 0;
        }
        if (dissTid == 0)
            throw new ApiException("feed 中未找到\"每日30首\"入口卡片", -1);

        var songs = await GetPlaylistTracksAsync(dissTid, ct).ConfigureAwait(false);
        if (songs.Count == 0)
            throw new ApiException("每日30首歌单返回为空", -1);
        return songs;
    }

    /// <summary>雷达流日推(官方路径回落):GetRadarSong ReqType=2 串行翻页收集 RecommendSongIds
    /// (每页约 5 首,时间窗内随机洗牌、跨页基本不重叠),攒够超额样本后经 pf_song_detail_svr
    /// 批量解析前 30 首。业务码非 0 抛 ApiException;零数据同样抛 —— 现网对未登录/新账号
    /// 返回的是"空成功"而非错误码。</summary>
    private async Task<List<Song>> GetDailyFromRadarAsync(CancellationToken ct)
    {
        const int TargetCount = 30, Oversample = 40, MaxPages = 12;
        var ids = new List<long>();
        var seen = new HashSet<long>();
        for (var page = 1; page <= MaxPages && ids.Count < Oversample; page++)
        {
            using var doc = await PostMusicuAsync(w =>
            {
                WriteModuleReq(w, "music.recommend.TrackRelationServer", "GetRadarSong", p =>
                {
                    p.WriteNumber("Page", page);
                    p.WriteNumber("ReqType", 2);
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

            foreach (var id in req.Data?.RecommendSongIds ?? new List<long>())
                if (id != 0 && seen.Add(id)) ids.Add(id);
            if (req.Data?.HasMore != true) break;
        }
        if (ids.Count == 0)
            throw new ApiException("QQ 音乐未返回每日推荐数据(未登录或账号暂无推荐内容)", -1);
        return await ResolveSongsByIdsAsync(ids.Take(TargetCount), ct).ConfigureAwait(false);
    }

    /// <summary>按数字 id 批量取完整曲目:pf_song_detail_svr/get_song_detail_yqq 一条一个子请求,
    /// 单 POST 合并 ≤15 条省 RTT;单个子请求失败静默跳过。顺带填充 mid 缓存(MapTrack)。</summary>
    private async Task<List<Song>> ResolveSongsByIdsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var songs = new List<Song>();
        foreach (var chunk in ChunkList(ids.ToList(), 15))
        {
            using var doc = await PostMusicuMultiAsync(chunk.Select((id, i) =>
                (Action<Utf8JsonWriter>)(w => WriteModuleReq(w, $"req_{i}",
                    "music.pf_song_detail_svr", "get_song_detail_yqq", p =>
                    {
                        p.WriteNumber("song_type", 0);
                        p.WriteString("song_mid", "");
                        p.WriteNumber("song_id", id);
                    }))).ToList(), ct).ConfigureAwait(false);

            foreach (var t in ExtractTracksByKeyPrefix(doc.RootElement, "req_"))
            {
                if (t is null) continue;
                var s = MapTrack(t);
                if (s.Id != 0 || s.Mid.Length > 0) songs.Add(s);
            }
        }
        return songs;
    }

    private static List<List<T>> ChunkList<T>(List<T> source, int size)
    {
        var chunks = new List<List<T>>();
        for (var i = 0; i < source.Count; i += size)
            chunks.Add(source.GetRange(i, Math.Min(size, source.Count - i)));
        return chunks;
    }

    /// <summary>从 musicu 复合响应里提取所有 req_* 子节点的 track_info(容错:缺字段/非 0 码的子请求跳过)。</summary>
    private static List<QQTrackDto?> ExtractTracksByKeyPrefix(JsonElement root, string keyPrefix)
    {
        var tracks = new List<QQTrackDto?>();
        foreach (var sub in root.EnumerateObject())
        {
            if (!sub.Name.StartsWith(keyPrefix, StringComparison.Ordinal) || sub.Value.ValueKind != JsonValueKind.Object)
                continue;
            var node = sub.Value;
            try
            {
                if (!node.TryGetProperty("code", out var codeEl) || codeEl.GetInt32() != 0) continue;
                if (!node.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                if (!data.TryGetProperty("track_info", out var tr) || tr.ValueKind == JsonValueKind.Null) continue;
                tracks.Add(tr.Deserialize(QQMusicJsonContext.Default.QQTrackDto));
            }
            catch
            {
                // 形状漂移的子请求跳过,不影响其余曲目
            }
        }
        return tracks;
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

    /// <summary>musicu.fcg JSON POST(单模块):{ 手写请求体(NativeAOT 安全),"loginUin","comm" };登录态带 Cookie。</summary>
    private Task<JsonDocument> PostMusicuAsync(Action<Utf8JsonWriter> writeReqs, CancellationToken ct)
        => PostMusicuMultiAsync(new List<Action<Utf8JsonWriter>> { writeReqs }, ct);

    /// <summary>musicu.fcg JSON POST(多模块复合):reqs[i] 各自写入一个 "req_{i}" 子对象;
    /// 服务端在同一响应里按相同键回包。</summary>
    private async Task<JsonDocument> PostMusicuMultiAsync(IReadOnlyList<Action<Utf8JsonWriter>> reqs, CancellationToken ct)
    {
        string json;
        using (var ms = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                for (var i = 0; i < reqs.Count; i++)
                    reqs[i](w);
                w.WriteString("loginUin", _uin);
                w.WriteStartObject("comm");
                w.WriteString("uin", _uin);
                w.WriteString("format", "json");
                w.WriteNumber("ct", 24);
                w.WriteNumber("cv", 4747474);
                w.WriteNumber("g_tk", ComputeGtk());
                w.WriteNumber("g_tk_new_20200303", ComputeGtk());
                w.WriteEndObject();
                w.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(ms.ToArray());
        }

        // 登录态双通道鉴权:Cookie 头之外同时在 URL 附 uin/qm_keyst/g_tk —— asset 类模块(用户歌单/
        // 资料等)服务端可能从查询串取凭据(参考 multiPlatformMusicApi 的 query 认证方式),缺了会回 8xxxx。
        var url = MusicuUrl;
        if (IsLoggedIn)
            url += "?uin=" + Uri.EscapeDataString(_uin)
                   + "&qm_keyst=" + Uri.EscapeDataString(_authst)
                   + "&g_tk=" + ComputeGtk();

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (IsLoggedIn) req.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseJson(body);
    }

    /// <summary>写一个默认键(req_0)的模块子请求:"req_0":{ module, method, param:{...} }。</summary>
    private static void WriteModuleReq(Utf8JsonWriter w, string module, string method, Action<Utf8JsonWriter> writeParam)
        => WriteModuleReq(w, "req_0", module, method, writeParam);

    /// <summary>写一个自定义键的模块子请求(多模块复合 POST 用)。</summary>
    private static void WriteModuleReq(Utf8JsonWriter w, string key, string module, string method, Action<Utf8JsonWriter> writeParam)
    {
        w.WriteStartObject(key);
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

    /// <summary>g_tk(bkn):按登录模型取哈希输入 —— 旧模型(p_skey 在 cookie 里)用 p_skey;
    /// 新模型(无 p_skey,凭据是 qm_keyst/musickey,本机现网形态)用 qm_keyst,
    /// 与现网客户端(L-1124/QQMusicApi)一致。两者皆无(匿名)退回参考实现的固定值。
    /// 写类 asset 接口(红心 ag-1 通道)会严格校验 g_tk,给错回 500026。</summary>
    private int ComputeGtk()
    {
        var input = ExtractCookieValue(_cookieHeader, "p_skey");
        if (string.IsNullOrEmpty(input))
        {
            input = ExtractCookieValue(_cookieHeader, "qm_keyst")
                    ?? ExtractCookieValue(_cookieHeader, "qqmusic_key")
                    ?? _authst;
        }
        if (string.IsNullOrEmpty(input)) return 1124214810;
        unchecked
        {
            var hash = 5381;
            foreach (var ch in input) hash += (hash << 5) + ch;
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

    /// <summary>从多个模块业务码中挑一个代表性异常码:优先负数(协议错),其次任意非零,全零则 -1。</summary>
    private static int PickErrorCode(params int?[] codes)
        => codes.FirstOrDefault(c => c < 0) ?? codes.FirstOrDefault(c => c != 0) ?? -1;

    /// <summary>歌单名缺省占位:老通道偶发不下发名字,避免 UI 出现空白行。</summary>
    private static string OrUnnamed(string name) => name.Length > 0 ? name : "(未命名歌单)";
}
