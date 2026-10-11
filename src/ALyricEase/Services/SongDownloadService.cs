using System.Net.Http;
using ALyricEase.Models;

namespace ALyricEase.Services;

public sealed record SongDownloadQuality(string Level, string Name, string Description);
public sealed record SongDownloadProgress(long Bytes, long? TotalBytes, bool FromCache);
public sealed record SongDownloadResult(string Location, bool FromCache);

public sealed class SongDownloadService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly MusicApiProvider _apis;
    private readonly MusicCacheService _cache;
    private readonly ISongDownloadStorage _storage;
    private readonly HttpClient _http;

    public SongDownloadService(MusicApiProvider apis, MusicCacheService cache, ISongDownloadStorage storage)
        : this(apis, cache, storage, SharedHttp) { }

    internal SongDownloadService(MusicApiProvider apis, MusicCacheService cache,
        ISongDownloadStorage storage, HttpClient http)
        => (_apis, _cache, _storage, _http) = (apis, cache, storage, http);

    public static IReadOnlyList<SongDownloadQuality> GetQualities(MusicSource source)
    {
        if (source is not (MusicSource.NetEase or MusicSource.QQ)) return [];

        // 与设置页和播放共用四档名称、顺序和协议映射，不单独扩展下载档位。
        string[] descriptions = source == MusicSource.NetEase
            ? ["极高 · HQ · 最高 320 kbps", "无损 · SQ", "高清臻音 · 空间音频", "臻音全景声 · 沉浸式空间音频"]
            : ["128 kbps · MP3", "腾讯 AICodec", "320 kbps · MP3", "FLAC 无损音频"];
        return Enumerable.Range(0, AudioQualityMapper.OptionCount)
            .Select(index => new SongDownloadQuality(
                AudioQualityMapper.GetRequestLevel(source, index),
                AudioQualityMapper.OptionNames[index], descriptions[index]))
            .ToArray();
    }

    public async Task<SongDownloadResult> DownloadAsync(Song song, SongDownloadQuality quality,
        IProgress<SongDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!GetQualities(song.Source).Any(option => option.Level == quality.Level))
            throw new InvalidOperationException("这首歌曲不支持下载。");

        // 在后台取得租约，避免缓存裁剪持锁时阻塞 UI。复制结束前一直固定缓存。
        using var cached = await Task.Run(() => _cache.TryAcquireForDownload(song, quality.Level),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (cached is not null)
        {
            await using var input = new FileStream(cached.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true);
            return await SaveAsync(input, Path.GetExtension(cached.FilePath), input.Length, true)
                .ConfigureAwait(false);
        }

        var item = await _apis.Resolve(song).GetPlayUrlAsync(song, quality.Level, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(item?.Url))
            throw new InvalidOperationException("暂时无法下载这首歌曲，请检查该平台的登录状态或歌曲权限。");
        if (item.IsTrial == true)
            throw new InvalidOperationException("当前账号只能试听这首歌曲，无法下载完整音频。");
        var actualRank = string.IsNullOrWhiteSpace(item.Level)
            ? MusicCacheService.GetQualityRankFromBr(item.Br)
            : MusicCacheService.GetQualityRank(song.Source, item.Level);
        if (actualRank != MusicCacheService.GetQualityRank(song.Source, quality.Level))
            throw new InvalidOperationException("当前歌曲或账号暂不支持所选音质，请选择其他音质后重试。");

        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        request.Headers.TryAddWithoutValidation("Referer",
            song.Source == MusicSource.QQ ? "https://y.qq.com/" : "https://music.163.com/");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            || mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("平台没有返回音频文件，请稍后重试。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await SaveAsync(source, MusicCacheService.GetAudioExtension(item.Url, mediaType),
            response.Content.Headers.ContentLength, false).ConfigureAwait(false);

        async Task<SongDownloadResult> SaveAsync(Stream input, string extension, long? length, bool fromCache)
        {
            if (extension == ".audio" && song.Source == MusicSource.QQ && quality.Level == "nac")
                extension = ".nac"; // 兼容旧缓存未识别 NAC 扩展名的文件。
            var name = BuildFileName(song, quality, extension);
            var mime = extension.ToLowerInvariant() switch
            {
                ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".m4a" => "audio/mp4",
                ".aac" => "audio/aac", ".ogg" => "audio/ogg", ".wav" => "audio/wav",
                _ => "application/octet-stream",
            };
            var location = await _storage.SaveAsync(name, mime, async (output, token) =>
            {
                long written = 0;
                var buffer = new byte[81920];
                var lastReport = Environment.TickCount64;
                progress?.Report(new(0, length, fromCache));
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    written += count;
                    if (Environment.TickCount64 - lastReport >= 100)
                    {
                        progress?.Report(new(written, length, fromCache));
                        lastReport = Environment.TickCount64;
                    }
                }
                if (written == 0 || (length is > 0 && written != length))
                    throw new IOException("音频文件下载不完整，请重试。");
                progress?.Report(new(written, length, fromCache));
            }, cancellationToken).ConfigureAwait(false);
            return new(location, fromCache);
        }
    }

    internal static string BuildFileName(Song song, SongDownloadQuality quality, string extension)
    {
        var text = $"{song.Name} - {song.Artist}";
        var clean = new string(text.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        if (clean.Length > 100) clean = clean[..100];
        if (string.IsNullOrWhiteSpace(clean)) clean = "歌曲";
        return $"{clean} [{quality.Level}]{extension}";
    }
}
