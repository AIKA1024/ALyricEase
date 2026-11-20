using System.Text.RegularExpressions;
using ALyricEase.Models;

namespace ALyricEase.Services.Lrc;

/// <summary>网易云 yrc(逐字歌词)解析。实测格式(2026-11-18,4 首样本):
/// <code>
/// {"t":0,"c":[{"tx":"作词: "},{"tx":"唐恬"}]}          ← 头部/尾部 credits,JSON 行,整行跳过
/// [33100,8050](33100,400,0)深(33500,230,0)海...
/// </code>
/// 要点:
/// <list type="bullet">
/// <item>行头是 <c>[start,dur]</c> <b>两字段</b>,第三字段可选 —— 按三字段写正则会一行都匹配不上。</item>
/// <item>中文按字、英文按词、标点/空格独立成字。</item>
/// <item>句尾长音由<b>空格字</b>承载(实测 3.3s / 18.3s),所以这里只 TrimEnd('\r'),绝不能 Trim 行尾。</item>
/// </list>
/// 数据来源:weapi <c>/song/lyric/v1</c>(带 yv/ytv/yrv=-1);明文通道不返回 yrc。</summary>
public static class YrcParser
{
    private static readonly Regex LineHead = new(@"^\[(\d+),(\d+)(?:,\d+)?\]", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"\((\d+),(\d+)(?:,\d+)?\)", RegexOptions.Compiled);

    /// <summary>解析 yrc 文本。非歌词行(JSON credits/空行)自动跳过;无法识别的行原样忽略。</summary>
    public static IReadOnlyList<YrcLine> Parse(string yrc)
    {
        var lines = new List<YrcLine>();
        if (string.IsNullOrEmpty(yrc)) return lines;

        foreach (var raw in yrc.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] != '[') continue;

            var head = LineHead.Match(line);
            if (!head.Success) continue;

            var start = long.Parse(head.Groups[1].Value);
            var duration = long.Parse(head.Groups[2].Value);

            var rest = line[head.Length..];
            var tags = WordTag.Matches(rest);
            if (tags.Count == 0) continue;

            var words = new List<LyricWord>(tags.Count);
            for (var i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                var textStart = tag.Index + tag.Length;
                var textEnd = i + 1 < tags.Count ? tags[i + 1].Index : rest.Length;
                var text = textStart < textEnd ? rest[textStart..textEnd] : "";

                var wordStart = long.Parse(tag.Groups[1].Value);
                var wordDur = long.Parse(tag.Groups[2].Value);
                words.Add(new LyricWord(text, wordStart, wordStart + wordDur));
            }

            lines.Add(new YrcLine(start, duration, words));
        }

        return lines;
    }
}

/// <summary>yrc 的一行:行头时间 + 字列表。<see cref="DurationMs"/> 是行头原值(含句尾停顿)。</summary>
public sealed record YrcLine(long StartMs, long DurationMs, IReadOnlyList<LyricWord> Words)
{
    public long EndMs => StartMs + DurationMs;
}
