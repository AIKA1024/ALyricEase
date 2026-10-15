using ALyricEase.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ALyricEase;

/// <summary>在真实 NativeAOT 可执行文件中验证 DI、文件 sink、异常和 Avalonia 桥接。</summary>
internal static class LoggingProbe
{
    public static void Run()
    {
        var services = new ServiceCollection();
        AppLogging.AddServices(services);
        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<ProbeCategory>>();
        logger.LogDebug("logging-probe-debug-must-not-appear");
        logger.LogInformation("logging-probe-start 中文日志");
        try { throw new InvalidOperationException("logging-probe-exception https://example.invalid/?token=secret-probe-value"); }
        catch (Exception ex) { logger.LogError(ex, "logging-probe-failure SongId={SongId}", 42); }
        logger.LogWarning("Cookie: MUSIC_U=secret-cookie-value; other=secret-other-value");
        AppLogging.ConnectAvalonia();
        Avalonia.Logging.Logger.Sink!.Log(Avalonia.Logging.LogEventLevel.Warning,
            "Probe", null, "logging-probe-avalonia {Value}", 42);
        Parallel.For(0, 20, i => logger.LogInformation("logging-probe-concurrent {Index}", i));
        UpdateLoggingProbe.RunAsync().GetAwaiter().GetResult();
        logger.LogInformation("logging-probe-complete");
    }

    private sealed class ProbeCategory;
}
