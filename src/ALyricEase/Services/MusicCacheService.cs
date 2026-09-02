using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ALyricEase.Models;

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

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _cacheDirectory;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(2, 2);
    private readonly object _pinGate = new();
    private readonly Dictionary<string, int> _pinCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deleteWhenReleased = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _downloads = new(StringComparer.Ordinal);
    private int _maximumSizeMb;
    private int _clearGeneration;

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
        _ = TrimToLimitAsync();
    }

    public int MaximumSizeMb => Volatile.Read(ref _maximumSizeMb);

    public static int NormalizeMaximumSizeMb(int value) =>
        Math.Clamp(value, MinimumAllowedSizeMb, MaximumAllowedSizeMb);

    /// <summary>取得满足请求音质的本地音乐；更高音质也可直接满足较低音质请求。</summary>
    public MusicCacheLease? TryAcquire(Song song, string requestedQuality)
    {
        var songKey = BuildSongKey(song);
        var requestedRank = GetQualityRank(song.Source, requestedQuality);
        _mutationGate.Wait();
        try
        {
            var candidate = GetAudioFiles(songKey)
                .Where(file => file.Rank >= requestedRank)
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

    public Task SetMaximumSizeMbAsync(int value)
    {
        Volatile.Write(ref _maximumSizeMb, NormalizeMaximumSizeMb(value));
        return TrimToLimitAsync();
    }

    /// <summary>清空全部媒体缓存；正在播放的音乐在租约释放后删除。</summary>
    public async Task ClearAsync()
    {
        Interlocked.Increment(ref _clearGeneration);
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
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal long GetCurrentSizeBytes() => EnumerateCacheFiles().Select(TryGetLength).Sum();

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

    private async Task TrimToLimitAsync()
    {
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

    private static string GetDefaultCacheDirectory()
    {
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

    private sealed record AudioCacheFile(string Path, int Rank);
}

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
