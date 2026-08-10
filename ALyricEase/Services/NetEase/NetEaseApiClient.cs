using System.Net;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services.Auth;
using ALyricEase.Services.Crypto;

namespace ALyricEase.Services.NetEase;

/// <summary>网易云 API 客户端:weapi/eapi 加密请求、匿名 cookie 管理、CN IP 风控头、各端点方法。
/// 所有端点集中在类内,接口字段变动只改这一处。</summary>
public sealed class NetEaseApiClient
{
    private const string BaseUrl = "https://music.163.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly CookieContainer _cookieContainer;
    private readonly CryptoService _crypto;
    private readonly CnIpPool _ipPool;
    private readonly CookieStore _cookie;

    /// <summary>加密 POST 被风控拦截(HTTP 200 空 body)时的内部错误码,触发明文接口回落。</summary>
    private const int BlockedCode = -3;

    /// <summary>已确认 weapi/eapi 被风控拦截后置 true,后续请求直接走明文接口,省掉重复空请求。</summary>
    private bool _wafBlocked;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public NetEaseApiClient(CryptoService crypto, CnIpPool ipPool, CookieStore cookie)
    {
        _crypto = crypto;
        _ipPool = ipPool;
        _cookie = cookie;

        _cookieContainer = new CookieContainer();
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = _cookieContainer,
            UseCookies = true,
        });
        _http.Timeout = TimeSpan.FromSeconds(20);

        RestoreCookies();
    }

    public bool IsLoggedIn => _cookie.MusicU is { Length: > 0 };

    /// <summary>设置登录用户的 MUSIC_U 并持久化。</summary>
    public void SetMusicUCookie(string musicU)
    {
        musicU = musicU.Trim();
        if (musicU.Length == 0) throw new ApiException("MUSIC_U 为空", -1);

        // 容忍用户贴整段 cookie,只取 MUSIC_U= 之后到 ; 的部分
        var idx = musicU.IndexOf("MUSIC_U=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            musicU = musicU[(idx + "MUSIC_U=".Length)..];
            var semi = musicU.IndexOf(';');
            if (semi >= 0) musicU = musicU[..semi];
        }

        _cookie.MusicU = musicU;
        _cookie.Save();
        AddCookie("MUSIC_U", musicU);
    }

    /// <summary>确保有未过期的匿名 cookie;无则调 /weapi/register/anonimous 注册。</summary>
    public async Task EnsureAnonymousAsync(CancellationToken ct = default)
    {
        if (_cookie.AnonymousMusicA is { Length: > 0 } && _cookie.AnonymousExpiresUtc > DateTime.UtcNow)
            return;

        var payload = new Dictionary<string, object?> { ["csrf_token"] = "" };
        using var req = CreateWeapiRequest("weapi/register/anonimous", payload, includeRealIp: true);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ApiException($"请求失败 HTTP {(int)resp.StatusCode}: {bodyText}", (int)resp.StatusCode);
        if (string.IsNullOrWhiteSpace(bodyText))
            throw new ApiException("响应为空(加密通道被风控拦截)", BlockedCode);

        var cookies = _cookieContainer.GetCookies(new Uri(BaseUrl));
        var musicA = cookies["MUSIC_A"]?.Value;
        if (string.IsNullOrEmpty(musicA))
            throw new ApiException($"匿名注册未返回 MUSIC_A cookie, body={Truncate(bodyText, 200)}", -2);

        _cookie.AnonymousMusicA = musicA;
        _cookie.AnonymousExpiresUtc = DateTime.UtcNow.AddDays(14);
        _cookie.Save();
    }

    // ---------- 端点方法 ----------

    public async Task<List<Song>> SearchAsync(string keyword, int limit = 30, int offset = 0, CancellationToken ct = default)
    {
        if (!_wafBlocked)
        {
            try
            {
                return await SearchWeapiAsync(keyword, limit, offset, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.Code == BlockedCode)
            {
                _wafBlocked = true; // 本网络加密通道被拦,后续直接走明文
            }
        }
        return await SearchLegacyAsync(keyword, limit, offset, ct).ConfigureAwait(false);
    }

    /// <summary>获取播放地址。匿名时免费歌可得 standard(128k);VIP 曲返回试听或 null。
    /// 自动降级:higher 拿不到 → standard 再试一次。eapi 被风控拦截时回落明文 GET。</summary>
    public async Task<PlayUrlItem?> GetPlayUrlAsync(long id, string level = "higher", CancellationToken ct = default)
    {
        if (!_wafBlocked)
        {
            try
            {
                await EnsureAnonymousAsync(ct).ConfigureAwait(false);
                var item = await TryPlayUrlEapiAsync(id, level, ct).ConfigureAwait(false);
                item ??= await TryPlayUrlEapiAsync(id, "standard", ct).ConfigureAwait(false);
                if (item is { Url.Length: > 0 }) return item;
            }
            catch (ApiException ex) when (ex.Code == BlockedCode)
            {
                _wafBlocked = true;
            }
        }

        // 明文回落,保留 higher→standard 降级
        var legacy = await TryPlayUrlLegacyAsync(id, level, ct).ConfigureAwait(false);
        if (legacy is null || string.IsNullOrEmpty(legacy.Url))
        {
            if (!string.Equals(level, "standard", StringComparison.OrdinalIgnoreCase))
                legacy = await TryPlayUrlLegacyAsync(id, "standard", ct).ConfigureAwait(false);
        }
        return legacy;
    }

    public async Task<LyricResult?> GetLyricAsync(long id, CancellationToken ct = default)
    {
        if (!_wafBlocked)
        {
            try
            {
                return await LyricWeapiAsync(id, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.Code == BlockedCode)
            {
                _wafBlocked = true;
            }
        }
        return await LyricLegacyAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>歌曲详情(v3/song/detail),字段为 artists/album。</summary>
    public async Task<Song?> GetSongDetailAsync(long id, CancellationToken ct = default)
    {
        await EnsureAnonymousAsync(ct).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["c"] = $"[{{\"id\":{id}}}]",
            ["csrf_token"] = "",
        };
        using var req = CreateWeapiRequest("weapi/v3/song/detail", payload, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<SongDetailResponse>(JsonOpts);
        if (resp is null || resp.Code != 200)
            throw new ApiException("获取歌曲详情失败", resp?.Code ?? -1);
        var item = resp.Songs?.FirstOrDefault();
        return item is null ? null : MapDetailSong(item);
    }

    // ---------- 内部 ----------

    private async Task<PlayUrlItem?> TryPlayUrlEapiAsync(long id, string level, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ids"] = $"[{id}]",
            ["level"] = level,
        };
        // 播放地址走 eapi,带随机 CN IP 防海外风控
        using var req = CreateEapiRequest("/api/song/enhance/player/url/v1", payload, includeRealIp: true);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<PlayUrlResponse>(JsonOpts);
        if (resp is null || resp.Code != 200 || resp.Data is null)
            return null;
        return resp.Data.FirstOrDefault(i => i.Id == id) ?? resp.Data.FirstOrDefault();
    }

    // ---------- weapi 主路径 ----------

    private async Task<List<Song>> SearchWeapiAsync(string keyword, int limit, int offset, CancellationToken ct)
    {
        await EnsureAnonymousAsync(ct).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["s"] = keyword,
            ["type"] = 1,
            ["limit"] = limit,
            ["offset"] = offset,
            ["csrf_token"] = "",
        };
        using var req = CreateWeapiRequest("weapi/cloudsearch/get/web", payload, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<SearchResponse>(JsonOpts);
        if (resp is null || resp.Code != 200 || resp.Result?.Songs is null)
            throw new ApiException("搜索失败", resp?.Code ?? -1);
        return resp.Result.Songs.Select(MapSearchSong).ToList();
    }

    private async Task<LyricResult?> LyricWeapiAsync(long id, CancellationToken ct)
    {
        await EnsureAnonymousAsync(ct).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["os"] = "pc",
            ["lv"] = -1,
            ["kv"] = -1,
            ["tv"] = -1,
            ["csrf_token"] = "",
        };
        using var req = CreateWeapiRequest("weapi/song/lyric", payload, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<LyricResponse>(JsonOpts);
        if (resp is null || resp.Code != 200)
            throw new ApiException("获取歌词失败", resp?.Code ?? -1);
        return new LyricResult
        {
            Original = resp.Lrc?.Lyric ?? "",
            Translation = resp.TLyric?.Lyric ?? "",
        };
    }

    // ---------- 明文 GET 回落路径(weapi/eapi 被风控拦截时) ----------

    private async Task<List<Song>> SearchLegacyAsync(string keyword, int limit, int offset, CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/search/get/web?s={Uri.EscapeDataString(keyword)}&type=1&limit={limit}&offset={offset}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<LegacySearchResponse>(JsonOpts);
        if (resp is null || resp.Code != 200 || resp.Result?.Songs is null)
            throw new ApiException("搜索失败", resp?.Code ?? -1);
        return resp.Result.Songs.Select(MapLegacySong).ToList();
    }

    private async Task<LyricResult?> LyricLegacyAsync(long id, CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<LyricResponse>(JsonOpts);
        if (resp is null || resp.Code != 200)
            throw new ApiException("获取歌词失败", resp?.Code ?? -1);
        return new LyricResult
        {
            Original = resp.Lrc?.Lyric ?? "",
            Translation = resp.TLyric?.Lyric ?? "",
        };
    }

    /// <summary>明文播放地址。免费歌可用;VIP 返回 code -110 / url null(由调用方降级)。</summary>
    private async Task<PlayUrlItem?> TryPlayUrlLegacyAsync(long id, string level, CancellationToken ct)
    {
        var br = LevelToBr(level);
        var url = $"{BaseUrl}/api/song/enhance/player/url?ids=[{id}]&br={br}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: true); // 带随机 CN IP 防海外风控
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<PlayUrlResponse>(JsonOpts);
        if (resp is null || resp.Code != 200 || resp.Data is null)
            return null;
        return resp.Data.FirstOrDefault(i => i.Id == id) ?? resp.Data.FirstOrDefault();
    }

    /// <summary>level → br 码率:lossless/hires→999000,higher→320000,standard/其他→128000。</summary>
    private static int LevelToBr(string level) => level.ToLowerInvariant() switch
    {
        "hires" or "lossless" => 999000,
        "higher" => 320000,
        _ => 128000,
    };

    // ---------- 登录用户 / 歌单(明文 GET,需 MUSIC_U cookie) ----------

    /// <summary>当前登录用户资料(MUSIC_U 缺失会抛"未登录")。</summary>
    public async Task<UserProfile> GetUserProfileAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/nuser/account/get";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<LegacyAccountResponse>(JsonOpts);
        if (resp is null || resp.Code != 200 || resp.Profile is null)
            throw new ApiException("获取用户信息失败(未登录或 cookie 失效)", resp?.Code ?? -1);
        return new UserProfile
        {
            UserId = resp.Profile.UserId,
            Nickname = resp.Profile.Nickname,
            AvatarUrl = resp.Profile.AvatarUrl,
        };
    }

    /// <summary>用户创建/收藏的歌单列表。</summary>
    public async Task<List<Playlist>> GetUserPlaylistsAsync(long uid, int limit = 50, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/user/playlist?uid={uid}&limit={limit}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<LegacyUserPlaylistResponse>(JsonOpts);
        if (resp is null || resp.Code != 200 || resp.Playlist is null)
            throw new ApiException("获取歌单失败", resp?.Code ?? -1);
        return resp.Playlist
            .Select(p => new Playlist
            {
                Id = p.Id,
                Name = p.Name,
                TrackCount = p.TrackCount,
                CoverUrl = p.CoverUrl,
            })
            .ToList();
    }

    /// <summary>歌单详情 → 曲目列表(一次全部,仅用于测试/小歌单)。</summary>
    public async Task<List<Song>> GetPlaylistDetailAsync(long id, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/playlist/detail?id={id}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<LegacyPlaylistDetailResponse>(JsonOpts);
        if (resp is null || resp.Code != 200 || resp.Result?.Tracks is null)
            throw new ApiException("获取歌单详情失败", resp?.Code ?? -1);
        return resp.Result.Tracks.Select(MapLegacySong).ToList();
    }

    // ---------- 首页推荐(明文 GET,匿名可用) ----------

    /// <summary>推荐歌单(personalized/playlist)。</summary>
    public async Task<List<RecommendItem>> GetPersonalizedPlaylistsAsync(int limit = 6, CancellationToken ct = default)
    {
        var resp = await GetJsonAsync<RecommendListResponse>($"{BaseUrl}/api/personalized/playlist?limit={limit}", ct).ConfigureAwait(false);
        if (resp is null || resp.Code != 200 || resp.Result is null) return new();
        return resp.Result.Select(MapPlaylistCard).ToList();
    }

    /// <summary>猜你喜欢 / 新歌推荐(personalized/newsong)。</summary>
    public async Task<List<RecommendItem>> GetNewSongsAsync(int limit = 6, CancellationToken ct = default)
    {
        var resp = await GetJsonAsync<RecommendListResponse>($"{BaseUrl}/api/personalized/newsong?limit={limit}", ct).ConfigureAwait(false);
        if (resp is null || resp.Code != 200 || resp.Result is null) return new();
        return resp.Result.Select(MapSongCard).ToList();
    }

    /// <summary>每日推荐(需登录;未登录接口返回 code 301 → 空列表,由调用方隐藏该区块)。</summary>
    public async Task<List<RecommendItem>> GetDailyRecommendAsync(CancellationToken ct = default)
    {
        var resp = await GetJsonAsync<RecommendResourceResponse>($"{BaseUrl}/api/v1/discovery/recommend/resource", ct).ConfigureAwait(false);
        if (resp is null || resp.Code != 200 || resp.Recommend is null) return new();
        return resp.Recommend.Select(MapPlaylistCard).ToList();
    }

    private static RecommendItem MapPlaylistCard(RecommendItemDto d)
    {
        var subtitle = string.IsNullOrEmpty(d.Copywriter) ? FormatPlayCount(d.PlayCount) : d.Copywriter;
        return new RecommendItem(d.Id, d.Name, subtitle, d.PicUrl);
    }

    private static RecommendItem MapSongCard(RecommendItemDto d)
    {
        var name = d.Song?.Name is { Length: > 0 } n ? n : d.Name;
        var artists = d.Song?.Artists is { Count: > 0 } ? string.Join("/", d.Song.Artists.Select(a => a.Name)) : "";
        return new RecommendItem(d.Id, name, artists, d.PicUrl);
    }

    private static string FormatPlayCount(double count)
    {
        if (count >= 1e8) return $"{count / 1e8:0.#}亿播放";
        if (count >= 1e4) return $"{count / 1e4:0.#}万播放";
        return $"{count:0}播放";
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        return doc.RootElement.Deserialize<T>(JsonOpts);
    }


    private HttpRequestMessage CreateWeapiRequest(string apiPath, IReadOnlyDictionary<string, object?> payload, bool includeRealIp)
    {
        var (params_, encSecKey) = _crypto.EncryptWeapi(payload);
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{apiPath}?csrf_token=");
        ApplyCommonHeaders(req, includeRealIp);
        req.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("params", params_),
            new KeyValuePair<string, string>("encSecKey", encSecKey),
        });
        return req;
    }

    private HttpRequestMessage CreateEapiRequest(string apiUrlPath, IReadOnlyDictionary<string, object?> payload, bool includeRealIp)
    {
        var params_ = _crypto.EncryptEapi(apiUrlPath, payload);
        // 明文里的 url 保留 /api/...;实际 HTTP 请求打到 /eapi/...
        var httpPath = apiUrlPath.StartsWith("/api/", StringComparison.Ordinal)
            ? "/eapi/" + apiUrlPath["/api/".Length..]
            : apiUrlPath;
        var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + httpPath);
        ApplyCommonHeaders(req, includeRealIp);
        req.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("params", params_),
        });
        return req;
    }

    private void ApplyCommonHeaders(HttpRequestMessage req, bool includeRealIp)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Referrer = new Uri("https://music.163.com");
        if (includeRealIp)
            req.Headers.TryAddWithoutValidation("X-Real-IP", _ipPool.Next());
    }

    private async Task<JsonDocument> PostJsonAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            throw new ApiException("响应为空(加密通道被风控拦截)", BlockedCode);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ApiException("响应不是有效 JSON", -2);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new ApiException($"请求失败 HTTP {(int)resp.StatusCode}: {body}", (int)resp.StatusCode);
    }

    private void RestoreCookies()
    {
        if (_cookie.MusicU is { Length: > 0 } u)
            AddCookie("MUSIC_U", u);
        if (_cookie.AnonymousMusicA is { Length: > 0 } a && _cookie.AnonymousExpiresUtc > DateTime.UtcNow)
            AddCookie("MUSIC_A", a);
    }

    private void AddCookie(string name, string value)
    {
        _cookieContainer.Add(new Uri(BaseUrl), new Cookie(name, value) { Path = "/" });
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private static Song MapLegacySong(LegacySearchSong s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Artist = s.Artists is { Count: > 0 } ? string.Join("/", s.Artists.Select(a => a.Name)) : "",
        Album = s.Album?.Name ?? "",
        CoverUrl = s.Album?.PicUrl ?? "",
        DurationMs = s.Duration,
        Fee = s.Fee,
    };

    private static Song MapSearchSong(SearchSong s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Artist = s.Artists is { Count: > 0 } ? string.Join("/", s.Artists.Select(a => a.Name)) : "",
        Album = s.Album?.Name ?? "",
        CoverUrl = s.Album?.PicUrl ?? "",
        DurationMs = s.DurationMs,
        Fee = s.Fee,
    };

    private static Song MapDetailSong(SongDetailItem s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Artist = s.Artists is { Count: > 0 } ? string.Join("/", s.Artists.Select(a => a.Name)) : "",
        Album = s.Album?.Name ?? "",
        CoverUrl = s.Album?.PicUrl ?? "",
        DurationMs = s.DurationMs,
        Fee = s.Fee,
    };
}
