using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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

    // 红心/喜欢状态:"我喜欢的音乐"歌单 id + 已喜欢曲目集合(懒加载缓存)。
    private long _likedPlaylistId;
    private HashSet<long>? _likedIds;
    private bool _likedLoading;
    private Task? _likedLoadTask;

    /// <summary>当前登录用户 uid(GetUserProfileAsync 填充,红心接口 userid 参数用)。</summary>
    private long _currentUserId;

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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.SongDetailResponse);
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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.PlayUrlResponse);
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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.SearchResponse);
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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.LyricResponse);
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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.LegacySearchResponse);
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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.LyricResponse);
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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.PlayUrlResponse);
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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.LegacyAccountResponse);
        if (resp is null || resp.Code != 200 || resp.Profile is null)
            throw new ApiException("获取用户信息失败(未登录或 cookie 失效)", resp?.Code ?? -1);
        _currentUserId = resp.Profile.UserId;
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
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.LegacyUserPlaylistResponse);
        if (resp is null || resp.Code != 200 || resp.Playlist is null)
            throw new ApiException("获取歌单失败", resp?.Code ?? -1);

        // 识别"我喜欢的音乐"(红心集合):specialType=5,兜底按名字
        _likedPlaylistId = resp.Playlist.FirstOrDefault(p => p.SpecialType == 5)?.Id
            ?? resp.Playlist.FirstOrDefault(p => p.Name == "我喜欢的音乐")?.Id
            ?? 0;

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

    /// <summary>歌单轨道概览：v6 单次请求即可拿到全量 trackIds(权威顺序) + 前段曲目(登录态约 150 首)。
    /// 不做任何 song/detail 补齐,供歌单页增量加载使用。CoverUrl = 当前封面(随曲目变化)。</summary>
    public sealed record PlaylistTrackOverview(IReadOnlyList<long> TrackIds, IReadOnlyList<Song> PrefixTracks, string CoverUrl);

    /// <summary>单次 v6 请求取歌单轨道概览,不补齐元数据。</summary>
    public async Task<PlaylistTrackOverview> GetPlaylistTrackOverviewAsync(long id, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v6/playlist/detail?id={id}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.PlaylistDetailResponse);
        var playlist = resp?.Playlist;
        if (resp is null || resp.Code != 200 || playlist is null)
            throw new ApiException("获取歌单详情失败", resp?.Code ?? -1);

        var trackIds = (playlist.TrackIds ?? Enumerable.Empty<TrackIdItem>()).Select(t => t.Id).ToList();
        var prefix = new List<Song>();
        foreach (var t in playlist.Tracks ?? Enumerable.Empty<SearchSong>())
            if (t.Id != 0) prefix.Add(MapSearchSong(t));
        return new PlaylistTrackOverview(trackIds, prefix, playlist.CoverImgUrl ?? "");
    }

    /// <summary>明文 /api/song/detail 批量取曲目元数据，单批 ≤100 首。供歌单增量加载补齐缺失段。</summary>
    public async Task<List<Song>> GetSongsByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new List<Song>();
        return await GetSongDetailsLegacyAsync(ids.Take(100).ToList(), ct).ConfigureAwait(false);
    }

    // ---------- 红心/喜欢 ----------

    /// <summary>登录后"我喜欢的音乐"歌单 id(0 = 未识别到/未登录)。</summary>
    public long LikedPlaylistId => _likedPlaylistId;

    /// <summary>懒加载已喜欢曲目 id 集合(单飞,幂等)。未登录或未识别到喜欢歌单时为空集。</summary>
    public Task EnsureLikedIdsAsync(CancellationToken ct = default)
    {
        if (_likedPlaylistId == 0 || _likedIds is not null) return Task.CompletedTask;
        if (_likedLoading) return _likedLoadTask ?? Task.CompletedTask;
        _likedLoading = true;
        return _likedLoadTask = LoadLikedIdsAsync(ct);
    }

    private async Task LoadLikedIdsAsync(CancellationToken ct)
    {
        try
        {
            var overview = await GetPlaylistTrackOverviewAsync(_likedPlaylistId, ct).ConfigureAwait(false);
            _likedIds = new HashSet<long>(overview.TrackIds);
        }
        catch
        {
            // 失败降级为空集合,避免反复请求
            _likedIds ??= new HashSet<long>();
        }
        finally
        {
            _likedLoading = false;
        }
    }

    /// <summary>已喜欢集合是否含该曲目(集合未加载时返回 false)。</summary>
    public bool IsLiked(long id) => _likedIds?.Contains(id) ?? false;

    /// <summary>切换红心:返回切换后状态(成功);失败抛 ApiException(调用方回滚 UI)。
    /// 乐观更新在调用方,这里只负责请求 + 更新内部集合。</summary>
    public async Task<bool> LikeToggleAsync(long id, CancellationToken ct = default)
    {
        if (_likedPlaylistId == 0)
            throw new ApiException("未识别到'我喜欢的音乐'歌单(请先登录)", -1);
        await EnsureLikedIdsAsync(ct).ConfigureAwait(false);
        var target = !IsLiked(id);
        await LikeRequestAsync(id, target, ct).ConfigureAwait(false);
        _likedIds ??= new HashSet<long>();
        if (target) _likedIds.Add(id); else _likedIds.Remove(id);
        return target;
    }

    /// <summary>明文 POST /api/song/like 设置红心状态(不走 weapi,规避本机风控)。
    /// 参数名必须是 trackId/userid/like —— 用 id 会返回 code:400 参数错误(已实测)。</summary>
    private async Task LikeRequestAsync(long id, bool like, CancellationToken ct)
    {
        if (_currentUserId == 0)
            throw new ApiException("未登录(缺少用户 id)", -1);
        var url = $"{BaseUrl}/api/song/like?csrf_token=";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        req.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("trackId", id.ToString()),
            new KeyValuePair<string, string>("userid", _currentUserId.ToString()),
            new KeyValuePair<string, string>("like", like ? "true" : "false"),
        });
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 200) throw new ApiException($"红心操作失败(code={code})", code);
    }

    // ---------- 歌手 / 专辑详情(明文 GET) ----------

    /// <summary>歌手详情(姓名/头像)。明文 GET /api/artist/head/info/get。</summary>
    public async Task<ArtistDetailInfo> GetArtistAsync(long id, CancellationToken ct = default)
    {
        var resp = await GetJsonAsync($"{BaseUrl}/api/artist/head/info/get?id={id}", NetEaseJsonContext.Default.ArtistDetailResponse, ct).ConfigureAwait(false);
        var a = resp?.Data?.Artist;
        if (resp is null || resp.Code != 200 || a is null) throw new ApiException("获取歌手信息失败", resp?.Code ?? -1);
        return a;
    }

    /// <summary>歌手热门歌曲。</summary>
    public async Task<List<Song>> GetArtistSongsAsync(long id, int count = 30, CancellationToken ct = default)
    {
        var resp = await GetJsonAsync($"{BaseUrl}/api/artist/top/song?id={id}&limit={count}", NetEaseJsonContext.Default.ArtistTopSongsResponse, ct).ConfigureAwait(false);
        if (resp is null || resp.Code != 200 || resp.Songs is null) return new();
        return resp.Songs.Select(MapSearchSong).ToList();
    }

    /// <summary>歌手专辑列表(专辑/单曲/EP,按 Type 字符串区分)。明文 GET /api/artist/albums/{id},
    /// 单页约 30 条,响应 more=true 时按 offset 翻页拉全。老路径 /api/artist/album?id= 已废弃(恒返回 400)。</summary>
    public async Task<List<ArtistAlbumItem>> GetArtistAlbumsAsync(long id, int limit = 50, CancellationToken ct = default)
    {
        var all = new List<ArtistAlbumItem>();
        const int pageSize = 30;
        var offset = 0;
        while (all.Count < limit)
        {
            var take = Math.Min(pageSize, limit - all.Count);
            var resp = await GetJsonAsync(
                $"{BaseUrl}/api/artist/albums/{id}?offset={offset}&limit={take}",
                NetEaseJsonContext.Default.ArtistAlbumsResponse, ct).ConfigureAwait(false);
            if (resp is null || resp.Code != 200 || resp.HotAlbums is null) break;
            all.AddRange(resp.HotAlbums);
            if (!resp.More || resp.HotAlbums.Count == 0) break; // 无更多或返回空,停止翻页
            offset += resp.HotAlbums.Count;
        }
        return all;
    }

    /// <summary>专辑详情:信息 + 全量曲目。</summary>
    public sealed record AlbumDetailResult(AlbumDetailInfo Info, IReadOnlyList<Song> Songs);

    public async Task<AlbumDetailResult> GetAlbumAsync(long id, CancellationToken ct = default)
    {
        var resp = await GetJsonAsync($"{BaseUrl}/api/v1/album/{id}", NetEaseJsonContext.Default.AlbumDetailResponse, ct).ConfigureAwait(false);
        if (resp is null || resp.Code != 200 || resp.Album is null) throw new ApiException("获取专辑信息失败", resp?.Code ?? -1);
        return new AlbumDetailResult(resp.Album, (resp.Songs ?? Enumerable.Empty<SearchSong>()).Select(MapSearchSong).ToList());
    }

    /// <summary>歌单详情 → 全量曲目。v6 接口返回全量 trackIds + 前段曲目,
    /// 缺的部分用明文 /api/song/detail 分批补齐。此前用 v1 playlist/detail 只回前 ~150 首,
    /// 导致大歌单(如“我喜欢的音乐”)曲目不全。</summary>
    public async Task<List<Song>> GetPlaylistDetailAsync(long id, CancellationToken ct = default)
    {
        var overview = await GetPlaylistTrackOverviewAsync(id, ct).ConfigureAwait(false);
        var byId = new Dictionary<long, Song>();
        foreach (var s in overview.PrefixTracks) byId[s.Id] = s;

        // 补齐 trackIds 里尚缺的完整曲目(分批明文 song/detail)
        const int batchSize = 100;
        var missing = overview.TrackIds.Where(id => !byId.ContainsKey(id)).ToList();
        for (var i = 0; i < missing.Count; i += batchSize)
        {
            var slice = missing.Skip(i).Take(batchSize).ToList();
            foreach (var s in await GetSongsByIdsAsync(slice, ct).ConfigureAwait(false))
                byId[s.Id] = s;
        }

        return overview.TrackIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    /// <summary>取歌单前 N 首曲目(推荐页预览用,避免拉全量 200+ 首)。
    /// v6 的 tracks 已含前段(匿名约 10 首),不足时从 trackIds 前 N 补齐 song/detail。</summary>
    public async Task<List<Song>> GetPlaylistTracksAsync(long id, int count, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v6/playlist/detail?id={id}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize<PlaylistDetailResponse>(NetEaseJsonContext.Default.PlaylistDetailResponse);
        var playlist = resp?.Playlist;
        if (resp is null || resp.Code != 200 || playlist is null)
            throw new ApiException("获取歌单详情失败", resp?.Code ?? -1);

        var songs = (playlist.Tracks ?? Enumerable.Empty<SearchSong>()).Take(count).Select(MapSearchSong).ToList();
        if (songs.Count >= count) return songs;

        // 前段不足,从 trackIds 前 count 补齐
        var have = new HashSet<long>(songs.Select(s => s.Id));
        var missing = (playlist.TrackIds ?? Enumerable.Empty<TrackIdItem>())
            .Select(t => t.Id).Take(count).Where(id => !have.Contains(id)).ToList();
        if (missing.Count > 0)
            songs.AddRange(await GetSongDetailsLegacyAsync(missing, ct).ConfigureAwait(false));
        return songs.Take(count).ToList();
    }

    /// <summary>明文 /api/song/detail 批量取曲目(legacy 格式:artists/album/duration)。</summary>
    private async Task<List<Song>> GetSongDetailsLegacyAsync(List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new List<Song>();
        var url = $"{BaseUrl}/api/song/detail?ids={Uri.EscapeDataString($"[{string.Join(",", ids)}]")}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.LegacySongDetailResponse);
        return resp?.Songs?.Select(MapLegacySong).ToList() ?? new List<Song>();
    }

    // ---------- 首页推荐(明文 GET,匿名可用) ----------

    /// <summary>推荐歌单(personalized/playlist)。</summary>
    public async Task<List<RecommendItem>> GetPersonalizedPlaylistsAsync(int limit = 6, CancellationToken ct = default)
    {
        var resp = await GetJsonAsync($"{BaseUrl}/api/personalized/playlist?limit={limit}", NetEaseJsonContext.Default.RecommendListResponse, ct).ConfigureAwait(false);
        if (resp is null || resp.Code != 200 || resp.Result is null) return new();
        return resp.Result.Select(MapPlaylistCard).ToList();
    }

    /// <summary>猜你喜欢 / 新歌推荐(personalized/newsong)。</summary>
    public async Task<List<RecommendItem>> GetNewSongsAsync(int limit = 6, CancellationToken ct = default)
    {
        var resp = await GetJsonAsync($"{BaseUrl}/api/personalized/newsong?limit={limit}", NetEaseJsonContext.Default.RecommendListResponse, ct).ConfigureAwait(false);
        if (resp is null || resp.Code != 200 || resp.Result is null) return new();
        return resp.Result.Select(MapSongCard).ToList();
    }

    /// <summary>每日推荐(需登录;未登录接口返回 code 301 → 空列表,由调用方隐藏该区块)。</summary>
    public async Task<List<RecommendItem>> GetDailyRecommendAsync(CancellationToken ct = default)
    {
        var resp = await GetJsonAsync($"{BaseUrl}/api/v1/discovery/recommend/resource", NetEaseJsonContext.Default.RecommendResourceResponse, ct).ConfigureAwait(false);
        if (resp is null || resp.Code != 200 || resp.Recommend is null) return new();
        return resp.Recommend.Select(MapPlaylistCard).ToList();
    }

    /// <summary>每日歌曲推荐(需登录;匿名或被风控时返回空,由调用方隐藏该区块)。
    /// 明文 GET 试拉,失败/空由调用方兜底为每日推荐歌单卡片。</summary>
    public async Task<List<Song>> GetDailyRecommendSongsAsync(CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v3/discovery/recommend/songs?csrf_token=";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        var resp = doc.RootElement.Deserialize(NetEaseJsonContext.Default.DailySongsResponse);
        return resp?.Data?.DailySongs?.Select(MapSearchSong).ToList() ?? new List<Song>();
    }

    private static RecommendItem MapPlaylistCard(RecommendItemDto d)
    {
        var subtitle = string.IsNullOrEmpty(d.Copywriter) ? FormatPlayCount(d.PlayCount) : d.Copywriter;
        return new RecommendItem(d.Id, d.Name, subtitle, d.PicUrl, (long)d.PlayCount);
    }

    private static RecommendItem MapSongCard(RecommendItemDto d)
    {
        var name = d.Song?.Name is { Length: > 0 } n ? n : d.Name;
        var artists = d.Song?.Artists is { Count: > 0 } ? string.Join("/", d.Song.Artists.Select(a => a.Name)) : "";
        return new RecommendItem(d.Id, name, artists, d.PicUrl);
    }

    /// <summary>播放量格式化(1.2亿播放 / 23.4万播放 / 999播放)。卡片角标与副标题共用。</summary>
    public static string FormatPlayCount(double count)
    {
        if (count >= 1e8) return $"{count / 1e8:0.#}亿播放";
        if (count >= 1e4) return $"{count / 1e4:0.#}万播放";
        return $"{count:0}播放";
    }

    private async Task<T?> GetJsonAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(req, includeRealIp: false);
        using var doc = await PostJsonAsync(req, ct).ConfigureAwait(false);
        return doc.RootElement.Deserialize(typeInfo);
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
        ArtistIds = s.Artists is { Count: > 0 } ? s.Artists.Select(a => a.Id).ToList() : new List<long>(),
        ArtistNames = s.Artists is { Count: > 0 } ? s.Artists.Select(a => a.Name).ToList() : new List<string>(),
        AlbumId = s.Album?.Id ?? 0,
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
        ArtistIds = s.Artists is { Count: > 0 } ? s.Artists.Select(a => a.Id).ToList() : new List<long>(),
        ArtistNames = s.Artists is { Count: > 0 } ? s.Artists.Select(a => a.Name).ToList() : new List<string>(),
        AlbumId = s.Album?.Id ?? 0,
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
        ArtistIds = s.Artists is { Count: > 0 } ? s.Artists.Select(a => a.Id).ToList() : new List<long>(),
        ArtistNames = s.Artists is { Count: > 0 } ? s.Artists.Select(a => a.Name).ToList() : new List<string>(),
        AlbumId = s.Album?.Id ?? 0,
    };
}
