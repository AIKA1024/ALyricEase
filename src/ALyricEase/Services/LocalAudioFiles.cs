using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>本地音频文件助手:扩展名识别 + 文件路径到 <see cref="Song"/> 的映射。
/// 标签/封面读取统一走 TagLibSharp(MP3/FLAC/M4A/Ogg/Opus/APE/WavPack/WAV/AIFF/WMA 全覆盖),
/// 只读不改写;标签缺失时用文件名兜底("歌手 - 标题"约定拆分)。
/// 供本地音乐歌单导入/聚合本地成员与启动参数(双击文件/打开方式)共用。</summary>
public static class LocalAudioFiles
{
    /// <summary>支持的音频扩展名(小写含点)。取主流有损/无损格式;生僻容器交由系统解码器裁决。</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = new[]
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus",
        ".wma", ".ape", ".wv", ".mka", ".aiff", ".aif",
    };

    /// <summary>路径是否为受支持的音频文件(只看扩展名,不探测文件头)。</summary>
    public static bool IsSupportedFile(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>从候选路径(命令行参数/拖放)过滤出存在的受支持音频文件,按原顺序去重。
    /// file:/// 前缀的 URI 也接受(资源管理器个别场景会传 URI 形态)。</summary>
    public static IReadOnlyList<string> FilterPaths(IEnumerable<string?>? candidates)
    {
        if (candidates is null) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith('-')) continue;
            string path;
            try
            {
                path = candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(candidate).LocalPath
                    : candidate;
            }
            catch
            {
                continue;
            }
            if (!IsSupportedFile(path) || !File.Exists(path)) continue;
            var full = Path.GetFullPath(path);
            if (seen.Add(full)) result.Add(full);
        }
        return result;
    }

    /// <summary>把一个本地音频文件映射为 Song。优先读文件内嵌标签(TagLibSharp:
    /// 标题/歌手/专辑/时长/内嵌封面,MP3/FLAC/M4A/Ogg/Opus/APE/WavPack/WAV/AIFF/WMA 全覆盖),
    /// 缺失字段用文件名兜底("歌手 - 标题"约定拆分,否则歌手显"未知歌手")。
    /// Id 用完整路径的确定性哈希(恒为负,避免与在线曲库撞号),
    /// 保证同一文件在队列去重/最近播放里是同一首歌,且跨运行稳定。</summary>
    public static Song CreateSong(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        string fallbackName = fileName, fallbackArtist = "未知歌手";
        var separator = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0 && separator + 3 < fileName.Length)
        {
            fallbackArtist = fileName[..separator].Trim();
            fallbackName = fileName[(separator + 3)..].Trim();
            if (fallbackArtist.Length == 0 || fallbackName.Length == 0)
            {
                fallbackArtist = "未知歌手";
                fallbackName = fileName;
            }
        }

        var tags = GetTags(path);
        return new Song
        {
            Source = MusicSource.Local,
            Id = StableNegativeId(path),
            Name = tags.Title ?? fallbackName,
            Artist = tags.Artist ?? fallbackArtist,
            Album = tags.Album ?? "",
            DurationMs = tags.DurationMs,
            LocalFilePath = Path.GetFullPath(path),
        };
    }

    /// <summary>本地音频内嵌标签(全部可空 = 文件没有可用标签,调用方用文件名兜底)。</summary>
    internal sealed record LocalAudioTags(string? Title, string? Artist, string? Album, int DurationMs, string? CoverFile);

    private static readonly ConcurrentDictionary<string, LocalAudioTags> TagCache = new();

    /// <summary>读取内嵌标签,按(路径+修改时间)进程内缓存 —— CreateSong 会被行重建反复调用,
    /// 不能每次都开文件解析。TagLibSharp 只读元数据,不改写文件。</summary>
    internal static LocalAudioTags GetTags(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return new LocalAudioTags(null, null, null, 0, null);
            var key = full.ToUpperInvariant() + "|" + File.GetLastWriteTimeUtc(full).Ticks;
            return TagCache.GetOrAdd(key, _ => ReadTags(full));
        }
        catch
        {
            return new LocalAudioTags(null, null, null, 0, null);
        }
    }

    private static LocalAudioTags ReadTags(string full)
    {
        try
        {
            using var file = TagLib.File.Create(full);
            var tag = file.Tag;
            var title = NullIfEmpty(tag.Title?.Trim());
            var artist = NullIfEmpty(tag.FirstPerformer?.Trim()) ?? NullIfEmpty(tag.FirstAlbumArtist?.Trim());
            var album = NullIfEmpty(tag.Album?.Trim());
            var durationMs = (int)(file.Properties?.Duration.TotalMilliseconds ?? 0);
            var cover = ExtractCover(file, full);
            return new LocalAudioTags(title, artist, album, durationMs, cover);
        }
        catch
        {
            // CorruptFileException/UnsupportedException:标签读不出,交由调用方文件名兜底
            return new LocalAudioTags(null, null, null, 0, null);
        }
    }

    /// <summary>提取内嵌专辑封面并落盘为持久缓存文件(covers/,键 = 路径+修改时间),
    /// 返回可直接交给 ManagedCoverImage 本地路径加载的文件路径;无内嵌封面返回 null。</summary>
    public static string? ExtractCoverFile(string path) => GetTags(path).CoverFile;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? ExtractCover(TagLib.File file, string full)
    {
        var picture = file.Tag.Pictures.FirstOrDefault();
        if (picture?.Data?.Data is not { Length: > 0 } bytes) return null;
        var ext = picture.MimeType?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ? ".png" : ".jpg";
        return PlaylistCoverStore.SaveEmbedded(full, bytes, ext);
    }

    /// <summary>展开导入输入:文件夹递归枚举受支持的音频文件,单文件直接透传。</summary>
    public static IEnumerable<string> ExpandLocalInputs(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (Directory.Exists(path))
            {
                var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(IsSupportedFile)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                    yield return file;
            }
            else
            {
                yield return path;
            }
        }
    }

    /// <summary>路径 → 稳定负数 id:SHA256 前 8 字节取绝对值再取负(排除 0,RecentSongFile 以 Id!=0 为有效判定)。</summary>
    private static long StableNegativeId(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        var raw = BitConverter.ToInt64(bytes, 0);
        var magnitude = raw == long.MinValue ? long.MaxValue : Math.Abs(raw);
        var id = -magnitude;
        return id == 0 ? -1 : id;
    }

    /// <summary>调试/日志用的规范展示文本(如 "本地音乐 (3 个文件)")。</summary>
    public static string Describe(IReadOnlyList<string> paths) => paths.Count switch
    {
        0 => "无本地音频文件",
        1 => Path.GetFileName(paths[0]),
        _ => string.Create(CultureInfo.InvariantCulture, $"本地音乐 ({paths.Count} 个文件)"),
    };
}
