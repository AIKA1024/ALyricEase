using System.Text.Json;
using ALyricEase.Models;

namespace ALyricEase.Services.NetEase;

public sealed partial class NetEaseApiClient
{
    public async Task<IReadOnlyList<string>?> GetAvailableQualitiesAsync(Song song, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (song.AvailableQualityLevels is { } known) return known;

        // 网页 weapi/v3/song/detail 在部分网络返回空响应。专用音质接口走已验证的
        // interface eapi 通道；只读取曲库元数据，不逐档请求播放地址或推测账号权限。
        try
        {
            using var doc = await QueryQualityMetadataAsync("/api/song/music/detail/get",
                new Dictionary<string, object?> { ["songId"] = song.Id }, ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && SongQualityMetadata.NetEase(data) is { } levels)
                return levels;
        }
        catch (ApiException ex) when (ex.Code == BlockedCode || ex.Code == -2)
        {
            // 空响应/非 JSON 可以换详情端点；限速、登录错误及取消不重复请求。
        }

        using var detail = await QueryQualityMetadataAsync("/api/v3/song/detail",
            new Dictionary<string, object?> { ["c"] = $"[{{\"id\":{song.Id}}}]" }, ct).ConfigureAwait(false);
        if (detail.RootElement.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in songs.EnumerateArray())
                if (item.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) && value == song.Id)
                    return SongQualityMetadata.NetEase(item);
        }
        return null;
    }

    private async Task<JsonDocument> QueryQualityMetadataAsync(string path, Dictionary<string, object?> payload,
        CancellationToken ct)
    {
        payload["header"] = BuildEapiIdentityHeader();
        using var request = CreateEapiRequest(path, payload, includeRealIp: false, useInterfaceHost: true);
        var doc = await PostJsonAsync(request, ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value == 200)
            return doc;
        var errorCode = doc.RootElement.TryGetProperty("code", out code) && code.TryGetInt32(out value) ? value : -1;
        doc.Dispose();
        throw new ApiException("获取歌曲音质失败", errorCode);
    }
}
