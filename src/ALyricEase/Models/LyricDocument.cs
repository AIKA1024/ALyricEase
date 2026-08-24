namespace ALyricEase.Models;

/// <summary>一首歌的解析结果。Offset 来自 [offset:±ms],播放时叠加参与高亮比较。</summary>
public sealed class LyricDocument
{
    public IReadOnlyList<LyricLine> Lines { get; init; } = [];

    public TimeSpan Offset { get; init; }

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>二分查找当前句:最大的 (Time - Offset) &lt;= positionMs 的行。无则 -1。</summary>
    public int FindIndex(long positionMs)
    {
        var offsetMs = (long)Offset.TotalMilliseconds;
        int lo = 0, hi = Lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Lines[mid].TimeMs - offsetMs <= positionMs)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }
}
