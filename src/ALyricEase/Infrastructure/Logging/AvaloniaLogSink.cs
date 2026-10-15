using Avalonia.Logging;
using Microsoft.Extensions.Logging;

namespace ALyricEase.Infrastructure.Logging;

internal sealed class AvaloniaLogSink(ILoggerFactory factory) : ILogSink
{
    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        => Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
        params object?[] propertyValues)
    {
        if (!IsEnabled(level, area)) return;
        var logLevel = level switch
        {
            LogEventLevel.Fatal => LogLevel.Critical,
            LogEventLevel.Error => LogLevel.Error,
            _ => LogLevel.Warning,
        };
        // Avalonia 自身提供模板；不展开控件树和 source 对象。
#pragma warning disable CA2254
        factory.CreateLogger("Avalonia." + area).Log(logLevel, messageTemplate, propertyValues);
#pragma warning restore CA2254
    }
}
