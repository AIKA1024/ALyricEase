using System.Text.Json;

namespace ALyricEase.Services;

internal static class SongQualityMetadata
{
    public static IReadOnlyList<string>? NetEase(JsonElement music)
    {
        if (music.ValueKind != JsonValueKind.Object) return null;
        music.TryGetProperty("l", out var standard);
        music.TryGetProperty("h", out var high);
        music.TryGetProperty("sq", out var lossless);
        music.TryGetProperty("hr", out var hires);
        return NetEase(standard, high, lossless, hires);
    }

    public static IReadOnlyList<string>? NetEase(JsonElement standard, JsonElement high,
        JsonElement lossless, JsonElement hires)
    {
        (string Level, JsonElement Value)[] fields =
            [("standard", standard), ("exhigh", high), ("lossless", lossless), ("hires", hires)];
        if (fields.All(f => f.Value.ValueKind == JsonValueKind.Undefined)) return null;
        return fields.Where(f => Positive(f.Value, "size") || Positive(f.Value, "br"))
            .Select(f => f.Level).ToArray();
    }

    public static IReadOnlyList<string>? QQ(JsonElement file, bool hasFullQualityMetadata = true)
    {
        (string Level, string Field)[] fields =
            [("standard", "size_128mp3"), ("higher", "size_320mp3"),
             ("lossless", "size_flac"), ("hires", "size_hires")];
        if (file.ValueKind != JsonValueKind.Object || !fields.Any(f => file.TryGetProperty(f.Field, out _)))
            return null;
        var levels = fields.Where(f => Positive(file, f.Field)).Select(f => f.Level).ToArray();
        // QQ ct=24 的列表/旧详情会把真实存在的 Hi-Res 清零。只有桌面元数据响应
        // 才能用零值排除档位；其他响应若未确认全部四档，交给下载时补查。
        return hasFullQualityMetadata || levels.Length == fields.Length ? levels : null;
    }

    private static bool Positive(JsonElement item, string field) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var size) && size > 0;
}
