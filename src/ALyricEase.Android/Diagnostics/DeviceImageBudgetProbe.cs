using System;
using System.Reflection;
using System.Text;
using ALyricEase.Infrastructure;

namespace ALyricEase.Diagnostics;

/// <summary>
/// Android 真机自检:把「图片内存预算的实际生效值」与「生产缓存实例的真正上限」打到 logcat。
///
/// 存在的理由:核心库只面向 net10.0,<c>#if ANDROID</c> 在那边是死分支,平台判定必须走
/// <see cref="OperatingSystem.IsAndroid"/>(见 ImageMemoryBudget 的注释)。桌面只能验证
/// "desktop 分支取值正确",Android 分支的输入是 GC/ART 在真机上的上报值 —— 那是推断不出来的,
/// 只能实测。本探针同时核对预算有没有退化成固定上限(若 TotalAvailableMemoryBytes 报的是
/// 物理内存,available/8 对任何现役手机都会超过 48MB 上限,缩放就失去意义)。
///
/// 触发方式(只读,不影响正常启动):
/// <code>
/// adb shell am start -n com.aika1024.alyricease/ALyricEase.MainActivity --es aly_probe image-budget
/// adb logcat -d -s ALyricEaseProbe:I
/// </code>
/// </summary>
internal static class DeviceImageBudgetProbe
{
    /// <summary>触发自检的 intent extra 名/值,与 MainActivity 约定。</summary>
    internal const string IntentExtraKey = "aly_probe";
    internal const string IntentExtraValue = "image-budget";

    private const string Tag = "ALyricEaseProbe";

    /// <summary>第二次采样前的等待时长:冷启动触发时应用才开始初始化,首页封面尚未落地。</summary>
    private static readonly TimeSpan SecondSampleDelay = TimeSpan.FromSeconds(20);

    public static void Run()
    {
        try
        {
            Emit("===== 真机图片内存预算自检 =====");
            EmitSample("t0");
            // 冷启动触发时租约缓存必然是空的;隔一段再采一次才能看到真实使用中的占用。
            // 不依赖 intent 的二次投递(SingleTop 下那条路径偶发不达),一次触发拿两个时点。
            Thread.Sleep(SecondSampleDelay);
            EmitSample($"t+{(int)SecondSampleDelay.TotalSeconds}s");
            Emit("===== 自检结束 =====");
        }
        catch (Exception exception)
        {
            Emit("自检异常: " + exception);
        }
    }

    private static void EmitSample(string tag)
    {
        Emit($"--- 采样 {tag} ---");
        Emit("budget  : " + ImageMemoryBudget.Describe());
        EmitHeapScalingTable();
        EmitPlatformFacts();
        EmitEffectiveCacheLimits();
    }

    /// <summary>按 ART 堆档位推演租约预算。真机只能验到当前这一档,把各档摊开才能看清"缩放"是否真在工作
    /// —— 换成 ART 堆上限作分母之前,旧公式对任何现役设备都落到 48MB 上限,靠这张表一眼可见。</summary>
    private static void EmitHeapScalingTable()
    {
        var text = new StringBuilder("scale   :");
        foreach (var heapMb in new[] { 96, 128, 192, 256, 512 })
        {
            var leaseMb = ImageMemoryBudget.LeaseBytesFor(heapMb * 1024L * 1024) / (1024 * 1024);
            text.Append($" {heapMb}MB→{leaseMb}MB");
        }

        Emit(text.ToString());
    }

    /// <summary>GC / ART / ActivityManager 各自报告的"可用内存",用于判断预算输入的来源。</summary>
    private static void EmitPlatformFacts()
    {
        var gc = GC.GetGCMemoryInfo();
        Emit($"gc      : available={Format(gc.TotalAvailableMemoryBytes)} " +
             $"managedHeap={Format(GC.GetTotalMemory(forceFullCollection: false))} " +
             $"heapSize={Format(gc.HeapSizeBytes)} " +
             $"committed={Format(gc.TotalCommittedBytes)} " +
             $"load={Format(gc.MemoryLoadBytes)} highLoadThreshold={Format(gc.HighMemoryLoadThresholdBytes)}");

        if (Java.Lang.Runtime.GetRuntime() is { } runtime)
        {
            Emit($"art     : max={Format(runtime.MaxMemory())} " +
                 $"total={Format(runtime.TotalMemory())} free={Format(runtime.FreeMemory())}");
        }

        var context = global::Android.App.Application.Context;
        if (context?.GetSystemService(global::Android.Content.Context.ActivityService)
            is global::Android.App.ActivityManager manager)
        {
            var info = new global::Android.App.ActivityManager.MemoryInfo();
            manager.GetMemoryInfo(info);
            Emit($"am      : memoryClass={manager.MemoryClass}MB " +
                 $"largeMemoryClass={manager.LargeMemoryClass}MB lowRamDevice={manager.IsLowRamDevice} " +
                 $"total={Format(info.TotalMem)} avail={Format(info.AvailMem)} " +
                 $"threshold={Format(info.Threshold)} lowMemory={info.LowMemory}");
        }

        Emit($"process : abi={Java.Lang.JavaSystem.GetProperty("os.arch")} " +
             $"bit64={Environment.Is64BitProcess} cores={Environment.ProcessorCount} " +
             $"workingSet={Format(Environment.WorkingSet)}");
    }

    /// <summary>
    /// 预算值只有在真的传给了缓存实例时才算生效。这两个缓存的构造函数入参没暴露出来,
    /// 用反射读实际持有的字段,避免"预算改了但缓存没跟着改"这类静默失效。
    /// </summary>
    private static void EmitEffectiveCacheLimits()
    {
        var leaseCache = ReadStaticField(typeof(CoverImagePipeline), "_memoryCache");
        if (leaseCache is null)
        {
            Emit("effective: 租约缓存未初始化(CoverImagePipeline.Configure 尚未调用)");
        }
        else
        {
            Emit("effective: leaseCache " +
                 $"maxBytes={Format(ReadInstanceField(leaseCache, "_maximumBytes"))} " +
                 $"maxItems={ReadInstanceField(leaseCache, "_maximumItems")}");
            var stats = CoverImagePipeline.MemoryCacheStats;
            Emit($"effective: leaseUsage bytes={Format(stats.Bytes)} items={stats.Items}");
        }

        Emit("effective: directCache " +
             $"maxBytes={Format(ReadStaticField(typeof(CoverLoader), "MaxCacheBytes"))} " +
             $"maxItems={Format(ReadStaticField(typeof(CoverLoader), "MaxCacheEntries"))}");
    }

    private static object? ReadStaticField(Type type, string name) =>
        type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    private static object? ReadInstanceField(object target, string name) =>
        target.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(target);

    private static string Format(object? value)
    {
        if (value is not { } number) return "(null)";
        long bytes;
        try { bytes = Convert.ToInt64(number); }
        catch (Exception) { return number.ToString() ?? "(null)"; }
        if (bytes < 0) return bytes.ToString();
        return $"{bytes} ({bytes / (1024.0 * 1024.0):F1}MB)";
    }

    private static void Emit(string message) =>
        global::Android.Util.Log.Info(Tag, message);
}
