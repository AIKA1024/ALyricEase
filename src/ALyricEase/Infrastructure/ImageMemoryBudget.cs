namespace ALyricEase.Infrastructure;

/// <summary>
/// 图片解码图的内存预算(唯一来源)。
/// 桌面用固定值;Android 按 GC 可用上限动态缩放 —— 同一个 64MB 在低端机上会把进程推向 OOM:
/// 图片之外还要同时留住 32 页页面快照、歌词、播放缓冲与 UI 位图。
/// 两个缓存各司其职,预算也分开:
/// - <see cref="LeaseCacheBytes"/>:ManagedCoverImage 的租约式缓存,承载列表与头像缩略图(主力);
/// - <see cref="DirectCacheBytes"/>:CoverLoader 的直接位图缓存,只服务播放器等低频大图。
///
/// ⚠️ 平台判定必须用 <see cref="OperatingSystem.IsAndroid"/> 而不是 <c>#if ANDROID</c>:
/// 核心库只面向 net10.0,而 ANDROID 常量由 Android SDK 只对 net*-android 工程定义,
/// 所以本程序集里的 <c>#if ANDROID</c> 分支**永远不会被编译**(实测:产出的 ALyricEase.dll
/// 里查不到任何 Android 类型名)。同一规则适用于其它写在核心库里的平台分支。
/// </summary>
internal static class ImageMemoryBudget
{
    /// <summary>桌面:64MB 解码图 / 512 项。约等于 20 张全尺寸封面或 3000 张 220px 缩略图。</summary>
    private const long DesktopLeaseBytes = 64L * 1024 * 1024;
    private const int DesktopLeaseItems = 512;
    private const long DesktopDirectBytes = 16L * 1024 * 1024;
    private const int DesktopDirectItems = 128;

    /// <summary>Android 缩放区间:低端机也要够显示一屏缩略图,再大的堆也不让图片吃掉太多。</summary>
    private const long AndroidMinLeaseBytes = 12L * 1024 * 1024;
    private const long AndroidMaxLeaseBytes = 48L * 1024 * 1024;

    /// <summary>租约式解码缓存的字节上限。</summary>
    public static long LeaseCacheBytes { get; } = ResolveLeaseBytes();

    /// <summary>租约式解码缓存的条目上限(失败项/极小图不占多少字节,靠条目数兜底)。</summary>
    public static int LeaseCacheItems { get; } = ResolveLeaseItems();

    /// <summary>直接位图缓存的字节上限。</summary>
    public static long DirectCacheBytes { get; } = ResolveDirectBytes();

    /// <summary>直接位图缓存的条目上限。</summary>
    public static int DirectCacheItems { get; } = ResolveDirectItems();

    /// <summary>预算解析结果(单行,供探针/日志核对当前生效值)。</summary>
    internal static string Describe() =>
        $"image-budget lease={LeaseCacheBytes / (1024 * 1024)}MB/{LeaseCacheItems}items " +
        $"direct={DirectCacheBytes / (1024 * 1024)}MB/{DirectCacheItems}items " +
        $"platform={(OperatingSystem.IsAndroid() ? "android" : "desktop")}";

    private static long ResolveLeaseBytes()
    {
        if (!OperatingSystem.IsAndroid()) return DesktopLeaseBytes;
        // 图片最多占可用堆的 1/8,其余留给页面快照(32 页 × 约 131KB)、歌词与播放缓冲。
        // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes 在 Android 上由运行时按设备/进程上限配置,
        // 无论它给的是 ART 堆上限还是物理内存,夹在 [12MB, 48MB] 内都是安全的取值。
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) available = 256L * 1024 * 1024;
        return Math.Clamp(available / 8, AndroidMinLeaseBytes, AndroidMaxLeaseBytes);
    }

    private static int ResolveLeaseItems() => OperatingSystem.IsAndroid()
        // 按平均 192KB/张(约 220×220 RGBA)折算,避免少数超大图占满字节预算后
        // 其余小图仍被条目数卡住;下限 48 保证一屏列表加若干头像。
        ? (int)Math.Clamp(LeaseCacheBytes / (192 * 1024), 48, DesktopLeaseItems)
        : DesktopLeaseItems;

    private static long ResolveDirectBytes() => OperatingSystem.IsAndroid()
        ? Math.Max(4L * 1024 * 1024, LeaseCacheBytes / 4)
        : DesktopDirectBytes;

    private static int ResolveDirectItems() => OperatingSystem.IsAndroid()
        ? Math.Max(32, LeaseCacheItems / 4)
        : DesktopDirectItems;
}
