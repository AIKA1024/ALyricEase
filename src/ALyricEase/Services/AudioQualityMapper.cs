namespace ALyricEase.Services;

/// <summary>
/// 把设置页统一的音质选项映射为各音源的协议档位。
/// 同一个设置索引在网易云与 QQ 音乐上的含义不同，播放时按歌曲来源选择。
/// </summary>
internal static class AudioQualityMapper
{
    public const int OptionCount = 4;

    public static int NormalizeIndex(int index) => Math.Clamp(index, 0, OptionCount - 1);

    public static string GetRequestLevel(MusicSource source, int index)
    {
        index = NormalizeIndex(index);
        return source switch
        {
            MusicSource.NetEase => index switch
            {
                0 => "exhigh",  // 极高(HQ)
                1 => "lossless", // 无损(SQ)
                2 => "jyeffect", // 高清臻音(Spatial Audio)
                _ => "sky",      // 臻音全景声(Audio Vivid)
            },
            MusicSource.QQ => index switch
            {
                0 => "standard", // M500 / 128k MP3
                1 => "nac",      // TL01 / 腾讯自研 AICodec
                2 => "higher",   // M800 / 320k MP3
                _ => "lossless", // F000 / FLAC
            },
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "未知音乐源"),
        };
    }
}
