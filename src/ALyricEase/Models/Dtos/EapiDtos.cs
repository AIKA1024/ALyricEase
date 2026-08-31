using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>网易云 eapi 接口响应 DTO。</summary>

public sealed record PlayUrlResponse
{
    public int Code { get; init; }

    public List<PlayUrlItem>? Data { get; init; }
}

public sealed record PlayUrlItem
{
    public long Id { get; init; }

    /// <summary>播放地址。null 表示免费不可播(海外 IP/VIP/版权)。</summary>
    public string? Url { get; init; }

    public int Br { get; init; }

    /// <summary>服务端实际返回的音质档位；可能低于请求档位。</summary>
    public string Level { get; init; } = "";

    public int Fee { get; init; }

    /// <summary>true 表示仅 30 秒试听。</summary>
    [JsonPropertyName("isTrial")] public bool? IsTrial { get; init; }
}
