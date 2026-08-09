using System.Text.RegularExpressions;
using ALyricEase.Models;

namespace ALyricEase.Services.Lrc;

/// <summary>LRC 解析:支持一行多时间标签、[offset:]、原文+翻译按最近时间合并(±300ms)。</summary>
public static class LrcParser
{
    private static readonly Regex TimeTag = new(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex MetaTag = new(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MergeToleranceMs = 300;

    /// <summary>解析原文 LRC,并把翻译 LRC(tlyric)按时间轴最近对齐合并进各行。</summary>
    public static LyricDocument Parse(string original, string translation = "")
    {
        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;

        foreach (var raw in original.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

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

        return new LyricDocument { Lines = lines, Offset = offset };
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
