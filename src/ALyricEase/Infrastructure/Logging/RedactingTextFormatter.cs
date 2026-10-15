using System.Globalization;
using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace ALyricEase.Infrastructure.Logging;

/// <summary>连异常文本一起脱敏；调用方仍不得记录凭证、响应正文或完整歌曲对象。</summary>
internal sealed partial class RedactingTextFormatter : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _inner = new(
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [PID:{ProcessId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
        CultureInfo.InvariantCulture);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        try
        {
            var text = UrlPattern().Replace(buffer.ToString(), "[URL redacted]");
            // 出现凭证字段时隐藏该行剩余内容，兼顾 Cookie 多键值和 Authorization Bearer。
            output.Write(CredentialPattern().Replace(text, "$1=[redacted]"));
        }
        catch (RegexMatchTimeoutException)
        {
            output.WriteLine("[Log entry omitted: redaction timeout]");
        }
    }

    [GeneratedRegex("https?://[^\\s<>\"']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex UrlPattern();

    [GeneratedRegex("\\b(Cookie|Set-Cookie|Authorization|MUSIC_U|MUSIC_A|__csrf|access_token|refresh_token|token|password|skey|p_skey|qqmusic_key|qm_keyst|uin|wxunionid)[\"']?\\s*[:=][^\\r\\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex CredentialPattern();
}
