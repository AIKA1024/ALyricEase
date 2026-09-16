using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;

namespace ALyricEase.Services;

/// <summary>
/// 统一媒体磁盘缓存：音乐、封面、原文歌词和翻译共享容量上限与 LRU 淘汰。
/// 每首歌只长期保留最高已缓存音质；高音质缓存可以满足较低音质播放请求。
/// </summary>
public sealed class MusicCacheService
{
    public const int DefaultMaximumSizeMb = 1024;
    public const int MinimumAllowedSizeMb = 128;
    public const int MaximumAllowedSizeMb = 10240;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string OfflineIndexFileName = "offline-index.json";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _cacheDirectory;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(2, 2);
    private readonly object _pinGate = new();
    private readonly Dictionary<string, int> _pinCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deleteWhenReleased = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _downloads = new(StringComparer.Ordinal);
    // 一次性页面快照的内存副本(纯数据,不含视觉树/ViewModel)。快照 key 是每次捕获时新生成的 GUID,
    // 只存在进程内的导航历史里 —— 也就是说它天生只服务当前进程,落盘没有任何额外收益。
    // 所以这里只留内存:读取是同步的,返回时不等待任何 I/O(旧实现还会派发一次延迟落盘,已移除)。
    private readonly SnapshotMemoryCache<PlaylistPageCacheData> _playlistSnapshots = new(MaxInMemorySnapshots);
    private readonly SnapshotMemoryCache<DetailPageCacheData> _detailSnapshots = new(MaxInMemorySnapshots);
    // 容量要覆盖"返回栈的常见深度":返回是 LIFO 的,不够大时深层返回取不到快照、只能回退常规加载。
    // 导航历史上限 50,这里取 32 —— 单页 600 首的纯数据约 131KB,32 页合计约 4MB/类,
    // 换来的是淘汰几乎不发生(旧值 8 会让第 9 层往后的返回全部落空)。
    internal const int MaxInMemorySnapshots = 32;
    // 容量裁剪要 stat + 排序整个缓存目录(真实用户可达上万文件,实测一次空跑也要 550ms),
    // 所以同一条路径按时间窗口节流;需要立即生效的调用(改缓存上限)传 force。
    private const int TrimIntervalMs = 5000;
    private long _lastTrimAt;
    private int _maximumSizeMb;
    private int _clearGeneration;
    private MusicCacheIndexFile _offlineIndex;

    public MusicCacheService(AppStateStore state)
        : this(state.MusicCacheMaximumSizeMb, GetDefaultCacheDirectory(), SharedHttp)
    {
    }

    internal MusicCacheService(int maximumSizeMb, string cacheDirectory, HttpClient http)
    {
        _maximumSizeMb = NormalizeMaximumSizeMb(maximumSizeMb);
        _cacheDirectory = cacheDirectory;
        _http = http;
        Directory.CreateDirectory(_cacheDirectory);
        DeleteStaleTemporaryFiles();
        DeleteLegacyPageSnapshotFiles();
        _offlineIndex = LoadOfflineIndex();
        MigrateInlinePlaylistTracks();
        _ = TrimToLimitAsync();
    }

    public int MaximumSizeMb => Volatile.Read(ref _maximumSizeMb);

    public static int NormalizeMaximumSizeMb(int value) =>
        Math.Clamp(value, MinimumAllowedSizeMb, MaximumAllowedSizeMb);

    /// <summary>取得满足请求音质的本地音乐；更高音质也可直接满足较低音质请求。</summary>
    public MusicCacheLease? TryAcquire(Song song, string requestedQuality)
        => TryAcquire(song, GetQualityRank(song.Source, requestedQuality));

    /// <summary>取得本地最高可用音质，不要求满足当前在线音质偏好；供断网回退使用。</summary>
    public MusicCacheLease? TryAcquireBestAvailable(Song song) => TryAcquire(song, minimumRank: 0);

    /// <summary>只检查歌曲是否存在任意完整音频缓存，不创建播放租约。</summary>
    public bool IsAudioCached(Song song) => GetAudioCacheAvailability([song])[0];

    /// <summary>一次目录扫描返回整批歌曲的缓存状态，避免离线大歌单逐行枚举目录。</summary>
    public IReadOnlyList<bool> GetAudioCacheAvailability(IReadOnlyList<Song> songs)
    {
        _mutationGate.Wait();
        try
        {
            var cachedKeys = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(_cacheDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "a-*"))
                {
                    if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(path);
                    // a- + 64 位 SHA256 + - + 2 位音质等级 + 扩展名
                    if (name.Length >= 69 && name[66] == '-')
                        cachedKeys.Add(name.Substring(2, 64));
                }
            }
            return songs.Select(song => cachedKeys.Contains(BuildSongKey(song))).ToArray();
        }
        catch
        {
            return new bool[songs.Count];
        }
        finally { _mutationGate.Release(); }
    }

    private MusicCacheLease? TryAcquire(Song song, int minimumRank)
    {
        var songKey = BuildSongKey(song);
        _mutationGate.Wait();
        try
        {
            var candidate = GetAudioFiles(songKey)
                .Where(file => file.Rank >= minimumRank)
                .OrderByDescending(file => file.Rank)
                .FirstOrDefault();
            if (candidate is null) return null;

            File.SetLastWriteTimeUtc(candidate.Path, DateTime.UtcNow);
            lock (_pinGate)
            {
                if (!File.Exists(candidate.Path)) return null;
                _pinCounts.TryGetValue(candidate.Path, out var count);
                _pinCounts[candidate.Path] = count + 1;
            }
            return new MusicCacheLease(candidate.Path, candidate.Rank, Release);
        }
        catch
        {
            return null;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>缓存完整音乐；比现有缓存低或相同的结果丢弃，更高音质写入后替换旧文件。</summary>
    public Task CacheAsync(Song song, string actualQuality, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.CompletedTask;
        var songKey = BuildSongKey(song);
        var rank = GetQualityRank(song.Source, actualQuality);
        var downloadKey = $"audio:{songKey}:{rank}";
        var clearGeneration = Volatile.Read(ref _clearGeneration);
        var download = _downloads.GetOrAdd(
            downloadKey,
            _ => new Lazy<Task>(
                () => DownloadAndStoreAudioAsync(
                    downloadKey, songKey, rank, song.Source, url, clearGeneration),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return download.Value;
    }

    public Task<byte[]?> TryGetCoverAsync(string url) =>
        TryReadBytesAsync(BuildCoverFileName(url));

    public Task CacheCoverAsync(string url, byte[] bytes) =>
        StoreBytesAsync(BuildCoverFileName(url), bytes);

    public Task RemoveCoverAsync(string url) => RemoveFileAsync(BuildCoverFileName(url));

    /// <summary>读取保存在同一缓存项中的原文歌词和翻译。</summary>
    public async Task<LyricResult?> TryGetLyricAsync(Song song)
    {
        var fileName = BuildLyricFileName(song);
        var bytes = await TryReadBytesAsync(fileName).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadByte() != 1) throw new InvalidDataException("未知歌词缓存版本");
            return new LyricResult
            {
                Original = reader.ReadString(),
                Translation = reader.ReadString(),
            };
        }
        catch
        {
            await RemoveFileAsync(fileName).ConfigureAwait(false);
            return null;
        }
    }

    public Task CacheLyricAsync(Song song, LyricResult lyric)
    {
        if (string.IsNullOrEmpty(lyric.Original) && string.IsNullOrEmpty(lyric.Translation))
            return Task.CompletedTask;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(lyric.Original ?? string.Empty);
            writer.Write(lyric.Translation ?? string.Empty);
        }
        return StoreBytesAsync(BuildLyricFileName(song), stream.ToArray());
    }

    /// <summary>保存账号歌单列表快照；已有详情曲目会在列表刷新时保留。</summary>
    public Task CachePlaylistListAsync(
        MusicSource source,
        string userName,
        IReadOnlyList<Playlist> playlists)
    {
        var snapshot = playlists.Select(ToCachedPlaylist).ToList();
        return CachePlaylistListCoreAsync(source, userName, snapshot);
    }

    /// <summary>保存已成功解析的歌单曲目，供断网时打开详情并播放本地音频。</summary>
    public Task CachePlaylistTracksAsync(Playlist playlist, IReadOnlyList<Song> songs)
    {
        var playlistSnapshot = ToCachedPlaylist(playlist);
        // 先复制引用窗口，避免后台序列化时与继续分页追加同一个 List 竞争。
        var songSnapshot = songs.ToArray();
        return CachePlaylistTracksCoreAsync(
            playlistSnapshot, songSnapshot, Volatile.Read(ref _clearGeneration));
    }

    /// <summary>驻留即将离页的页面重数据:只留一份内存纯数据副本,返回时同步取回。
    /// 调用方既不等待内存写入,也不等待任何磁盘 I/O。
    /// (签名保留 Task 形式以兼容既有调用方;内部同步完成,await 不会产生线程跳转。)</summary>
    internal Task CachePlaylistPageSnapshotAsync(string snapshotKey, PlaylistPageCacheData snapshot)
    {
        _playlistSnapshots.Set(snapshotKey, snapshot);
        return Task.CompletedTask;
    }

    /// <summary>取回页面快照并从缓存移除。返回 null 表示这一层已经不在内存里(LRU 淘汰,或从未捕获),
    /// 由调用方(PlaylistViewModel.RestoreNavigationSnapshotAsync)回退常规加载。</summary>
    internal Task<PlaylistPageCacheData?> TryTakePlaylistPageSnapshotAsync(string snapshotKey)
    {
        _playlistSnapshots.TryTake(snapshotKey, out var cached);
        return Task.FromResult(cached);
    }

    internal Task DiscardPlaylistPageSnapshotAsync(string snapshotKey)
    {
        _playlistSnapshots.Remove(snapshotKey);
        return Task.CompletedTask;
    }

    /// <summary>驻留歌手/专辑详情页的一次性重数据(纯内存,与歌单页同策略)。</summary>
    internal Task CacheDetailPageSnapshotAsync(string snapshotKey, DetailPageCacheData snapshot)
    {
        _detailSnapshots.Set(snapshotKey, snapshot);
        return Task.CompletedTask;
    }

    /// <summary>取回详情页快照并移除;返回 null 由调用方回退网络加载。</summary>
    internal Task<DetailPageCacheData?> TryTakeDetailPageSnapshotAsync(string snapshotKey)
    {
        _detailSnapshots.TryTake(snapshotKey, out var cached);
        return Task.FromResult(cached);
    }

    internal Task DiscardDetailPageSnapshotAsync(string snapshotKey)
    {
        _detailSnapshots.Remove(snapshotKey);
        return Task.CompletedTask;
    }


    /// <summary>读取账号歌单快照。返回 null 表示该音源从未成功同步过。</summary>
    public CachedPlaylistLibrary? TryGetPlaylistLibrary(MusicSource source)
    {
        _mutationGate.Wait();
        try
        {
            var account = _offlineIndex.Accounts.FirstOrDefault(item => item.Source == (int)source);
            if (account is not { HasPlaylistList: true }) return null;
            return new CachedPlaylistLibrary(
                account.UserName,
                account.Playlists.Where(item => item.Listed).Select(ToPlaylist).ToList());
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>读取歌单最近一次成功解析的曲目快照。</summary>
    public async Task<IReadOnlyList<Song>> TryGetPlaylistTracksAsync(Playlist playlist)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_cacheDirectory, BuildPlaylistTracksFileName(playlist));
            if (File.Exists(path))
            {
                var file = JsonSerializer.Deserialize(
                    await File.ReadAllBytesAsync(path).ConfigureAwait(false),
                    MusicCacheJsonContext.Default.PlaylistTracksCacheFile);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return file?.Tracks.Select(ToSong).ToList() ?? [];
            }

            // 兼容迁移失败或旧文件仍内嵌 Tracks 的极端情况。
            var account = _offlineIndex.Accounts.FirstOrDefault(item => item.Source == (int)playlist.Source);
            return account?.Playlists.FirstOrDefault(item => item.Id == playlist.Id)
                       ?.Tracks.Select(ToSong).ToList()
                   ?? [];
        }
        catch
        {
            return [];
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public Task SetMaximumSizeMbAsync(int value)
    {
        Volatile.Write(ref _maximumSizeMb, NormalizeMaximumSizeMb(value));
        return TrimToLimitAsync(force: true);
    }

    /// <summary>清空全部媒体缓存；正在播放的音乐在租约释放后删除。</summary>
    public async Task ClearAsync()
    {
        Interlocked.Increment(ref _clearGeneration);
        _playlistSnapshots.Clear();
        _detailSnapshots.Clear();
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var path in EnumerateCacheFiles())
            {
                if (IsPinned(path))
                {
                    lock (_pinGate) _deleteWhenReleased.Add(path);
                    continue;
                }
                TryDelete(path);
            }
            _offlineIndex = new MusicCacheIndexFile();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal long GetCurrentSizeBytes() => EnumerateCacheFiles().Select(TryGetLength).Sum();

    internal int RetainedInlinePlaylistTrackCount => _offlineIndex.Accounts
        .SelectMany(account => account.Playlists)
        .Sum(playlist => playlist.Tracks.Count);

    internal static int GetQualityRank(MusicSource source, string quality)
    {
        quality = quality.Trim();
        if (source == MusicSource.QQ)
        {
            return quality.ToUpperInvariant() switch
            {
                "M500" or "STANDARD" => 1,
                "TL01" or "NAC" => 2,
                "M800" or "HIGHER" or "EXHIGH" => 3,
                "F000" or "LOSSLESS" or "FLAC" or "AUTO" => 4,
                _ => 1,
            };
        }

        return quality.ToLowerInvariant() switch
        {
            "standard" => 1,
            "higher" => 2,
            "exhigh" => 3,
            "lossless" or "auto" => 4,
            "hires" or "jyeffect" => 5,
            "jymaster" or "dolby" or "sky" => 6,
            _ => 1,
        };
    }

    private async Task DownloadAndStoreAudioAsync(
        string downloadKey,
        string songKey,
        int rank,
        MusicSource source,
        string url,
        int clearGeneration)
    {
        string? temporaryPath = null;
        var downloadGateEntered = false;
        try
        {
            await _downloadGate.WaitAsync().ConfigureAwait(false);
            downloadGateEntered = true;
            if (clearGeneration != Volatile.Read(ref _clearGeneration)) return;
            if (GetAudioFiles(songKey).Any(file => file.Rank >= rank)) return;

            var requestUrl = url;
            // ⚠️ #if ANDROID 在本程序集里是死分支(核心库只面向 net10.0),这段升级到 https
            // 从未生效。目前无害:AndroidManifest 里 usesCleartextTraffic="true" 允许明文。
            // 若哪天关掉明文,这里必须换成 OperatingSystem.IsAndroid() 才能真的起作用。
#if ANDROID
            if (requestUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                requestUrl = "https://" + requestUrl["http://".Length..];
#endif
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation(
                "Referer",
                source == MusicSource.QQ ? "https://y.qq.com/" : "https://music.163.com/");

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var maximumBytes = GetMaximumBytes();
            if (response.Content.Headers.ContentLength is > 0 and var declaredLength
                && declaredLength > maximumBytes)
                return;

            Directory.CreateDirectory(_cacheDirectory);
            temporaryPath = Path.Combine(
                _cacheDirectory,
                $"a-{songKey}-{rank:D2}.{Guid.NewGuid():N}.part");
            await using (var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var targetStream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await sourceStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > GetMaximumBytes()) return;
                    await targetStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                }
            }

            var fileLength = new FileInfo(temporaryPath).Length;
            var extension = GetAudioExtension(url, response.Content.Headers.ContentType?.MediaType);
            var finalPath = Path.Combine(_cacheDirectory, $"a-{songKey}-{rank:D2}{extension}");

            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (clearGeneration != Volatile.Read(ref _clearGeneration)) return;
                var existing = GetAudioFiles(songKey);
                if (existing.Any(file => file.Rank >= rank)) return;
                if (!CanMakeSpaceFor(fileLength)) return;
                foreach (var lowerQuality in existing) DeleteOrDefer(lowerQuality.Path);

                if (!EnsureSpaceFor(fileLength)) return;
                File.Move(temporaryPath, finalPath, overwrite: false);
                File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
                temporaryPath = null;
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        catch
        {
            // 缓存失败不影响当前在线流播放。
        }
        finally
        {
            if (temporaryPath is not null) TryDelete(temporaryPath);
            if (downloadGateEntered) _downloadGate.Release();
            _downloads.TryRemove(downloadKey, out _);
        }
    }

    private async Task CachePlaylistListCoreAsync(
        MusicSource source,
        string userName,
        List<CachedPlaylistFile> playlists)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var account = GetOrCreateCachedAccount(source);
            account.UserName = userName ?? "";
            account.HasPlaylistList = true;
            foreach (var playlist in playlists)
                playlist.Listed = true;

            // 保留从推荐/搜索打开过的公共歌单详情，但它们不进入账号侧栏。
            playlists.AddRange(account.Playlists.Where(item => !item.Listed
                && playlists.All(candidate => candidate.Id != item.Id)));
            account.Playlists = playlists;
            SaveOfflineIndexLocked();
        }
        catch
        {
            // 离线索引写入失败不影响在线页面。
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task CachePlaylistTracksCoreAsync(
        CachedPlaylistFile playlist,
        IReadOnlyList<Song> songs,
        int clearGeneration)
    {
        var prepared = await Task.Run(() =>
        {
            var tracks = songs.Select(ToCachedSong).ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new PlaylistTracksCacheFile { Tracks = tracks },
                MusicCacheJsonContext.Default.PlaylistTracksCacheFile);
            return (Tracks: tracks, Bytes: bytes);
        }).ConfigureAwait(false);

        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (clearGeneration != Volatile.Read(ref _clearGeneration)) return;
            var account = GetOrCreateCachedAccount((MusicSource)playlist.Source);
            var existingIndex = account.Playlists.FindIndex(item => item.Id == playlist.Id);
            if (existingIndex >= 0)
            {
                var existing = account.Playlists[existingIndex];
                playlist.Listed = existing.Listed;
            }
            playlist.TrackCount = Math.Max(playlist.TrackCount, prepared.Tracks.Count);
            playlist.Tracks = [];
            WriteJsonCacheFileLocked(
                BuildPlaylistTracksFileName((MusicSource)playlist.Source, playlist.Id),
                prepared.Bytes);
            // 必须原位替换：删除后 Add 会把每个打开过的歌单挪到末尾，离线重启后侧栏顺序随之改变。
            if (existingIndex >= 0)
                account.Playlists[existingIndex] = playlist;
            else
                account.Playlists.Add(playlist);
            SaveOfflineIndexLocked();
        }
        catch
        {
            // 离线索引写入失败不影响在线页面。
        }
        finally
        {
            _mutationGate.Release();
        }
        await TrimToLimitAsync().ConfigureAwait(false);
    }

    private CachedAccountFile GetOrCreateCachedAccount(MusicSource source)
    {
        var account = _offlineIndex.Accounts.FirstOrDefault(item => item.Source == (int)source);
        if (account is not null) return account;
        account = new CachedAccountFile { Source = (int)source };
        _offlineIndex.Accounts.Add(account);
        return account;
    }

    private MusicCacheIndexFile LoadOfflineIndex()
    {
        try
        {
            var path = Path.Combine(_cacheDirectory, OfflineIndexFileName);
            if (!File.Exists(path)) return new MusicCacheIndexFile();
            return JsonSerializer.Deserialize(
                       File.ReadAllText(path),
                       MusicCacheJsonContext.Default.MusicCacheIndexFile)
                   ?? new MusicCacheIndexFile();
        }
        catch
        {
            return new MusicCacheIndexFile();
        }
    }

    /// <summary>旧版把全部曲目常驻总索引；启动时一次性拆文件并清空内嵌列表。</summary>
    private void MigrateInlinePlaylistTracks()
    {
        var changed = false;
        foreach (var account in _offlineIndex.Accounts)
        foreach (var playlist in account.Playlists)
        {
            if (playlist.Tracks.Count == 0) continue;
            try
            {
                WriteJsonCacheFileLocked(
                    BuildPlaylistTracksFileName((MusicSource)playlist.Source, playlist.Id),
                    JsonSerializer.SerializeToUtf8Bytes(
                        new PlaylistTracksCacheFile { Tracks = playlist.Tracks },
                        MusicCacheJsonContext.Default.PlaylistTracksCacheFile));
                playlist.Tracks = [];
                changed = true;
            }
            catch
            {
                // 保留内嵌数据，下次启动继续迁移。
            }
        }
        if (changed) SaveOfflineIndexLocked();
    }

    private void WriteJsonCacheFileLocked(string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var path = Path.Combine(_cacheDirectory, fileName);
        var temporaryPath = path + $".{Guid.NewGuid():N}.part";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private void SaveOfflineIndexLocked()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var path = Path.Combine(_cacheDirectory, OfflineIndexFileName);
        var temporaryPath = path + ".part";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(_offlineIndex, MusicCacheJsonContext.Default.MusicCacheIndexFile));
        File.Move(temporaryPath, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    private async Task<byte[]?> TryReadBytesAsync(string fileName)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_cacheDirectory, fileName);
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return bytes;
        }
        catch
        {
            return null;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task StoreBytesAsync(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.LongLength > GetMaximumBytes()) return;
        var generation = Volatile.Read(ref _clearGeneration);
        string? temporaryPath = null;
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _clearGeneration)) return;
            Directory.CreateDirectory(_cacheDirectory);
            var finalPath = Path.Combine(_cacheDirectory, fileName);
            if (File.Exists(finalPath))
            {
                File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
                return;
            }
            if (!EnsureSpaceFor(bytes.LongLength)) return;

            temporaryPath = finalPath + $".{Guid.NewGuid():N}.part";
            await File.WriteAllBytesAsync(temporaryPath, bytes).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _clearGeneration)) return;
            File.Move(temporaryPath, finalPath, overwrite: false);
            File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
            temporaryPath = null;
        }
        catch
        {
        }
        finally
        {
            if (temporaryPath is not null) TryDelete(temporaryPath);
            _mutationGate.Release();
        }
    }

    private async Task RemoveFileAsync(string fileName)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try { TryDelete(Path.Combine(_cacheDirectory, fileName)); }
        finally { _mutationGate.Release(); }
    }

    /// <summary>把缓存裁到容量上限。默认按时间窗口节流:目录里通常上万文件,每次写入都全量
    /// stat + 排序会让"写几十 KB 的快照"也花约 550ms(实测 0 首与 600 首耗时几乎相同)。
    /// 需要立即按新上限生效的调用(改缓存上限)传 force。</summary>
    private async Task TrimToLimitAsync(bool force = false)
    {
        if (!force)
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastTrimAt);
            if (now - last < TrimIntervalMs) return;
            // 只有一个调用方拿到"执行权",窗口内的其他调用直接返回。
            if (Interlocked.CompareExchange(ref _lastTrimAt, now, last) != last) return;
        }

        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var files = GetEvictionCandidates();
            var total = files.Sum(file => file.Length);
            var maximum = GetMaximumBytes();
            foreach (var file in files)
            {
                if (total <= maximum) break;
                if (IsPinned(file.FullName)) continue;
                if (TryDelete(file.FullName)) total -= file.Length;
            }
        }
        catch
        {
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private bool EnsureSpaceFor(long incomingLength)
    {
        var maximum = GetMaximumBytes();
        if (incomingLength <= 0 || incomingLength > maximum) return false;
        var files = GetEvictionCandidates();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total + incomingLength <= maximum) break;
            if (IsPinned(file.FullName)) continue;
            if (TryDelete(file.FullName)) total -= file.Length;
        }
        return total + incomingLength <= maximum;
    }

    private bool CanMakeSpaceFor(long incomingLength)
    {
        if (incomingLength <= 0 || incomingLength > GetMaximumBytes()) return false;
        var pinnedBytes = GetEvictionCandidates()
            .Where(file => IsPinned(file.FullName))
            .Sum(file => file.Length);
        return pinnedBytes + incomingLength <= GetMaximumBytes();
    }

    private List<FileInfo> GetEvictionCandidates()
    {
        var result = new List<FileInfo>();
        foreach (var path in EnumerateCacheFiles())
        {
            if (string.Equals(Path.GetFileName(path), OfflineIndexFileName,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            try { result.Add(new FileInfo(path)); }
            catch { }
        }
        result.Sort((left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
        return result;
    }

    private List<AudioCacheFile> GetAudioFiles(string songKey)
    {
        var result = new List<AudioCacheFile>();
        if (!Directory.Exists(_cacheDirectory)) return result;
        var prefix = $"a-{songKey}-";
        try
        {
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, prefix + "*"))
            {
                if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                var stem = Path.GetFileNameWithoutExtension(path);
                if (stem.Length < prefix.Length + 2
                    || !int.TryParse(stem.AsSpan(prefix.Length, 2), out var rank))
                    continue;
                result.Add(new AudioCacheFile(path, rank));
            }
        }
        catch
        {
        }
        return result;
    }

    private IEnumerable<string> EnumerateCacheFiles()
    {
        if (!Directory.Exists(_cacheDirectory)) return [];
        try
        {
            return Directory.EnumerateFiles(_cacheDirectory)
                .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void DeleteOrDefer(string path)
    {
        if (IsPinned(path))
        {
            lock (_pinGate) _deleteWhenReleased.Add(path);
        }
        else
        {
            TryDelete(path);
        }
    }

    private void Release(string path)
    {
        var shouldDelete = false;
        lock (_pinGate)
        {
            if (_pinCounts.TryGetValue(path, out var count) && count > 1)
                _pinCounts[path] = count - 1;
            else
            {
                _pinCounts.Remove(path);
                shouldDelete = _deleteWhenReleased.Remove(path);
            }
        }
        if (shouldDelete) TryDelete(path);
        else _ = TrimToLimitAsync();
    }

    private bool IsPinned(string path)
    {
        lock (_pinGate) return _pinCounts.ContainsKey(path);
    }

    private long GetMaximumBytes() => (long)MaximumSizeMb * 1024 * 1024;

    private static string BuildSongKey(Song song) => Hash(song.Id != 0
        ? $"{(int)song.Source}:id:{song.Id}"
        : $"{(int)song.Source}:mid:{song.Mid}");

    private static string BuildCoverFileName(string url) => "c-" + Hash(url) + ".cover";

    private static string BuildLyricFileName(Song song) => "l-" + BuildSongKey(song) + ".lyrics";

    private static string BuildPlaylistTracksFileName(Playlist playlist)
        => BuildPlaylistTracksFileName(playlist.Source, playlist.Id);

    private static string BuildPlaylistTracksFileName(MusicSource source, long playlistId)
        => $"p-{(int)source}-{Hash(playlistId.ToString(CultureInfo.InvariantCulture))}.tracks";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string GetAudioExtension(string url, string? mediaType)
    {
        var extension = mediaType?.ToLowerInvariant() switch
        {
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mpeg" => ".mp3",
            _ => null,
        };
        if (extension is not null) return extension;
        try
        {
            extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            if (extension is ".mp3" or ".flac" or ".m4a" or ".aac" or ".ogg" or ".wav")
                return extension;
        }
        catch
        {
        }
        return ".audio";
    }

    private static CachedPlaylistFile ToCachedPlaylist(Playlist playlist) => new()
    {
        Id = playlist.Id,
        DirId = playlist.DirId,
        Source = (int)playlist.Source,
        Name = playlist.Name,
        Description = playlist.Description,
        TrackCount = playlist.TrackCount,
        CoverUrl = playlist.CoverUrl,
        CanAddTracks = playlist.CanAddTracks,
    };

    private static Playlist ToPlaylist(CachedPlaylistFile playlist) => new()
    {
        Id = playlist.Id,
        DirId = playlist.DirId,
        Source = (MusicSource)playlist.Source,
        Name = playlist.Name,
        Description = playlist.Description,
        TrackCount = playlist.TrackCount,
        CoverUrl = playlist.CoverUrl,
        CanAddTracks = playlist.CanAddTracks,
    };

    private static CachedSongFile ToCachedSong(Song song) => new()
    {
        Id = song.Id,
        Source = (int)song.Source,
        Mid = song.Mid,
        Name = song.Name,
        Artist = song.Artist,
        Album = song.Album,
        CoverUrl = song.CoverUrl,
        DurationMs = song.DurationMs,
        Fee = song.Fee,
        ArtistIds = song.ArtistIds.ToArray(),
        ArtistNames = song.ArtistNames.ToArray(),
        ArtistMids = song.ArtistMids.ToArray(),
        AlbumId = song.AlbumId,
        AlbumMid = song.AlbumMid,
    };

    private static Song ToSong(CachedSongFile song) => new()
    {
        Id = song.Id,
        Source = (MusicSource)song.Source,
        Mid = song.Mid,
        Name = song.Name,
        Artist = song.Artist,
        Album = song.Album,
        CoverUrl = song.CoverUrl,
        DurationMs = song.DurationMs,
        Fee = song.Fee,
        ArtistIds = song.ArtistIds,
        ArtistNames = song.ArtistNames,
        ArtistMids = song.ArtistMids,
        AlbumId = song.AlbumId,
        AlbumMid = song.AlbumMid,
    };

    private static long TryGetLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DeleteStaleTemporaryFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*.part")) TryDelete(path);
        }
        catch
        {
        }
    }

    /// <summary>清掉旧版实现遗留的一次性页面快照文件。
    /// 现在快照只驻内存,但曾经把它们写到 n-&lt;进程 GUID&gt;-&lt;hash&gt;.snapshot,升级后不会有人再读 ——
    /// 不清就会一直占着缓存配额(还会被容量裁剪当成普通缓存文件)。</summary>
    private void DeleteLegacyPageSnapshotFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "n-*.snapshot")) TryDelete(path);
        }
        catch
        {
        }
    }

    private static string GetDefaultCacheDirectory()
    {
        // ⚠️ 这里的 #if ANDROID 是**死分支**:核心库只面向 net10.0,ANDROID 常量由 Android SDK
        // 只对 net*-android 工程定义,所以 Android 上实际走 #else(缓存落在
        // <应用私有 files 目录>/.local/share/ALyricEase/cache/music)。
        // 功能上没问题,但要知道它不在 FilesDir 下;改路径会孤立已有缓存,需要一并迁移。
#if ANDROID
        var root = Path.Combine(
            global::Android.App.Application.Context.FilesDir!.AbsolutePath, "ALyricEase");
#else
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALyricEase");
#endif
        // 沿用既有 music 目录，旧版缓存仍会被统一容量统计/清理，不在升级后留下孤儿文件。
        return Path.Combine(root, "cache", "music");
    }

    /// <summary>一次性页面快照的内存 LRU:纯数据(不含视觉树、不含 ViewModel),按"页面数"限容。
    /// 读取同步完成 —— 这是"返回不再卡一下"的关键。淘汰即丢弃:调用方取不到就回退常规加载,
    /// 容量按 MaxInMemorySnapshots 配到"淘汰几乎不发生"的量级,所以这条回退是长尾而非常态。</summary>
    private sealed class SnapshotMemoryCache<T> where T : class
    {
        private readonly int _capacity;
        private readonly LinkedList<(string Key, T Data)> _order = new();
        private readonly Dictionary<string, LinkedListNode<(string Key, T Data)>> _index =
            new(StringComparer.Ordinal);

        public SnapshotMemoryCache(int capacity) => _capacity = Math.Max(1, capacity);

        /// <summary>写入;超容时直接丢弃最久未用的条目(没有磁盘副本可兜底)。</summary>
        public void Set(string key, T data)
        {
            lock (_order)
            {
                if (_index.TryGetValue(key, out var existing))
                {
                    _order.Remove(existing);
                    _index.Remove(key);
                }

                var node = _order.AddFirst((key, data));
                _index[key] = node;
                while (_order.Count > _capacity)
                {
                    var oldest = _order.Last!;
                    _order.RemoveLast();
                    _index.Remove(oldest.Value.Key);
                }
            }
        }

        public bool TryTake(string key, out T? data)
        {
            lock (_order)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    data = node.Value.Data;
                    _order.Remove(node);
                    _index.Remove(key);
                    return true;
                }
            }

            data = null;
            return false;
        }

        public void Remove(string key)
        {
            lock (_order)
            {
                if (!_index.TryGetValue(key, out var node)) return;
                _order.Remove(node);
                _index.Remove(key);
            }
        }

        public void Clear()
        {
            lock (_order)
            {
                _order.Clear();
                _index.Clear();
            }
        }
    }

    private sealed record AudioCacheFile(string Path, int Rank);
}

/// <summary>某音源最近一次成功同步的侧栏歌单。</summary>
public sealed record CachedPlaylistLibrary(string UserName, IReadOnlyList<Playlist> Playlists);

/// <summary>固定正在播放的缓存音乐，防止清理时中断播放。</summary>
public sealed class MusicCacheLease : IDisposable
{
    private Action<string>? _release;

    internal MusicCacheLease(string filePath, int qualityRank, Action<string> release)
    {
        FilePath = filePath;
        QualityRank = qualityRank;
        _release = release;
    }

    public string FilePath { get; }

    public int QualityRank { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(FilePath);
}
