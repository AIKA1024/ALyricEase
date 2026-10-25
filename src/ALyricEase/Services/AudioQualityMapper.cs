namespace ALyricEase.Services;

/// <summary>
/// 把设置页的音质选项映射为各音源的协议档位。
/// 两个音源的档位**各自独立**(名称不同,所以各存一份索引),顺序统一为**从低到高**。
/// (2026-10-25 之前共用一个索引,设置页写的"标准/较高/极高/无损"是 QQ 的叫法,
/// 网易云实际请求的却是 exhigh/lossless/空间音频——标签与真实档位完全对不上。)
/// </summary>
internal static class AudioQualityMapper
{
    public const int OptionCount = 4;

    private static readonly string[] NetEaseNames =
        ["标准", "极高（HQ）", "无损（SQ）", "高解析度无损（Hi-Res）"];

    private static readonly string[] QqNames =
        ["标准", "较高", "极高", "无损(仅VIP可用)"];

    public static IReadOnlyList<string> GetOptionNames(MusicSource source) => source switch
    {
        MusicSource.NetEase => NetEaseNames,
        MusicSource.QQ => QqNames,
        _ => [],
    };

    /// <summary>缺省档位:两边都取最高档(非会员/无该曲目源时由各音源的降级链自动回落)。</summary>
    public static int GetDefaultIndex(MusicSource source) => OptionCount - 1;

    public static int NormalizeIndex(int index) => Math.Clamp(index, 0, OptionCount - 1);

    public static string GetRequestLevel(MusicSource source, int index)
    {
        index = NormalizeIndex(index);
        return source switch
        {
            MusicSource.NetEase => index switch
            {
                0 => "standard", // 标准(128k)
                1 => "exhigh",   // 极高(HQ,320k)
                2 => "lossless", // 无损(SQ)
                _ => "hires",    // 高解析度无损(Hi-Res,24bit)
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

    /// <summary>旧配置只有一个全局档位,网易云那边按**实际请求过的档位**折算到新列表
    /// (旧 0=exhigh、1=lossless、2=空间音频、3=臻音全景声)。</summary>
    public static int MigrateLegacyNetEaseIndex(int legacyIndex) => NormalizeIndex(legacyIndex) switch
    {
        0 => 1, // 旧"标准"实际请求的是 exhigh ⇒ 极高
        1 => 2, // lossless ⇒ 无损
        2 => 2, // jyeffect(空间音频)无对应档 ⇒ 无损
        _ => 3, // sky(臻音全景声) ⇒ 最高档 Hi-Res
    };
}
