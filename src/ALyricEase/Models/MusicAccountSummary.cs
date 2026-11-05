namespace ALyricEase.Models;

/// <summary>跨平台账号页使用的稳定摘要；平台协议字段在各客户端内部完成映射。</summary>
public sealed record MusicAccountSummary
{
    public long UserId { get; init; }
    public string Nickname { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public bool IsVip { get; init; }
    public string MembershipName { get; init; } = "普通用户";
    public int MembershipLevel { get; init; }
    public int AccountLevel { get; init; }

    /// <summary>关注数(自己用户页头部的"关注")。平台无此读数时为 0。</summary>
    public int Follows { get; init; }

    /// <summary>粉丝数(自己用户页头部的"粉丝")。平台无此读数时为 0。</summary>
    public int Followeds { get; init; }

    public DateTimeOffset? MembershipExpiresAt { get; init; }
}
