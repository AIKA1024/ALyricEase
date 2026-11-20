namespace ALyricEase.Models;

/// <summary>已解码的歌词文本。Original 支持 LRC 和 QQ 音乐带字尾时间标记的 QRC。</summary>
public sealed class LyricResult
{
    public string Original { get; init; } = "";

    public string Translation { get; init; } = "";

    /// <summary>网易云逐字歌词原始文本(yrc)。空 = 该歌没有逐字,或本次走的是不返回 yrc 的明文回落通道。</summary>
    public string Yrc { get; init; } = "";

    /// <summary>逐字接口成功时可缓存。普通接口的回退结果和旧 QQ 缓存允许下次播放时重试。</summary>
    public bool Cacheable { get; init; } = true;
}
