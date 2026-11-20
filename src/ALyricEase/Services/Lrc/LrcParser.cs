using System.Text.RegularExpressions;
using ALyricEase.Models;

namespace ALyricEase.Services.Lrc;

/// <summary>LRC 解析:支持一行多时间标签、[offset:]、原文+翻译按最近时间合并(±300ms)。
/// 支持 QQ 的 QRC 字尾时间标记，以及网易云单独提供的 YRC 时间轴。</summary>
public static class LrcParser
{
    private static readonly Regex TimeTag = new(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex MetaTag = new(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QrcLine = new(@"^\[(\d+),\d+\]", RegexOptions.Compiled);
    private static readonly Regex QrcWord = new(@"\((\d+),(\d+)\)", RegexOptions.Compiled);

    private const int MergeToleranceMs = 300;

    /// <summary>yrc 行与 lrc 行的对齐容差。两份歌词是不同批次打的轴,实测可差几百毫秒
    /// (《我记得》21 行只对上 12 行);文本匹配本身是强约束,放宽到 1s 也不会贴错行。</summary>
    private const int WordTimingToleranceMs = 1000;

    /// <summary>解析原文 LRC,并把翻译 LRC(tlyric)按时间轴最近对齐合并进各行。
    /// yrc 非空时再合并逐字时间轴(文本对不齐的行自动放弃,保持整行高亮)。</summary>
    public static LyricDocument Parse(string original, string translation = "", string yrc = "")
    {
        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;

        foreach (var raw in original.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (ParseQrcLine(line) is { } qrcLine)
            {
                lines.Add(qrcLine);
                continue;
            }

            var meta = MetaTag.Match(line);
            if (meta.Success)
            {
                offset = TimeSpan.FromMilliseconds(long.Parse(meta.Groups[1].Value));
                continue;
            }

            var matches = TimeTag.Matches(line);
            if (matches.Count == 0) continue;

            var text = TimeTag.Replace(line, "").Trim();
            if (text.Length == 0) continue;

            foreach (Match m in matches)
                lines.Add(new LyricLine(TimeSpan.FromMilliseconds(ParseTime(m)), text));
        }

        lines.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        MergeTranslation(lines, translation);
        MergeWordTiming(lines, yrc);

        return new LyricDocument { Lines = lines, Offset = offset };
    }

    // QRC 行头和字尾的时间都是相对于歌曲开头的毫秒数。
    private static LyricLine? ParseQrcLine(string line)
    {
        var head = QrcLine.Match(line);
        if (!head.Success || !int.TryParse(head.Groups[1].ValueSpan, out var start)) return null;
        var content = line[head.Length..];
        if (string.IsNullOrWhiteSpace(content)) return null;
        var words = new List<LyricWord>();
        int cursor = 0;
        foreach (Match tag in QrcWord.Matches(content))
        {
            if (!int.TryParse(tag.Groups[1].ValueSpan, out var wordStart) ||
                !int.TryParse(tag.Groups[2].ValueSpan, out var duration)) break;
            var word = content[cursor..tag.Index];
            if (word.Length > 0)
                words.Add(new LyricWord(word, wordStart, (long)wordStart + duration));
            cursor = tag.Index + tag.Length;
        }
        var time = TimeSpan.FromMilliseconds(start);
        if (words.Count > 0 && cursor == content.Length)
            return new LyricLine(time, string.Concat(words.Select(w => w.Text))) { Words = words };
        var plain = QrcWord.Replace(content, "").Trim();
        return plain.Length == 0 ? null : new LyricLine(time, plain);
    }

    private static long ParseTime(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value);
        var seconds = int.Parse(m.Groups[2].Value);
        var frac = m.Groups[3].Value;
        var ms = frac.Length switch
        {
            0 => 0,
            1 => int.Parse(frac) * 100,
            2 => int.Parse(frac) * 10,
            _ => int.Parse(frac),
        };
        return minutes * 60_000L + seconds * 1_000L + ms;
    }

    private static void MergeTranslation(List<LyricLine> lines, string translation)
    {
        if (string.IsNullOrWhiteSpace(translation)) return;

        var translated = new List<(long Time, string Text)>();
        foreach (var raw in translation.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (ParseQrcLine(line) is { } qrcLine)
            {
                translated.Add((qrcLine.TimeMs, qrcLine.Original));
                continue;
            }

            var matches = TimeTag.Matches(line);
            if (matches.Count == 0) continue;

            var text = TimeTag.Replace(line, "").Trim();
            if (text.Length == 0) continue;

            foreach (Match m in matches)
                translated.Add((ParseTime(m), text));
        }

        if (translated.Count == 0) return;
        translated.Sort((a, b) => a.Time.CompareTo(b.Time));

        foreach (var line in lines)
        {
            var match = FindNearest(translated, line.TimeMs);
            if (match >= 0 && Math.Abs(translated[match].Time - line.TimeMs) <= MergeToleranceMs)
                line.Translation = translated[match].Text;
        }
    }

    /// <summary>把 yrc 的字级时间按行时间贴到 LRC 行上。
    /// 文本对不齐的行直接放弃 —— yrc 与 lrc 实测有两类差异(全半角括号、和声括号被剥:
    /// lrc「（为什么）」→ yrc「为什么」),按字符索引硬贴会整体错位,宁可这一行退回整行高亮。</summary>
    private static void MergeWordTiming(List<LyricLine> lines, string yrc)
    {
        if (string.IsNullOrWhiteSpace(yrc)) return;

        var yrcLines = YrcParser.Parse(yrc);
        if (yrcLines.Count == 0) return;

        foreach (var line in lines)
        {
            var idx = FindNearestYrc(yrcLines, line.TimeMs);
            if (idx < 0) continue;

            var candidate = yrcLines[idx];
            if (Math.Abs(candidate.StartMs - line.TimeMs) > WordTimingToleranceMs) continue;
            if (candidate.Words.Count == 0) continue;
            if (!TextMatches(line.Original, candidate.Words)) continue;

            line.Words = candidate.Words;
        }
    }

    /// <summary>二分:返回 yrc 中起始时间最接近 target 的行下标。</summary>
    private static int FindNearestYrc(IReadOnlyList<YrcLine> sorted, long target)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (sorted[mid].StartMs <= target) lo = mid + 1;
            else hi = mid - 1;
        }
        if (hi < 0) return 0;
        if (lo >= sorted.Count) return sorted.Count - 1;
        return Math.Abs(sorted[hi].StartMs - target) <= Math.Abs(sorted[lo].StartMs - target) ? hi : lo;
    }

    /// <summary>忽略空白与括号逐字符比较 lrc 文本与 yrc 字列表。
    /// 两家文本的差异集中在两处:全半角括号、和声括号被剥(lrc「（为什么）」→ yrc「为什么」),
    /// 把括号整个跳过就能让这些行也挂上字级时间。</summary>
    private static bool TextMatches(string original, IReadOnlyList<LyricWord> words)
    {
        var i = 0;
        foreach (var word in words)
        {
            foreach (var ch in word.Text)
            {
                if (IsAlignIgnorable(ch)) continue;
                while (i < original.Length && IsAlignIgnorable(original[i])) i++;
                if (i >= original.Length) return false;
                if (original[i] != ch) return false;
                i++;
            }
        }

        while (i < original.Length && IsAlignIgnorable(original[i])) i++;
        return i >= original.Length;
    }

    private static bool IsAlignIgnorable(char ch) =>
        char.IsWhiteSpace(ch) || ch is '(' or ')' or '（' or '）';

    /// <summary>二分:返回 sorted 中时间最接近 target 的下标。</summary>
    private static int FindNearest(List<(long Time, string Text)> sorted, long target)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (sorted[mid].Time <= target) lo = mid + 1;
            else hi = mid - 1;
        }
        if (hi < 0) return 0;
        if (lo >= sorted.Count) return sorted.Count - 1;
        return Math.Abs(sorted[hi].Time - target) <= Math.Abs(sorted[lo].Time - target) ? hi : lo;
    }
}
