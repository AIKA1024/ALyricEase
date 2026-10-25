namespace ALyricEase.Services;

/// <summary>
/// 把设置页统一的音质选项映射为各音源的协议档位。
/// 只有一个索引、一份名称,**从低到高**;两个音源按同一档位的真实含义各自映射
/// (2026-10-25 修正:旧映射里网易云从 exhigh 起跳,设置页写着"标准"实际发的是 320k)。
/// </summary>
internal static class AudioQualityMapper
{
    public const int OptionCount = 4;

    /// <summary>设置页显示的统一档位名(不区分音源,按质量从低到高)。</summary>
    public static IReadOnlyList<string> OptionNames { get; } =
        ["标准", "极高（HQ）", "无损（SQ）", "高解析度无损（Hi-Res）"];

    public static int NormalizeIndex(int index) => Math.Clamp(index, 0, OptionCount - 1);

    public static string GetRequestLevel(MusicSource source, int index)
    {
        index = NormalizeIndex(index);
        return source switch
        {
            MusicSource.NetEase => index switch
            {
                0 => "standard", // 标准 128k MP3
                1 => "exhigh",   // 极高(HQ) 320k MP3
                2 => "lossless", // 无损(SQ) FLAC
                _ => "hires",    // 高解析度无损(Hi-Res) 24bit FLAC
            },
            MusicSource.QQ => index switch
            {
                0 => "standard", // 标准品质 128k MP3(M500)
                1 => "higher",   // HQ高品质 320k MP3(M800)
                2 => "lossless", // SQ无损品质 FLAC(F000)
                _ => "hires",    // Hi-Res无损品质 FLAC(RS01)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "未知音乐源"),
        };
    }
}
