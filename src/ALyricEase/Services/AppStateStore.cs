using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using ALyricEase.Services.Hotkeys;
using Avalonia.Threading;

namespace ALyricEase.Services;

/// <summary>应用状态持久化:侧栏状态、聚合歌单、搜索/播放历史、设置与主窗口几何。
/// Windows 存 %LocalAppData%\ALyricEase\config\state.json,Android 存应用私有
/// FilesDir\ALyricEase\config\state.json(折叠状态同样有意义;窗口字段仅桌面端读写)。
/// 写入走 tmp+Move 原子替换;文件损坏按全部默认值处理。</summary>
public sealed class AppStateStore
{
    public const int MaximumRecentSongCount = 100;
    private static readonly TimeSpan DeferredSaveDelay = TimeSpan.FromMilliseconds(400);
    private readonly string _path;
    private DispatcherTimer? _deferredSaveTimer;
    private bool _hasPendingSave;

    /// <summary>网易云歌单分组是否展开。</summary>
    public bool IsNetEaseGroupExpanded { get; set; } = true;

    /// <summary>QQ音乐歌单分组是否展开。</summary>
    public bool IsQqGroupExpanded { get; set; } = true;

    /// <summary>用户创建的聚合歌单(持久化;顺序即侧栏显示顺序)。</summary>
    public List<AggregatePlaylist> AggregatePlaylists { get; } = new();

    /// <summary>本地音乐歌单(用户在"本地音乐"分组下创建;导入的本地音频按歌单归档)。</summary>
    public List<LocalPlaylist> LocalPlaylists { get; } = new();

    /// <summary>搜索历史(最新在前,SearchViewModel 维护与落盘)。</summary>
    public List<string> SearchHistory { get; } = new();

    /// <summary>搜索页上次选择的来源；默认综合搜索。</summary>
    public SearchSourceMode PreferredSearchSource { get; private set; } = SearchSourceMode.Combined;

    /// <summary>全部专辑页显示偏好；未选择时移动端列表、桌面端大图。</summary>
    public bool? ArtistAlbumsListMode { get; set; }

    /// <summary>个人主页"创建的歌单"显示偏好；未选择时移动端列表、桌面端大图。</summary>
    public bool? HomeCreatedPlaylistsListMode { get; set; }

    /// <summary>个人主页"收藏的歌单"显示偏好；未选择时移动端列表、桌面端大图。</summary>
    public bool? HomeCollectedPlaylistsListMode { get; set; }

    public void SetPreferredSearchSource(SearchSourceMode mode)
    {
        if (!Enum.IsDefined(mode) || PreferredSearchSource == mode) return;
        PreferredSearchSource = mode;
        Save();
    }

    private readonly List<RecentPlaybackEntry> _recentSongs = new();

    /// <summary>最近成功开始播放的歌曲，播放次数优先、同次数时最近播放优先。</summary>
    public IReadOnlyList<Song> RecentSongs => _recentSongs.Select(static entry => entry.Song).ToArray();

    /// <summary>最近播放列表发生变化；播放器和页面均在 UI 线程使用。</summary>
    public event Action? RecentSongsChanged;

    /// <summary>
    /// 影响渲染的开关（动态背景等）发生变化。正在显示的界面据此立刻改绘 ——
    /// 这些开关只改"怎么画"，不会让状态失效，所以不走保存/重载那条路。
    /// </summary>
    public event Action? VisualEffectsChanged;

    /// <summary>综合搜索默认红心平台发生变化；已显示的合并行据此切换红心所代表的平台状态。</summary>
    public event Action? PreferredCombinedLikeSourceChanged;

    /// <summary>由设置项的 setter 调用（见 SettingsViewModel）。</summary>
    public void NotifyVisualEffectsChanged() => VisualEffectsChanged?.Invoke();

    /// <summary>主窗口常规态宽度(DIP);null = 从未记录过。</summary>
    public double? WindowWidth { get; set; }

    /// <summary>主窗口常规态高度(DIP)。</summary>
    public double? WindowHeight { get; set; }

    /// <summary>主窗口左上角 X(屏幕像素坐标,PixelPoint 原值)。</summary>
    public int? WindowX { get; set; }

    /// <summary>主窗口左上角 Y(屏幕像素坐标)。</summary>
    public int? WindowY { get; set; }

    /// <summary>上次关闭时是否处于最大化(恢复时套用,尺寸/位置字段保存的是常规态值)。</summary>
    public bool WindowMaximized { get; set; }

    // ---- 设置页偏好(SettingsViewModel 绑定;离散项即时保存,连续滑块延迟合并保存) ----

    /// <summary>关闭主窗口时：null=每次询问，true=隐藏到托盘，false=退出应用。</summary>
    public bool? MinimizeToTrayOnClose { get; set; }

    /// <summary>主题:System/Light/Dark。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>界面语言:System/zh-CN(当前仅存储偏好)。</summary>
    public string Language { get; set; } = "System";

    /// <summary>
    /// 性能与体验:Performance(最佳性能)/ Quality(最佳质量)。
    /// <para>
    /// 原先还有一档 Balanced(默认)。那一档对渲染毫无影响 —— 它和 Quality 画出来的东西
    /// 完全一样,只是个"看起来能调"的空位,2026-09-21 删掉,只留两个**真的会改画面**的档。
    /// 老配置文件里的 <c>"Balanced"</c> 等于"当年什么都不改" ⇒ 加载时归一到 Quality,
    /// 免得老用户升级后歌词突然不模糊了。
    /// </para>
    /// </summary>
    public string PerformanceMode { get; set; } = "Quality";

    /// <summary>
    /// 歌词行要不要模糊。整个"性能与体验"档位目前**只**管这一件事 ——
    /// 保留这个计算属性是为了让档位语义有个单一定义点,别在设置页和主 VM 里各解析一次字符串。
    /// </summary>
    public bool LyricBlurEnabled => PerformanceMode == "Quality";

    /// <summary>播放详情页动态背景效果。</summary>
    public bool DynamicBackground { get; set; } = true;

    public bool DesktopLyricEnabled { get; set; }
    public bool DesktopLyricClickThrough { get; set; }
    public bool DesktopLyricShowTranslation { get; set; } = true;
    /// <summary>播放暂停/停止时收起歌词窗口。默认关:保持"一直显示"的旧行为。</summary>
    public bool DesktopLyricHideWhenPaused { get; set; }
    public double DesktopLyricFontSize { get; set; } = 28;
    public double DesktopLyricBackgroundOpacity { get; set; } = 0.85;
    public int? DesktopLyricX { get; set; }
    public int? DesktopLyricY { get; set; }
    public int? AndroidLyricX { get; set; }
    public int? AndroidLyricY { get; set; }

    /// <summary>兼容的视觉效果。</summary>
    public bool CompatibilityVisual { get; set; } = true;

    /// <summary>统一音频质量档位(0=标准 / 1=极高 / 2=无损 / 3=Hi-Res,从低到高)。
    /// 两个音源按同一档位的真实含义各自映射;缺省 3 = 最高档,没会员时由降级链回落。</summary>
    public int AudioQuality { get; set; } = 3;

    /// <summary>传统播放控制。</summary>
    public bool LegacyPlaybackControl { get; set; }

    /// <summary>歌曲行的播放/喜欢按钮位置互换(播放钮盖封面、喜欢钮在行中列)。改动经
    /// VisualEffectsChanged 通知现有行即时切换。</summary>
    public bool SwapPlayAndLikeOnRows { get; set; }

    /// <summary>综合搜索合并歌曲的默认红心平台；null 表示首次有歧义时由用户选择。</summary>
    public MusicSource? PreferredCombinedLikeSource { get; private set; }

    /// <summary>记录综合搜索红心的默认平台。只接受当前支持账号“我喜欢”的两个音源。</summary>
    public void SetPreferredCombinedLikeSource(MusicSource source)
    {
        if (source is not (MusicSource.NetEase or MusicSource.QQ)) return;
        if (PreferredCombinedLikeSource == source) return;
        PreferredCombinedLikeSource = source;
        Save();
        PreferredCombinedLikeSourceChanged?.Invoke();
    }

    /// <summary>音频交叉淡化开关。</summary>
    public bool Crossfade { get; set; }

    /// <summary>交叉淡化时长(秒)。</summary>
    public double CrossfadeSeconds { get; set; } = 4;

    /// <summary>音频输出设备后端标识(null = 跟随系统默认设备)。</summary>
    public string? AudioOutputDeviceId { get; set; }

    /// <summary>音频输出设备展示名(设备 Id 失配时按名字二次匹配,也用于"已保存设备未连接"的提示)。</summary>
    public string? AudioOutputDeviceName { get; set; }

    /// <summary>播放模式(0=列表循环 1=单曲循环 2=随机播放)。</summary>
    public int PlaybackMode { get; set; }

    /// <summary>播放器音量(0-100)。</summary>
    public int Volume { get; set; } = 80;

    /// <summary>音乐、封面和歌词的统一磁盘缓存容量上限(MB)。</summary>
    public int MusicCacheMaximumSizeMb { get; set; } = MusicCacheService.DefaultMaximumSizeMb;

    // ---- 快捷键(动作 Id → 落盘手势文本;只记"用户改过的",出厂默认不在文件里) ----

    private readonly Dictionary<string, string> _hotkeys = new(StringComparer.Ordinal);

    /// <summary>勾选了「应用外也生效」的动作(注册成系统级热键)。
    /// 这是用户意图,实际能否注册由 <see cref="Hotkeys.GlobalHotkeyHost"/> 对账。</summary>
    private readonly HashSet<string> _globalHotkeys = new(StringComparer.Ordinal);

    /// <summary>用户改动过的快捷键(动作 Id → 手势文本;值 "" 表示用户显式清空)。
    /// 字典里没有的动作按 <see cref="HotkeyCatalog"/> 的默认值生效。</summary>
    public IReadOnlyDictionary<string, string> Hotkeys => _hotkeys;

    /// <summary>快捷键配置变化(设置弹窗改键、勾选全局、恢复默认);应用内分发无需订阅,它每次按键都现查。</summary>
    public event Action? HotkeysChanged;

    /// <summary>该动作是否勾选了「应用在后台也响应」。</summary>
    public bool IsGlobalHotkey(string actionId) => _globalHotkeys.Contains(actionId);

    /// <summary>写入全局标记。没有绑定(或绑定为空)的动作不予标记 —— 没键可注册。</summary>
    public void SetGlobalHotkey(string actionId, bool global)
    {
        if (HotkeyCatalog.Find(actionId) is null) return;
        if (global && ResolveHotkey(actionId).IsEmpty) return;
        var changed = global ? _globalHotkeys.Add(actionId) : _globalHotkeys.Remove(actionId);
        if (!changed) return;
        Save();
        HotkeysChanged?.Invoke();
    }

    /// <summary>取某个动作当前生效的组合:未改过 = 出厂默认,显式清空 = 空(不响应)。</summary>
    public HotkeyGesture ResolveHotkey(string actionId)
    {
        if (!_hotkeys.TryGetValue(actionId, out var text))
            return HotkeyCatalog.GetDefault(actionId);
        if (text == "") return HotkeyGesture.None;
        return HotkeyGesture.TryParse(text, out var gesture) ? gesture : HotkeyCatalog.GetDefault(actionId);
    }

    /// <summary>写入某个动作的组合。<paramref name="gesture"/> 等于出厂默认时移除覆盖项
    /// (配置文件只留真正改过的,升级后默认值仍能跟着演进)。</summary>
    public void SetHotkey(string actionId, HotkeyGesture gesture)
    {
        if (HotkeyCatalog.Find(actionId) is null) return;
        if (!gesture.IsEmpty && !gesture.IsValid) return;
        var text = gesture.ToStorageString();
        bool changed;
        if (text == HotkeyCatalog.GetDefault(actionId).ToStorageString())
        {
            changed = _hotkeys.Remove(actionId);
        }
        else if (_hotkeys.TryGetValue(actionId, out var existing))
        {
            changed = existing != text;
            if (changed) _hotkeys[actionId] = text;
        }
        else
        {
            _hotkeys[actionId] = text;
            changed = true;
        }

        // 绑定被清空 ⇒ 没有键可注册,顺手收回全局标记(否则界面会显示勾着却不生效)。
        if (gesture.IsEmpty && _globalHotkeys.Remove(actionId)) changed = true;

        if (!changed) return;
        Save();
        HotkeysChanged?.Invoke();
    }

    /// <summary>恢复全部出厂默认(清空覆盖项与全局标记)。</summary>
    public void ResetHotkeys()
    {
        if (_hotkeys.Count == 0 && _globalHotkeys.Count == 0) return;
        _hotkeys.Clear();
        _globalHotkeys.Clear();
        Save();
        HotkeysChanged?.Invoke();
    }

    public AppStateStore()
        : this(GetDefaultPath())
    {
    }

    /// <summary>显式路径构造仅供无头回归测试隔离真实用户状态。</summary>
    internal AppStateStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Load();
    }

    private static string GetDefaultPath()
    {
        // Android 上 LocalApplicationData = <应用私有 files 目录>/.local/share;
        // Windows 的实际目录见 AppDataRoot(⚠ 不能落在 Velopack 安装根里,重装会被清空)
        return Path.Combine(AppDataRoot.Path, "config", "state.json");
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var dto = JsonSerializer.Deserialize(File.ReadAllText(_path), AppStateJsonContext.Default.AppStateFile);
            if (dto is null) return;
            IsNetEaseGroupExpanded = dto.NetEaseGroupExpanded ?? true;
            IsQqGroupExpanded = dto.QqGroupExpanded ?? true;
            ArtistAlbumsListMode = dto.ArtistAlbumsListMode;
            HomeCreatedPlaylistsListMode = dto.HomeCreatedPlaylistsListMode;
            HomeCollectedPlaylistsListMode = dto.HomeCollectedPlaylistsListMode;
            PreferredSearchSource = dto.PreferredSearchSource is >= (int)SearchSourceMode.Combined
                and <= (int)SearchSourceMode.QQ
                ? (SearchSourceMode)dto.PreferredSearchSource.Value
                : SearchSourceMode.Combined;
            WindowWidth = dto.WindowWidth;
            WindowHeight = dto.WindowHeight;
            WindowX = dto.WindowX;
            WindowY = dto.WindowY;
            WindowMaximized = dto.WindowMaximized ?? false;
            MinimizeToTrayOnClose = dto.MinimizeToTrayOnClose;
            Theme = string.IsNullOrEmpty(dto.Theme) ? "System" : dto.Theme;
            Language = string.IsNullOrEmpty(dto.Language) ? "System" : dto.Language;
            // 只有显式写着 Performance 才是最佳性能;其余(含老配置里的 "Balanced"、空值、
            // 将来冒出来的未知值)一律归到 Quality —— 认不出来的档位宁可多留观感,不要偷偷降画质。
            PerformanceMode = string.Equals(dto.PerformanceMode, "Performance", StringComparison.OrdinalIgnoreCase)
                ? "Performance"
                : "Quality";
            DynamicBackground = dto.DynamicBackground ?? true;
            DesktopLyricEnabled = dto.DesktopLyricEnabled;
            // 歌词关着的时候穿透没有意义(窗口不存在),旧配置里的残留一律归一为 false。
            DesktopLyricClickThrough = dto.DesktopLyricEnabled && dto.DesktopLyricClickThrough == true;
            DesktopLyricBackgroundOpacity = dto.DesktopLyricBackgroundOpacity is >= 0 and <= 1 ? dto.DesktopLyricBackgroundOpacity.Value : 0.85;
            DesktopLyricShowTranslation = dto.DesktopLyricShowTranslation ?? true;
            DesktopLyricHideWhenPaused = dto.DesktopLyricHideWhenPaused ?? false;
            DesktopLyricFontSize = dto.DesktopLyricFontSize is >= 16 and <= 48 ? dto.DesktopLyricFontSize.Value : 28;
            DesktopLyricX = dto.DesktopLyricX;
            DesktopLyricY = dto.DesktopLyricY;
            AndroidLyricX = dto.AndroidLyricX;
            AndroidLyricY = dto.AndroidLyricY;
            CompatibilityVisual = dto.CompatibilityVisual ?? true;
            AudioQuality = AudioQualityMapper.NormalizeIndex(dto.AudioQuality ?? 3);
            LegacyPlaybackControl = dto.LegacyPlaybackControl ?? false;
            SwapPlayAndLikeOnRows = dto.SwapPlayAndLikeOnRows ?? false;
            PreferredCombinedLikeSource = dto.PreferredCombinedLikeSource is (int)MusicSource.NetEase
                or (int)MusicSource.QQ
                ? (MusicSource)dto.PreferredCombinedLikeSource.Value
                : null;
            Crossfade = dto.Crossfade ?? false;
            CrossfadeSeconds = dto.CrossfadeSeconds ?? 4;
            AudioOutputDeviceId = NormalizeDeviceText(dto.AudioOutputDeviceId);
            AudioOutputDeviceName = NormalizeDeviceText(dto.AudioOutputDeviceName);
            PlaybackMode = dto.PlaybackMode is >= 0 and <= 2 ? dto.PlaybackMode.Value : 0;
            Volume = Math.Clamp(dto.Volume ?? 80, 0, 100);
            MusicCacheMaximumSizeMb = MusicCacheService.NormalizeMaximumSizeMb(
                dto.MusicCacheMaximumSizeMb ?? MusicCacheService.DefaultMaximumSizeMb);

            _hotkeys.Clear();
            foreach (var pair in dto.Hotkeys ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                _hotkeys[pair.Key] = pair.Value ?? "";
            }

            // 全局标记只在"动作仍存在且真的有绑定"时保留:旧配置里残留的 Id 或已清空的绑定
            // 都不能变成一条永远注册不上的死记录。
            _globalHotkeys.Clear();
            foreach (var id in dto.GlobalHotkeys ?? [])
            {
                if (string.IsNullOrEmpty(id) || HotkeyCatalog.Find(id) is null) continue;
                if (ResolveHotkey(id).IsEmpty) continue;
                _globalHotkeys.Add(id);
            }

            AggregatePlaylists.Clear();
            foreach (var f in dto.AggregatePlaylists ?? new List<AggregatePlaylistFile>())
            {
                // 有效成员 = 平台歌单(PlaylistId>0)或本地音乐歌单(LocalPlaylistId 非空,
                // 其 PlaylistId 恒为 0)—— ⚠ 漏掉本地成员会在重开软件后静默丢失
                var members = (f.Members ?? new List<AggregateMemberFile>())
                    .Where(m => m.PlaylistId is > 0
                        || (m.Source == (int)MusicSource.Local
                            && !string.IsNullOrEmpty(m.LocalPlaylistId)))
                    .Select(m => new AggregatePlaylistMember
                    {
                        Source = (MusicSource)(m.Source ?? 0),
                        PlaylistId = m.PlaylistId ?? 0,
                        LocalPlaylistId = m.LocalPlaylistId,
                        PlaylistName = m.PlaylistName ?? "",
                    })
                    .ToList();
                if (members.Count == 0) continue; // 无有效成员的旧数据丢弃
                AggregatePlaylists.Add(new AggregatePlaylist
                {
                    Id = string.IsNullOrEmpty(f.Id) ? System.Guid.NewGuid().ToString("N") : f.Id,
                    Name = string.IsNullOrEmpty(f.Name) ? "聚合歌单" : f.Name,
                    SourceOrder = (AggregateSourceOrder)(f.SourceOrder ?? 0),
                    CustomCover = f.CustomCover,
                    Members = members,
                });
            }

            LocalPlaylists.Clear();
            foreach (var f in dto.LocalPlaylists ?? new List<LocalPlaylistFile>())
            {
                LocalPlaylists.Add(new LocalPlaylist
                {
                    Id = string.IsNullOrEmpty(f.Id) ? System.Guid.NewGuid().ToString("N") : f.Id,
                    Name = string.IsNullOrEmpty(f.Name) ? "本地音乐歌单" : f.Name,
                    CustomCover = f.CustomCover,
                    Tracks = f.Tracks ?? new List<string>(),
                });
            }

            SearchHistory.Clear();
            foreach (var w in dto.SearchHistory ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(w))
                    SearchHistory.Add(w.Trim());

            _recentSongs.Clear();
            foreach (var file in dto.RecentSongs ?? new List<RecentSongFile>())
            {
                var song = FromRecentFile(file);
                if (song is null || _recentSongs.Any(existing => SameSong(existing.Song, song))) continue;
                _recentSongs.Add(new RecentPlaybackEntry(song, Math.Max(1, file.PlayCount)));
                if (_recentSongs.Count == MaximumRecentSongCount) break;
            }
            // OrderByDescending 是稳定排序：同次数继续沿用文件中的最近播放顺序。
            var rankedRecentSongs = _recentSongs.OrderByDescending(static entry => entry.PlayCount).ToArray();
            _recentSongs.Clear();
            _recentSongs.AddRange(rankedRecentSongs);
        }
        catch
        {
            // 文件损坏不致命,按默认值
        }
    }

    /// <summary>立即保存完整状态，并取消尚未执行的延迟保存。</summary>
    public void Save()
    {
        StopDeferredSaveTimer();
        _hasPendingSave = true;
        try
        {
            var dto = new AppStateFile
            {
                NetEaseGroupExpanded = IsNetEaseGroupExpanded,
                QqGroupExpanded = IsQqGroupExpanded,
                LocalPlaylists = LocalPlaylists
                    .Select(l => new LocalPlaylistFile
                    {
                        Id = l.Id,
                        Name = l.Name,
                        CustomCover = l.CustomCover,
                        Tracks = l.Tracks,
                    })
                    .ToList(),
                AggregatePlaylists = AggregatePlaylists
                    .Select(a => new AggregatePlaylistFile
                    {
                        Id = a.Id,
                        Name = a.Name,
                        SourceOrder = (int)a.SourceOrder,
                        CustomCover = a.CustomCover,
                        Members = a.Members
                            .Select(m => new AggregateMemberFile
                            {
                                Source = (int)m.Source,
                                PlaylistId = m.PlaylistId,
                                LocalPlaylistId = m.LocalPlaylistId,
                                PlaylistName = m.PlaylistName,
                            })
                            .ToList(),
                    })
                    .ToList(),
                SearchHistory = SearchHistory.ToList(),
                PreferredSearchSource = (int)PreferredSearchSource,
                ArtistAlbumsListMode = ArtistAlbumsListMode,
                HomeCreatedPlaylistsListMode = HomeCreatedPlaylistsListMode,
                HomeCollectedPlaylistsListMode = HomeCollectedPlaylistsListMode,
                RecentSongs = _recentSongs.Select(ToRecentFile).ToList(),
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowX = WindowX,
                WindowY = WindowY,
                WindowMaximized = WindowMaximized,
                MinimizeToTrayOnClose = MinimizeToTrayOnClose,
                Theme = Theme,
                Language = Language,
                PerformanceMode = PerformanceMode,
                DynamicBackground = DynamicBackground,
                DesktopLyricEnabled = DesktopLyricEnabled,
                DesktopLyricClickThrough = DesktopLyricClickThrough,
                DesktopLyricBackgroundOpacity = DesktopLyricBackgroundOpacity,
                DesktopLyricShowTranslation = DesktopLyricShowTranslation,
                DesktopLyricHideWhenPaused = DesktopLyricHideWhenPaused,
                DesktopLyricFontSize = DesktopLyricFontSize,
                DesktopLyricX = DesktopLyricX,
                DesktopLyricY = DesktopLyricY,
                AndroidLyricX = AndroidLyricX,
                AndroidLyricY = AndroidLyricY,
                CompatibilityVisual = CompatibilityVisual,
                AudioQuality = AudioQuality,
                LegacyPlaybackControl = LegacyPlaybackControl,
                SwapPlayAndLikeOnRows = SwapPlayAndLikeOnRows,
                PreferredCombinedLikeSource = PreferredCombinedLikeSource is { } likeSource
                    ? (int)likeSource
                    : null,
                Crossfade = Crossfade,
                CrossfadeSeconds = CrossfadeSeconds,
                AudioOutputDeviceId = AudioOutputDeviceId,
                AudioOutputDeviceName = AudioOutputDeviceName,
                PlaybackMode = PlaybackMode,
                Volume = Volume,
                MusicCacheMaximumSizeMb = MusicCacheMaximumSizeMb,
                Hotkeys = _hotkeys.Count == 0
                    ? null
                    : new Dictionary<string, string>(_hotkeys, StringComparer.Ordinal),
                GlobalHotkeys = _globalHotkeys.Count == 0 ? null : _globalHotkeys.ToArray(),
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, AppStateJsonContext.Default.AppStateFile));
            File.Move(tmp, _path, overwrite: true);
            _hasPendingSave = false;
        }
        catch
        {
            // 写失败不致命；保留 dirty 标记，生命周期 Flush 或下次 Save 会重试。
        }
    }

    /// <summary>高频连续值使用的合并保存：每次调用都把保存推迟 400ms，停止变化后才落盘。
    /// 调用方均来自 Avalonia UI 线程，计时器也在 UI Dispatcher 的后台优先级执行。</summary>
    public void ScheduleSave()
    {
        _hasPendingSave = true;
        _deferredSaveTimer ??= CreateDeferredSaveTimer();
        _deferredSaveTimer.Stop();
        _deferredSaveTimer.Start();
    }

    /// <summary>应用退出或进入后台时同步写完尚未落盘的延迟状态。</summary>
    public void Flush()
    {
        StopDeferredSaveTimer();
        if (_hasPendingSave)
            Save();
    }

    private DispatcherTimer CreateDeferredSaveTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = DeferredSaveDelay,
        };
        timer.Tick += (_, _) => Save();
        return timer;
    }

    private void StopDeferredSaveTimer() => _deferredSaveTimer?.Stop();

    /// <summary>设备字段的空串一律归一为 null(= 未选择 / 跟随系统默认设备)。</summary>
    private static string? NormalizeDeviceText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>记录一次真正开始的播放，并按累计次数降序、同次数最近优先排列。</summary>
    public void RecordRecentSong(Song song)
    {
        var stored = CloneSong(song);
        var existingIndex = _recentSongs.FindIndex(candidate => SameSong(candidate.Song, stored));
        var playCount = 1L;
        if (existingIndex >= 0)
        {
            var existingCount = _recentSongs[existingIndex].PlayCount;
            playCount = existingCount == long.MaxValue ? long.MaxValue : existingCount + 1;
            _recentSongs.RemoveAt(existingIndex);
        }

        // 插在第一个“次数小于或等于当前值”的项之前，同次数下刚播放的优先。
        var insertIndex = _recentSongs.FindIndex(candidate => candidate.PlayCount <= playCount);
        var entry = new RecentPlaybackEntry(stored, playCount);
        if (insertIndex < 0) _recentSongs.Add(entry);
        else _recentSongs.Insert(insertIndex, entry);
        if (_recentSongs.Count > MaximumRecentSongCount)
            _recentSongs.RemoveRange(MaximumRecentSongCount, _recentSongs.Count - MaximumRecentSongCount);
        ScheduleSave();
        RecentSongsChanged?.Invoke();
    }

    /// <summary>清空最近播放并立即落盘。</summary>
    public void ClearRecentSongs()
    {
        if (_recentSongs.Count == 0) return;
        _recentSongs.Clear();
        Save();
        RecentSongsChanged?.Invoke();
    }

    private static bool SameSong(Song left, Song right)
    {
        if (left.Source != right.Source) return false;
        if (left.Source == MusicSource.QQ
            && !string.IsNullOrEmpty(left.Mid)
            && !string.IsNullOrEmpty(right.Mid))
            return left.Mid == right.Mid;
        return left.Id != 0 && left.Id == right.Id;
    }

    private static Song CloneSong(Song song) => new()
    {
        Id = song.Id,
        Source = song.Source,
        Mid = song.Mid,
        Name = song.Name,
        Artist = song.Artist,
        Album = song.Album,
        CoverUrl = song.CoverUrl,
        DurationMs = song.DurationMs,
        Fee = song.Fee,
        IsPurchased = song.IsPurchased,
        ArtistIds = song.ArtistIds.ToArray(),
        ArtistNames = song.ArtistNames.ToArray(),
        ArtistMids = song.ArtistMids.ToArray(),
        AlbumId = song.AlbumId,
        AlbumMid = song.AlbumMid,
        LocalFilePath = song.LocalFilePath,
    };

    private static RecentSongFile ToRecentFile(RecentPlaybackEntry entry) => new()
    {
        PlayCount = entry.PlayCount,
        Id = entry.Song.Id,
        Source = (int)entry.Song.Source,
        Mid = entry.Song.Mid,
        Name = entry.Song.Name,
        Artist = entry.Song.Artist,
        Album = entry.Song.Album,
        CoverUrl = entry.Song.CoverUrl,
        DurationMs = entry.Song.DurationMs,
        Fee = entry.Song.Fee,
        ArtistIds = entry.Song.ArtistIds.ToList(),
        ArtistNames = entry.Song.ArtistNames.ToList(),
        ArtistMids = entry.Song.ArtistMids.ToList(),
        AlbumId = entry.Song.AlbumId,
        AlbumMid = entry.Song.AlbumMid,
        LocalPath = entry.Song.LocalFilePath,
    };

    private sealed record RecentPlaybackEntry(Song Song, long PlayCount);

    private static Song? FromRecentFile(RecentSongFile file)
    {
        if (!Enum.IsDefined(typeof(MusicSource), file.Source)) return null;
        var source = (MusicSource)file.Source;
        if (string.IsNullOrWhiteSpace(file.Name)) return null;
        if (file.Id == 0 && string.IsNullOrWhiteSpace(file.Mid)) return null;
        // 本地歌曲以文件路径为唯一事实来源;路径缺失(旧版本写入)则放弃恢复该条目。
        if (source == MusicSource.Local && string.IsNullOrWhiteSpace(file.LocalPath)) return null;
        return new Song
        {
            Id = file.Id,
            Source = source,
            Mid = file.Mid ?? "",
            Name = file.Name,
            Artist = file.Artist ?? "",
            Album = file.Album ?? "",
            CoverUrl = file.CoverUrl ?? "",
            DurationMs = Math.Max(0, file.DurationMs),
            Fee = file.Fee,
            ArtistIds = file.ArtistIds?.ToArray() ?? Array.Empty<long>(),
            ArtistNames = file.ArtistNames?.ToArray() ?? Array.Empty<string>(),
            ArtistMids = file.ArtistMids?.ToArray() ?? Array.Empty<string>(),
            AlbumId = file.AlbumId,
            AlbumMid = file.AlbumMid ?? "",
            LocalFilePath = source == MusicSource.Local ? file.LocalPath : null,
        };
    }
}
