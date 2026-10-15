using System.Reflection;
using System.Runtime.InteropServices;
using ALyricEase.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ALyricEase.Infrastructure.Logging;

/// <summary>进程级日志；显式 C# 配置，无配置程序集扫描或对象反射展开。</summary>
public static class AppLogging
{
    private static readonly object Gate = new();
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;
    private static bool _initialized;

    public static string DirectoryPath { get; private set; } = Path.Combine(AppDataRoot.Path, "Logs");
    public static ILoggerFactory Factory => _factory;
    public static bool IsFileLoggingAvailable { get; private set; }

    public static void Initialize(string? directory = null)
    {
        lock (Gate)
        {
            if (_initialized) return;
            DirectoryPath = directory ?? DirectoryPath;
            try
            {
                _factory = new SerilogLoggerFactory(CreateFileLogger(DirectoryPath), dispose: true);
                IsFileLoggingAvailable = true;
            }
            catch (Exception ex)
            {
                // 日志目录不可写也不能阻止播放器启动；不向另一个未知目录偷偷落盘。
                System.Diagnostics.Trace.WriteLine($"File logging unavailable: {ex.GetType().Name}");
            }
            _initialized = true;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            var version = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            Factory.CreateLogger("Startup").LogInformation(
                "Application started. Version={Version} OS={OS} Architecture={Architecture} Runtime={Runtime}",
                version, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription);
        }
    }

    internal static Serilog.Core.Logger CreateFileLogger(string directory,
        long fileSizeLimitBytes = 10 * 1024 * 1024, int retainedFileCountLimit = 14)
    {
        Directory.CreateDirectory(directory);
        // 预检写权限；普通 Serilog sink 可能只把打开失败写入 SelfLog。
        using (new FileStream(Path.Combine(directory, $".write-test-{Guid.NewGuid():N}"),
                   FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(new RedactingTextFormatter(), Path.Combine(directory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: fileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFileCountLimit,
                shared: OperatingSystem.IsWindows(), buffered: false)
            .CreateLogger();
    }

    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton(Factory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    }

    public static void ConnectAvalonia()
    {
        Avalonia.Logging.Logger.Sink = new AvaloniaLogSink(Factory);
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_initialized) return;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            Factory.CreateLogger("Startup").LogInformation("Application logging stopped");
            _factory.Dispose();
            _factory = NullLoggerFactory.Instance;
            IsFileLoggingAvailable = false;
            _initialized = false;
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Factory.CreateLogger("UnhandledException").LogCritical(e.ExceptionObject as Exception,
            "Unhandled process exception. Terminating={Terminating}", e.IsTerminating);
        if (e.IsTerminating) Shutdown();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Factory.CreateLogger("UnobservedTaskException").LogError(e.Exception, "Unobserved task exception");
        // 不 SetObserved，也不改变异常原有处理语义。此事件只是兜底，关键后台任务仍应主动捕获。
    }

    private static void OnProcessExit(object? sender, EventArgs e) => Shutdown();
}
