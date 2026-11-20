namespace ALyricEase.Models;

/// <summary>逐字歌词的最小单元(中文单字 / 英文整词 / 标点 / 承载句尾长音的空格)。
/// 时间来自网易云 YRC 或 QQ 音乐 QRC 的歌曲绝对毫秒，offset 由播放端统一叠加。</summary>
public readonly record struct LyricWord(string Text, long StartMs, long EndMs)
{
    public long DurationMs => EndMs - StartMs;
}
