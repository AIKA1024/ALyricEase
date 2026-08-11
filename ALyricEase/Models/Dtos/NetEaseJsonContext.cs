using System.Text.Json.Serialization;
using ALyricEase.Services.Auth;

namespace ALyricEase.Models.Dtos;

/// <summary>网易云 API 响应的 JSON 源生成上下文(NativeAOT 兼容,替代反射反序列化)。
/// 只注册根类型,嵌套类型由生成器自动纳入;PropertyNameCaseInsensitive 复刻原 JsonSerializerOptions。</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SongDetailResponse))]
[JsonSerializable(typeof(PlayUrlResponse))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(LyricResponse))]
[JsonSerializable(typeof(LegacySearchResponse))]
[JsonSerializable(typeof(LegacyAccountResponse))]
[JsonSerializable(typeof(LegacyUserPlaylistResponse))]
[JsonSerializable(typeof(PlaylistDetailResponse))]
[JsonSerializable(typeof(LegacySongDetailResponse))]
[JsonSerializable(typeof(RecommendListResponse))]
[JsonSerializable(typeof(RecommendResourceResponse))]
[JsonSerializable(typeof(CookieStore.CookieFile))]
internal sealed partial class NetEaseJsonContext : JsonSerializerContext
{
}
