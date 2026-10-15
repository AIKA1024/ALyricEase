using Microsoft.Extensions.Logging;
using Velopack.Logging;

namespace ALyricEase.Services;

/// <summary>将更新库的版本比较、增量回退和安装诊断写入应用的文件日志。</summary>
internal sealed class VelopackLogAdapter(ILogger logger) : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        var level = logLevel switch
        {
            VelopackLogLevel.Trace => LogLevel.Trace,
            VelopackLogLevel.Debug => LogLevel.Debug,
            VelopackLogLevel.Information => LogLevel.Information,
            VelopackLogLevel.Warning => LogLevel.Warning,
            VelopackLogLevel.Error => LogLevel.Error,
            VelopackLogLevel.Critical => LogLevel.Critical,
            _ => LogLevel.Information
        };
        // 库消息不是模板；交给统一 formatter 脱敏，包括异常中的 URL。
        logger.Log(level, exception, "{VelopackMessage}", message);
    }
}
