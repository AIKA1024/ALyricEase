using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>本地音频文件助手:扩展名识别 + 文件路径到 <see cref="Song"/> 的映射。
/// 供启动参数(双击文件/打开方式)与后续"打开本地文件"入口共用。</summary>
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

    /// <summary>把一个本地音频文件映射为 Song。文件名约定"歌手 - 标题"时拆出歌手,
    /// 否则歌手显示"未知歌手"。Id 用完整路径的确定性哈希(恒为负,避免与在线曲库撞号),
    /// 保证同一文件在队列去重/最近播放里是同一首歌,且跨运行稳定。</summary>
    public static Song CreateSong(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        string name = fileName, artist = "未知歌手";
        var separator = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0 && separator + 3 < fileName.Length)
        {
            artist = fileName[..separator].Trim();
            name = fileName[(separator + 3)..].Trim();
            if (artist.Length == 0 || name.Length == 0)
            {
                artist = "未知歌手";
                name = fileName;
            }
        }
        return new Song
        {
            Source = MusicSource.Local,
            Id = StableNegativeId(path),
            Name = name,
            Artist = artist,
            LocalFilePath = Path.GetFullPath(path),
        };
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
