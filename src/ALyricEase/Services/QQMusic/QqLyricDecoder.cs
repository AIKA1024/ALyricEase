using System.IO.Compression;
using System.Net;
using System.Text;
using Lyricify.Lyrics.Decrypter.Qrc;

namespace ALyricEase.Services.QQMusic;

internal static class QqLyricDecoder
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly byte[][][] Schedule = CreateSchedule();

    public static string Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxBytes * 2) return "";
        var text = value.Trim();
        try
        {
            if (text.All(char.IsAsciiHexDigit))
            {
                if (text.Length % 16 != 0) return "";
                text = Decrypt(Convert.FromHexString(text));
            }
            else if (!text.StartsWith('[') && !text.StartsWith('<'))
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            }
        }
        catch (FormatException) { return ""; }
        catch (InvalidDataException) { return ""; }

        text = text.TrimStart('\uFEFF').TrimStart();
        if (!text.StartsWith('<')) return text.StartsWith('[') ? text : "";

        // LyricContent 内保留了原始换行，通用 XML 属性解析会把它们归一成空格。
        const string attribute = "LyricContent=\"";
        int start = text.IndexOf(attribute, StringComparison.Ordinal);
        if (start < 0) return "";
        start += attribute.Length;
        int end = text.IndexOf("\"/>", start, StringComparison.Ordinal);
        if (end < 0) end = text.IndexOf("\" />", start, StringComparison.Ordinal);
        return end < 0 ? "" : WebUtility.HtmlDecode(text[start..end]);
    }

    private static byte[][][] CreateSchedule()
    {
        var schedule = new byte[3][][];
        for (var i = 0; i < schedule.Length; i++)
            schedule[i] = Enumerable.Range(0, 16).Select(_ => new byte[6]).ToArray();
        DESHelper.TripleDESKeySetup("!@#)(*$%123ZXC!@!@#)(NHL"u8.ToArray(), schedule, DESHelper.DECRYPT);
        return schedule;
    }

    private static string Decrypt(byte[] bytes)
    {
        // 复用 8 字节块，避免为每个块复制其后的整段密文。
        var block = new byte[8];
        for (var i = 0; i < bytes.Length; i += block.Length)
        {
            Array.Copy(bytes, i, block, 0, block.Length);
            DESHelper.TripleDESCrypt(block, block, Schedule);
            block.CopyTo(bytes, i);
        }

        using var compressed = new MemoryStream(bytes);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        Span<byte> buffer = stackalloc byte[4096];
        int count;
        while ((count = inflater.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxBytes) return "";
            output.Write(buffer[..count]);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }
}
