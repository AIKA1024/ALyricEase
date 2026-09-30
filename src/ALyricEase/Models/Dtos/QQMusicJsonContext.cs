using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>QQ 音乐 API 响应的 JSON 源生成上下文(NativeAOT 兼容,与 NetEaseJsonContext 同模式)。
/// 只注册根类型,嵌套类型由生成器自动纳入;PropertyNameCaseInsensitive 复刻上游大小写差异;
/// AllowReadingFromString 兜底个别接口把数字 id 下发为字符串的形态漂移。</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(QQSearchResponse))]
[JsonSerializable(typeof(QQVkeyResponse))]
[JsonSerializable(typeof(QQLyricFcgResponse))]
[JsonSerializable(typeof(QQMusicuLyricResponse))]
[JsonSerializable(typeof(QQDetailResponse))]
[JsonSerializable(typeof(QQHomepageResponse))]
[JsonSerializable(typeof(QQRecommendFeedResponse))]
[JsonSerializable(typeof(QQRadarResponse))]
[JsonSerializable(typeof(QQSongEntriesResponse))]
[JsonSerializable(typeof(QQAlbumListResponse))]
[JsonSerializable(typeof(QQAlbumDetailResponse))]
[JsonSerializable(typeof(QQUniformDissResponse))]
[JsonSerializable(typeof(QQCreatedPlaylistsResponse))]
[JsonSerializable(typeof(QQFavPlaylistsResponse))]
[JsonSerializable(typeof(QQUserInfoResponse))]
[JsonSerializable(typeof(QQVipLoginResponse))]
[JsonSerializable(typeof(QQMusicuSearchPlaylistResponse))]
[JsonSerializable(typeof(QQMusicuSearchSongResponse))]
internal sealed partial class QQMusicJsonContext : JsonSerializerContext
{
}
