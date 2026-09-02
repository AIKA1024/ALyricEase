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
    public DateTimeOffset? MembershipExpiresAt { get; init; }
}
