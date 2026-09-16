using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ALyricEase.Infrastructure;

namespace ALyricEase.Android;

[Application]
public class Application : AvaloniaAndroidApplication<App>
{
    public Application(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        // 图片内存预算的分母:核心库只面向 net10.0、引用不到 Android 类型,ART 堆上限只能由这里注入。
        // 必须早于首次读取预算(CoverImagePipeline.Configure 在 Avalonia 的 App 初始化里,
        // 而那发生在 MainActivity.OnCreate);Application.OnCreate 早于 Activity 创建,满足约束。
        InjectImageMemoryBudget();

        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    /// <summary>把 ART 堆上限(= ActivityManager.MemoryClass,低端机 96/128MB、中端 192/256MB)交给核心库。
    /// 失败不致命:核心库在未注入时会退回固定上限,只是低端机拿不到自动降额,所以只记一行警告。</summary>
    private static void InjectImageMemoryBudget()
    {
        try
        {
            var runtime = global::Java.Lang.Runtime.GetRuntime();
            if (runtime is null) return;

            var maxMemory = runtime.MaxMemory();
            if (maxMemory <= 0) return;

            ImageMemoryBudget.SetAndroidHeapLimit(maxMemory);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn(
                "ALyricEase", "注入 ART 堆上限失败,图片预算退回固定上限: " + exception.Message);
        }
    }
}
