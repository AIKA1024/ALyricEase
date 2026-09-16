using System.Threading;

namespace ALyricEase.Infrastructure;

/// <summary>
/// 图片解码图的内存预算(唯一来源)。
///
/// 桌面用固定值;Android **按 ART 堆上限缩放**,而不是按"可用系统内存":
/// - 同一个 64MB 在低端机上会把进程推向 OOM —— 图片之外还要同时留住 32 页页面快照、
///   歌词、播放缓冲与 UI 位图;
/// - 但"可用内存"是个误导性的分母:`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
///   在 Android 上**报的是物理内存量级**(2026-09-16 真机实测 4793MB ≈ 5950MB × 0.8,
///   与 ART 堆无关),÷8 恒大于上限 ⇒ 缩放退化成固定 48MB,低端机拿不到任何降额。
/// 真正反映设备档位、也是 Android 官方给应用的内存指导值,是 `ActivityManager.MemoryClass`
/// (等价于 `Runtime.maxMemory()`,即 ART 堆上限:低端机常见 96/128MB,中端 192/256MB,大内存 512MB)。
///
/// 核心库只面向 `net10.0`、引用不到 Android 类型,所以这个分母由宿主**注入** ——
/// 见 <see cref="SetAndroidHeapLimit"/>,Android 侧在 `Application.OnCreate` 里调用。
/// 未注入时退回 <see cref="AndroidMaxLeaseBytes"/>(等于旧行为),不影响功能正确性。
///
/// 两个缓存各司其职,预算也分开:
/// - <see cref="LeaseCacheBytes"/>:ManagedCoverImage 的租约式缓存,承载列表与头像缩略图(主力);
/// - <see cref="DirectCacheBytes"/>:CoverLoader 的直接位图缓存,只服务播放器等低频大图。
///
/// ⚠️ 平台判定必须用 <see cref="OperatingSystem.IsAndroid"/> 而不是 `#if ANDROID`:
/// 核心库只面向 net10.0,而 ANDROID 常量由 Android SDK 只对 net*-android 工程定义,
/// 所以本程序集里的 `#if ANDROID` 分支**永远不会被编译**(实测:产出的 ALyricEase.dll
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

    /// <summary>图片占 ART 堆的比例。1/4 让 192MB 档维持 48MB(与旧行为一致),同时给低端机留出降额空间。</summary>
    private const long AndroidHeapDivisor = 4;

    /// <summary>ART 堆上限(字节),由 Android 宿主注入;0 表示尚未注入。</summary>
    private static long _androidHeapLimitBytes;

    // 惰性解析:注入必须发生在首次读取之前(Android 上是 Application.OnCreate,
    // 早于 Avalonia 的 App 初始化与 CoverImagePipeline.Configure)。用 Lazy 而不是
    // "静态只读属性 = 表达式",是因为后者在类型初始化时就定型,注入晚一步就永远读不到;
    // 也不能用可空结构体字段做双检锁(多字段结构体的读写不是原子的)。
    private static readonly Lazy<Budget> ResolvedBudget =
        new(ComputeBudget, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>租约式解码缓存的字节上限。</summary>
    public static long LeaseCacheBytes => ResolvedBudget.Value.LeaseBytes;

    /// <summary>租约式解码缓存的条目上限(失败项/极小图不占多少字节,靠条目数兜底)。</summary>
    public static int LeaseCacheItems => ResolvedBudget.Value.LeaseItems;

    /// <summary>直接位图缓存的字节上限。</summary>
    public static long DirectCacheBytes => ResolvedBudget.Value.DirectBytes;

    /// <summary>直接位图缓存的条目上限。</summary>
    public static int DirectCacheItems => ResolvedBudget.Value.DirectItems;

    /// <summary>
    /// Android 宿主注入 ART 堆上限(= `Java.Lang.Runtime.GetRuntime().MaxMemory()`,
    /// 与 `ActivityManager.MemoryClass` 同值),必须在**首次读取预算之前**调用 ——
    /// 即 `CoverImagePipeline.Configure` 之前。Android 侧放在 `Application.OnCreate`,
    /// 它早于 Activity 创建与 Avalonia 的 App 初始化。桌面宿主不需要调用。
    /// </summary>
    internal static void SetAndroidHeapLimit(long bytes)
    {
        if (bytes > 0) Volatile.Write(ref _androidHeapLimitBytes, bytes);
    }

    /// <summary>给定 ART 堆上限时的租约预算试算(纯函数,不读缓存)。
    /// 真机只能验到当前这一档,靠它把各档位摊开对照;与 <see cref="LeaseCacheBytes"/> 同一公式。</summary>
    internal static long LeaseBytesFor(long androidHeapLimitBytes) =>
        Math.Clamp(androidHeapLimitBytes / AndroidHeapDivisor, AndroidMinLeaseBytes, AndroidMaxLeaseBytes);

    /// <summary>预算解析结果(单行,供探针/日志核对当前生效值;Android 上附注入到的堆上限)。</summary>
    internal static string Describe()
    {
        var budget = ResolvedBudget.Value;
        var text = $"image-budget lease={budget.LeaseBytes / (1024 * 1024)}MB/{budget.LeaseItems}items " +
                   $"direct={budget.DirectBytes / (1024 * 1024)}MB/{budget.DirectItems}items " +
                   $"platform={(OperatingSystem.IsAndroid() ? "android" : "desktop")}";
        if (!OperatingSystem.IsAndroid()) return text;

        var limit = Volatile.Read(ref _androidHeapLimitBytes);
        return text + (limit > 0
            ? $" heapLimit={limit / (1024 * 1024)}MB"
            : " heapLimit=(未注入,已退回固定上限)");
    }

    private static Budget ComputeBudget()
    {
        var leaseBytes = ResolveLeaseBytes();
        return new Budget(
            leaseBytes,
            OperatingSystem.IsAndroid() ? LeaseItemsFor(leaseBytes) : DesktopLeaseItems,
            OperatingSystem.IsAndroid() ? Math.Max(4L * 1024 * 1024, leaseBytes / 4) : DesktopDirectBytes,
            OperatingSystem.IsAndroid() ? Math.Max(32, LeaseItemsFor(leaseBytes) / 4) : DesktopDirectItems);
    }

    private static long ResolveLeaseBytes()
    {
        if (!OperatingSystem.IsAndroid()) return DesktopLeaseBytes;
        var limit = Volatile.Read(ref _androidHeapLimitBytes);
        // 没拿到堆上限就别猜:退回固定上限,与"按平台区分预算"之前的行为一致。
        return limit > 0 ? LeaseBytesFor(limit) : AndroidMaxLeaseBytes;
    }

    /// <summary>按平均 192KB/张(约 220×220 RGBA)折算,避免少数超大图占满字节预算后
    /// 其余小图仍被条目数卡住;下限 48 保证一屏列表加若干头像。</summary>
    private static int LeaseItemsFor(long leaseBytes) =>
        (int)Math.Clamp(leaseBytes / (192 * 1024), 48, DesktopLeaseItems);

    private readonly record struct Budget(long LeaseBytes, int LeaseItems, long DirectBytes, int DirectItems);
}
