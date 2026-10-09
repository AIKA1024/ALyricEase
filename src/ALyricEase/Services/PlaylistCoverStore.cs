using System.IO;

namespace ALyricEase.Services;

/// <summary>歌单自定义封面文件存取(聚合歌单与本地音乐歌单共用):文件复制进应用数据目录
/// covers/ 下(是用户数据,不进媒体缓存 —— 那里会被容量管理裁剪),按调用方给的键一对一存取
/// (键形如 "agg-{id}" / "local-{id}")。内嵌封面提取的落盘缓存也放这里(前缀 embedded-)。</summary>
public static class PlaylistCoverStore
{
    private static string CoverDirectory => Path.Combine(AppDataRoot.Path, "covers");

    private static string CoverPath(string key) =>
        Path.Combine(CoverDirectory, $"{key}.img");

    /// <summary>解析自定义封面绝对路径;未设置或文件已不存在(被手动清理等)返回 null。</summary>
    public static string? ResolvePath(string? customCover)
    {
        if (string.IsNullOrEmpty(customCover)) return null;
        var path = Path.Combine(CoverDirectory, Path.GetFileName(customCover));
        return File.Exists(path) ? path : null;
    }

    /// <summary>把准备好的临时封面文件落位(覆盖旧文件),返回应写进歌单模型的存档文件名。</summary>
    public static string Save(string key, string tempFilePath)
    {
        Directory.CreateDirectory(CoverDirectory);
        var target = CoverPath(key);
        File.Copy(tempFilePath, target, overwrite: true);
        return Path.GetFileName(target);
    }

    public static void Delete(string key)
    {
        try
        {
            if (File.Exists(CoverPath(key))) File.Delete(CoverPath(key));
        }
        catch
        {
            // 文件被占用/权限问题:清理失败不影响功能,封面留成孤儿文件
        }
    }

    /// <summary>把内嵌封面字节落盘为持久缓存文件(键 = 音频路径 + 修改时间,重复提取直接命中)。
    /// 返回可直接交给 ManagedCoverImage 的本地文件路径;提取失败返回 null。</summary>
    public static string? SaveEmbedded(string audioPath, byte[] imageBytes, string extension)
    {
        try
        {
            Directory.CreateDirectory(CoverDirectory);
            var stamp = File.GetLastWriteTimeUtc(audioPath).Ticks.ToString("x16");
            var key = $"embedded-{Hash(audioPath + "|" + stamp)}";
            var target = CoverPath(key);
            if (!File.Exists(target)) File.WriteAllBytes(target, imageBytes);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
