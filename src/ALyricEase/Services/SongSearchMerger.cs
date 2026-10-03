using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ALyricEase.Models;

namespace ALyricEase.Services;

/// <summary>
/// 将两个平台的歌曲搜索结果按“同一段录音”保守合并。误合并的代价高于重复展示，
/// 因此歌名、歌手、时长缺少任一关键证据时默认保留两条。
/// </summary>
public static partial class SongSearchMerger
{
    private const double MatchThreshold = 0.84;
    private const double RrfOffset = 60;

    [Flags]
    private enum VersionMarkers
    {
        None = 0,
        Live = 1 << 0,
        Remix = 1 << 1,
        Remaster = 1 << 2,
        Instrumental = 1 << 3,
        Acoustic = 1 << 4,
        Demo = 1 << 5,
        Cover = 1 << 6,
        RadioEdit = 1 << 7,
        ReRecorded = 1 << 8,
    }

    private sealed class Group(Song first, int netEaseRank, int qqRank)
    {
        public List<Song> Songs { get; } = [first];
        public int NetEaseRank { get; } = netEaseRank;
        public int QqRank { get; private set; } = qqRank;

        public void AddQq(Song song, int rank)
        {
            Songs.Add(song);
            QqRank = rank;
        }

        public double RankScore
            => (NetEaseRank >= 0 ? 1d / (RrfOffset + NetEaseRank + 1) : 0)
               + (QqRank >= 0 ? 1d / (RrfOffset + QqRank + 1) : 0);
    }

    /// <summary>
    /// 合并网易云与 QQ 搜索结果。只进行跨平台一对一折叠，不会把同一平台的多个版本互相吞掉。
    /// <paramref name="preference"/> 用于从匹配组中选择实际展示和播放的主音源。
    /// </summary>
    public static List<Song> Merge(
        IReadOnlyList<Song> netEase,
        IReadOnlyList<Song> qq,
        int limit,
        Func<Song, int>? preference = null)
    {
        if (limit <= 0) return [];

        var groups = netEase
            .Select((song, rank) => new Group(song, rank, -1))
            .ToList();

        for (var qqRank = 0; qqRank < qq.Count; qqRank++)
        {
            var qqSong = qq[qqRank];
            Group? best = null;
            var bestScore = 0d;
            foreach (var group in groups)
            {
                // 一对一匹配：一个网易云结果最多折叠一个 QQ 结果，避免不同版本串组。
                if (group.QqRank >= 0) continue;
                var score = GetMatchConfidence(group.Songs[0], qqSong);
                if (score > bestScore)
                {
                    best = group;
                    bestScore = score;
                }
            }

            if (best is not null && bestScore >= MatchThreshold)
                best.AddQq(qqSong, qqRank);
            else
                groups.Add(new Group(qqSong, -1, qqRank));
        }

        return groups
            .OrderByDescending(group => group.RankScore)
            .ThenBy(group => MinKnownRank(group.NetEaseRank, group.QqRank))
            .Take(limit)
            .Select(group => SelectPrimary(group.Songs, preference))
            .ToList();
    }

    /// <summary>返回 0..1 的匹配置信度；0 表示存在明确冲突，不应合并。</summary>
    public static double GetMatchConfidence(Song left, Song right)
    {
        if (!string.IsNullOrWhiteSpace(left.Isrc) && !string.IsNullOrWhiteSpace(right.Isrc))
            return string.Equals(NormalizeText(left.Isrc), NormalizeText(right.Isrc), StringComparison.Ordinal)
                ? 1d
                : 0d;

        var leftMarkers = DetectVersionMarkers(left);
        var rightMarkers = DetectVersionMarkers(right);
        // 一边明确标出 Live/伴奏/Remix 等而另一边没有，宁可重复展示也不误合并。
        if (leftMarkers != rightMarkers && (leftMarkers != VersionMarkers.None || rightMarkers != VersionMarkers.None))
            return 0d;

        if (left.OriginalVersion is not null && right.OriginalVersion is not null
            && left.OriginalVersion != right.OriginalVersion)
            return 0d;

        var leftTitle = NormalizeTitle(left.Name);
        var rightTitle = NormalizeTitle(right.Name);
        if (leftTitle.Length == 0 || !string.Equals(leftTitle, rightTitle, StringComparison.Ordinal))
            return 0d;

        var artistScore = GetArtistScore(left, right);
        if (artistScore == 0d) return 0d;

        var score = 0.5d + artistScore;
        if (left.DurationMs > 0 && right.DurationMs > 0)
        {
            var difference = Math.Abs(left.DurationMs - right.DurationMs);
            if (difference > 5_000) return 0d;
            score += difference <= 1_500 ? 0.20d
                : difference <= 3_000 ? 0.17d
                : 0.12d;
        }

        if (SameNonEmptyText(left.Album, right.Album)) score += 0.05d;
        if (SamePublishYear(left.PublishDate, right.PublishDate)) score += 0.03d;
        if (leftMarkers != VersionMarkers.None) score += 0.03d;
        return Math.Min(score, 1d);
    }

    private static Song SelectPrimary(IReadOnlyList<Song> songs, Func<Song, int>? preference)
    {
        var primary = songs
            .Select((song, index) => (song, index, score: preference?.Invoke(song) ?? DefaultPreference(song)))
            .OrderByDescending(candidate => candidate.score)
            .ThenBy(candidate => candidate.index)
            .First().song;
        primary.AlternateRecordings = songs.Where(song => !ReferenceEquals(song, primary)).ToList();
        return primary;
    }

    private static int DefaultPreference(Song song)
        => (song.IsNoCopyright ? -100 : 0)
           + (song.Fee == 0 ? 20 : 0)
           + (song.Source == MusicSource.NetEase ? 1 : 0);

    private static int MinKnownRank(int left, int right)
        => left < 0 ? right : right < 0 ? left : Math.Min(left, right);

    private static double GetArtistScore(Song left, Song right)
    {
        var leftArtists = GetArtists(left);
        var rightArtists = GetArtists(right);
        if (leftArtists.Count == 0 || rightArtists.Count == 0) return 0d;

        if (leftArtists.Count == rightArtists.Count
            && leftArtists.All(name => rightArtists.Any(other => ArtistNamesMatch(name, other))))
            return 0.25d;

        var shorter = leftArtists.Count <= rightArtists.Count ? leftArtists : rightArtists;
        var longer = ReferenceEquals(shorter, leftArtists) ? rightArtists : leftArtists;
        return shorter.All(name => longer.Any(other => ArtistNamesMatch(name, other))) ? 0.22d : 0d;
    }

    private static List<string> GetArtists(Song song)
    {
        var raw = song.ArtistNames.Count > 0
            ? song.ArtistNames
            : ArtistSeparatorRegex().Split(song.Artist);
        return raw.Select(NormalizeText).Where(name => name.Length > 0).Distinct().ToList();
    }

    private static bool ArtistNamesMatch(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        // 兼容“G.E.M.邓紫棋”与“邓紫棋”一类平台别名，但避免单字包含造成误判。
        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length <= right.Length ? right : left;
        return shorter.Length >= 2 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static string NormalizeTitle(string value)
        => NormalizeText(VersionTokenRegex().Replace(value.Normalize(NormalizationForm.FormKC), " "));

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber)
                builder.Append(rune);
        }
        return builder.ToString();
    }

    private static VersionMarkers DetectVersionMarkers(Song song)
    {
        var value = string.Join(' ', song.Name, song.Subtitle, song.Album).Normalize(NormalizationForm.FormKC);
        var markers = VersionMarkers.None;
        foreach (Match match in VersionTokenRegex().Matches(value))
        {
            if (match.Groups["live"].Success) markers |= VersionMarkers.Live;
            if (match.Groups["remix"].Success) markers |= VersionMarkers.Remix;
            if (match.Groups["remaster"].Success) markers |= VersionMarkers.Remaster;
            if (match.Groups["instrumental"].Success) markers |= VersionMarkers.Instrumental;
            if (match.Groups["acoustic"].Success) markers |= VersionMarkers.Acoustic;
            if (match.Groups["demo"].Success) markers |= VersionMarkers.Demo;
            if (match.Groups["cover"].Success) markers |= VersionMarkers.Cover;
            if (match.Groups["radio"].Success) markers |= VersionMarkers.RadioEdit;
            if (match.Groups["rerecorded"].Success) markers |= VersionMarkers.ReRecorded;
        }
        return markers;
    }

    private static bool SameNonEmptyText(string left, string right)
    {
        var normalizedLeft = NormalizeText(left);
        return normalizedLeft.Length > 0
               && string.Equals(normalizedLeft, NormalizeText(right), StringComparison.Ordinal);
    }

    private static bool SamePublishYear(string left, string right)
    {
        var leftYear = PublishYearRegex().Match(left).Value;
        var rightYear = PublishYearRegex().Match(right).Value;
        return leftYear.Length == 4 && string.Equals(leftYear, rightYear, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?ix)
        (?<live>\blive(?:\s+version)?\b|现场(?:版|录音)?|演唱会(?:版)?) |
        (?<remix>\bremix(?:ed)?\b|混音(?:版)?) |
        (?<remaster>\bremaster(?:ed)?(?:\s+\d{4})?\b|重制(?:版)?) |
        (?<instrumental>\binstrumental\b|\boff\s*vocal\b|\bkaraoke\b|伴奏(?:版)?|纯音乐(?:版)?) |
        (?<acoustic>\bacoustic(?:\s+version)?\b|\bunplugged\b|不插电(?:版)?) |
        (?<demo>\bdemo\b|小样(?:版)?) |
        (?<cover>\bcover(?:\s+version)?\b|翻唱(?:版)?) |
        (?<radio>\bradio\s+edit\b) |
        (?<rerecorded>\bre-?recorded\b|重新录制(?:版)?)")]
    private static partial Regex VersionTokenRegex();

    [GeneratedRegex(@"(?ix)\s*(?:/|&|、|,|，|;|；|\bfeat(?:uring)?\.?\b|\bft\.?\b)\s*")]
    private static partial Regex ArtistSeparatorRegex();

    [GeneratedRegex(@"(?:19|20)\d{2}")]
    private static partial Regex PublishYearRegex();
}
