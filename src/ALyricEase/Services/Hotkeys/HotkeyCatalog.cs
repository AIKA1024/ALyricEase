using Avalonia.Input;

namespace ALyricEase.Services.Hotkeys;

/// <summary>一条可自定义的快捷键:稳定 Id(落盘键名)+ 分组 + 标题 + 出厂默认组合。</summary>
public sealed record HotkeyActionDefinition(string Id, string Group, string Title, HotkeyGesture Default);

/// <summary>
/// 应用内快捷键清单(单一事实来源)。
/// <para>
/// 设置弹窗与默认值回落共用这张表；新增动作还需在 HotkeyService 中接入执行逻辑。
/// </para>
/// <para>
/// ⚠ Id 是 state.json 的键,**一旦发布不要再改**;要换语义就新增一条并让旧 Id 自然失效。
/// 默认键位避开 Enter / Esc(弹窗与搜索框已有语义)、Tab(焦点导航)和所有带 Win 的组合。
/// </para>
/// </summary>
public static class HotkeyCatalog
{
    public const string PlayPause = "play-pause";
    public const string PreviousTrack = "previous-track";
    public const string NextTrack = "next-track";
    public const string SeekBackward = "seek-backward";
    public const string SeekForward = "seek-forward";
    public const string VolumeUp = "volume-up";
    public const string VolumeDown = "volume-down";
    public const string CyclePlaybackMode = "cycle-playback-mode";
    public const string OpenSearch = "open-search";
    public const string ToggleNowPlaying = "toggle-now-playing";
    public const string ToggleDesktopLyric = "toggle-desktop-lyric";
    public const string ToggleDesktopLyricTranslation = "toggle-desktop-lyric-translation";
    public const string ToggleDesktopLyricClickThrough = "toggle-desktop-lyric-click-through";

    public const string GroupPlayback = "播放控制";
    public const string GroupVolume = "音量";
    public const string GroupNavigation = "导航";
    public const string GroupDesktopLyric = "桌面歌词";

    /// <summary>后退/前进一次跳过的毫秒数。</summary>
    public const long SeekStepMs = 5_000;

    /// <summary>音量键一次调整的百分比。</summary>
    public const int VolumeStep = 5;

    public static IReadOnlyList<HotkeyActionDefinition> Actions { get; } =
    [
        new(PlayPause, GroupPlayback, "播放 / 暂停",
            new HotkeyGesture(Key.Space, KeyModifiers.None)),
        new(PreviousTrack, GroupPlayback, "上一首",
            new HotkeyGesture(Key.Left, KeyModifiers.Control)),
        new(NextTrack, GroupPlayback, "下一首",
            new HotkeyGesture(Key.Right, KeyModifiers.Control)),
        new(SeekBackward, GroupPlayback, "后退 5 秒",
            new HotkeyGesture(Key.Left, KeyModifiers.Control | KeyModifiers.Shift)),
        new(SeekForward, GroupPlayback, "前进 5 秒",
            new HotkeyGesture(Key.Right, KeyModifiers.Control | KeyModifiers.Shift)),
        new(VolumeUp, GroupVolume, "音量增大",
            new HotkeyGesture(Key.Up, KeyModifiers.Control)),
        new(VolumeDown, GroupVolume, "音量减小",
            new HotkeyGesture(Key.Down, KeyModifiers.Control)),
        new(CyclePlaybackMode, GroupPlayback, "切换播放模式",
            new HotkeyGesture(Key.L, KeyModifiers.Control)),
        new(OpenSearch, GroupNavigation, "打开搜索",
            new HotkeyGesture(Key.F, KeyModifiers.Control)),
        new(ToggleNowPlaying, GroupNavigation, "打开 / 关闭播放详情",
            new HotkeyGesture(Key.Enter, KeyModifiers.Control)),
        new(ToggleDesktopLyric, GroupDesktopLyric, "打开 / 关闭桌面歌词",
            new HotkeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift)),
        new(ToggleDesktopLyricTranslation, GroupDesktopLyric, "显示 / 隐藏翻译",
            new HotkeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift)),
        new(ToggleDesktopLyricClickThrough, GroupDesktopLyric, "切换鼠标穿透",
            new HotkeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
    ];

    public static HotkeyActionDefinition? Find(string? id)
        => id is null ? null : Actions.FirstOrDefault(a => a.Id == id);

    /// <summary>默认组合;未收录的 Id 返回空(等于"不可用")。</summary>
    public static HotkeyGesture GetDefault(string id) => Find(id)?.Default ?? HotkeyGesture.None;
}
