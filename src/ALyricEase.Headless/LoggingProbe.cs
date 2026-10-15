using ALyricEase.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace ALyricEase.Headless;

internal static class LoggingProbe
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ALyricEaseLoggingProbe", Guid.NewGuid().ToString("N"));
        var basic = Path.Combine(root, "basic");
        using (var factory = new SerilogLoggerFactory(AppLogging.CreateFileLogger(basic), dispose: true))
        {
            var logger = factory.CreateLogger("LoggingProbe");
            logger.LogDebug("filtered-debug");
            logger.LogInformation("中文日志 SongId={SongId}", 42);
            try { throw new InvalidOperationException("probe-exception https://example.invalid/?token=secret-url"); }
            catch (Exception ex) { logger.LogError(ex, "Failure"); }
            logger.LogWarning("Cookie: MUSIC_U=secret-cookie; p_skey=secret-skey");
            logger.LogWarning("Authorization: Bearer secret-bearer");
            logger.LogWarning("{{\"access_token\":\"secret-json\"}}");
            var sink = new AvaloniaLogSink(factory);
            sink.Log(Avalonia.Logging.LogEventLevel.Information, "Probe", null, "filtered-avalonia-info");
            sink.Log(Avalonia.Logging.LogEventLevel.Warning, "Probe", null, "framework-warning {Value}", 123);
            Parallel.For(0, 100, i => logger.LogInformation("concurrent-entry-{Index}", i));
        }
        var text = ReadLogs(basic);
        Check(text.Contains("中文日志 SongId=42"), "UTF-8 and structured fields");
        Check(text.Contains("InvalidOperationException: probe-exception") && text.Contains("LoggingProbe.Run"), "exception and stack trace");
        Check(!text.Contains("secret-") && text.Contains("[URL redacted]") && text.Contains("[redacted]"), "URL, Cookie, Authorization and JSON credential redaction");
        Check(!text.Contains("filtered-"), "business and framework level filtering");
        Check(text.Contains("[Avalonia.Probe] framework-warning 123"), "Avalonia template bridge");
        for (var i = 0; i < 100; i++) Check(text.Contains($"concurrent-entry-{i}\r\n"), "concurrent writes " + i);

        var rolling = Path.Combine(root, "rolling");
        Directory.CreateDirectory(rolling);
        for (var day = 1; day <= 16; day++) File.WriteAllText(Path.Combine(rolling, $"app-200001{day:D2}.log"), "old");
        using (var log = AppLogging.CreateFileLogger(rolling, fileSizeLimitBytes: 1024, retainedFileCountLimit: 3))
        {
            for (var i = 0; i < 20; i++) log.Information("Rolling {Index} {Padding}", i, new string('x', 600));
            log.Information("rolling-last-entry");
        }
        var files = Directory.GetFiles(rolling, "*.log");
        Check(files.Length == 3 && files.All(p => !Path.GetFileName(p).StartsWith("app-2000")), "retention and old-day cleanup");
        Check(files.Any(p => Path.GetFileNameWithoutExtension(p).Contains('_')), "size rollover");
        Check(ReadLogs(rolling).Contains("rolling-last-entry"), "logging continues after rollover");

        var blockedPath = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockedPath, "block directory creation");
        AppLogging.Initialize(blockedPath);
        Check(!AppLogging.IsFileLoggingAvailable, "unavailable directory is reported");
        AppLogging.Factory.CreateLogger("Probe").LogInformation("must not crash without writable log folder");
        AppLogging.Shutdown();
        AppLogging.Shutdown();
        Console.WriteLine("PASS: file logging, exception stacks, redaction, filtering, Avalonia bridge, concurrent writes, rolling/retention and unavailable-directory fallback.");
        Console.WriteLine("Probe artifacts: " + root);
    }

    private static string ReadLogs(string directory) => string.Concat(Directory.GetFiles(directory, "*.log").Select(File.ReadAllText));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Logging probe failed: " + message);
    }
}
