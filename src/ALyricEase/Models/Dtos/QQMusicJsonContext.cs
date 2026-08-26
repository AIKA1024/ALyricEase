using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>QQ 音乐 API 响应的 JSON 源生成上下文(NativeAOT 兼容,与 NetEaseJsonContext 同模式)。
/// 只注册根类型,嵌套类型由生成器自动纳入;PropertyNameCaseInsensitive 复刻上游大小写差异。</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(QQSearchResponse))]
[JsonSerializable(typeof(QQVkeyResponse))]
[JsonSerializable(typeof(QQLyricFcgResponse))]
[JsonSerializable(typeof(QQMusicuLyricResponse))]
[JsonSerializable(typeof(QQDetailResponse))]
[JsonSerializable(typeof(QQHomepageResponse))]
[JsonSerializable(typeof(QQCdListResponse))]
[JsonSerializable(typeof(QQRadarResponse))]
[JsonSerializable(typeof(QQSongEntriesResponse))]
[JsonSerializable(typeof(QQAlbumListResponse))]
[JsonSerializable(typeof(QQAlbumDetailResponse))]
[JsonSerializable(typeof(QQCgiGetDissResponse))]
internal sealed partial class QQMusicJsonContext : JsonSerializerContext
{
}
