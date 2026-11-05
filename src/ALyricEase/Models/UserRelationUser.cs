using ALyricEase.Services;

namespace ALyricEase.Models;

/// <summary>关注/粉丝列表里的一个用户(列表页展示用最小集:点进去看他的用户页)。
/// 两个音源的"用户 id"形态不同:网易云是数字 uid,QQ 只有 encrypt_uin(数字 uin 不下发),
/// 所以两个都留着,Source 决定点行时走哪个入口。</summary>
public sealed record UserRelationUser(
    long UserId,
    string Nickname,
    string AvatarUrl,
    string Signature,
    int Follows,
    int Followeds,
    MusicSource Source = MusicSource.NetEase,
    string EncryptUin = "",
    string SingerMid = "");
