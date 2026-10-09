using System.IO;

namespace ALyricEase.Services;

/// <summary>聚合歌单自定义封面文件存取:文件复制进应用数据目录 covers/ 下
/// (是用户数据,不进媒体缓存 —— 那里会被容量管理裁剪),按聚合 id 一对一存取。</summary>
public static class AggregateCoverStore
{
    private static string CoverDirectory => Path.Combine(AppDataRoot.Path, "covers");

    private static string CoverPath(string aggregateId) =>
        Path.Combine(CoverDirectory, $"{aggregateId}.img");

    /// <summary>解析自定义封面绝对路径;未设置或文件已不存在(被手动清理等)返回 null。</summary>
    public static string? ResolvePath(string? customCover)
    {
        if (string.IsNullOrEmpty(customCover)) return null;
        var path = Path.Combine(CoverDirectory, Path.GetFileName(customCover));
        return File.Exists(path) ? path : null;
    }

    /// <summary>把准备好的临时封面文件落位(覆盖旧文件),返回应写进聚合歌单模型的存档文件名。</summary>
    public static string Save(string aggregateId, string tempFilePath)
    {
        Directory.CreateDirectory(CoverDirectory);
        var target = CoverPath(aggregateId);
        File.Copy(tempFilePath, target, overwrite: true);
        return Path.GetFileName(target);
    }

    public static void Delete(string aggregateId)
    {
        try
        {
            if (File.Exists(CoverPath(aggregateId))) File.Delete(CoverPath(aggregateId));
        }
        catch
        {
            // 文件被占用/权限问题:清理失败不影响功能,封面留成孤儿文件
        }
    }
}
