namespace ALyricEase.Models;

/// <summary>本地音乐歌单:用户在侧栏"本地音乐"分组下创建的歌单实体。
/// Tracks 为导入的本地音频绝对路径(含单文件与整个文件夹枚举的结果),state.json 持久化;
/// 歌曲元数据由文件名派生(LocalAudioFiles.CreateSong),播放直接走文件,不走任何在线 API。</summary>
public sealed class LocalPlaylist
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>自定义封面文件名(covers/ 目录下;null = 默认取歌单内第一首带内嵌封面的歌)。</summary>
    public string? CustomCover { get; set; }

    public List<string> Tracks { get; set; } = new();
}
