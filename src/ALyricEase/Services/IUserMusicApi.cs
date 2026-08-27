using ALyricEase.Models;
using ALyricEase.Models.Dtos;

namespace ALyricEase.Services;

/// <summary>账号能力扩展(需登录):用户资料、用户歌单、歌单曲目、每日推荐。
/// 网易云与 QQ 音乐均实现;未登录/凭据失效抛 ApiException,由调用方决定 UI 呈现。</summary>
public interface IUserMusicApi : IMusicApi
{
    /// <summary>当前登录用户资料(昵称/头像/userId)。未登录抛异常。</summary>
    Task<UserProfile> GetUserProfileAsync(CancellationToken ct = default);

    /// <summary>用户创建/收藏的歌单列表。未登录抛异常。</summary>
    Task<List<Playlist>> GetUserPlaylistsAsync(CancellationToken ct = default);

    /// <summary>歌单全量曲目(按歌单权威顺序)。未登录抛异常。</summary>
    Task<List<Song>> GetPlaylistTracksAsync(long id, CancellationToken ct = default);

    /// <summary>每日推荐歌曲(个性化,需登录;无内容返回空列表)。</summary>
    Task<List<Song>> GetDailyRecommendSongsAsync(CancellationToken ct = default);
}
