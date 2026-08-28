using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;

namespace ALyricEase.Services;

/// <summary>应用界面状态持久化:侧栏歌单分组折叠状态 + 聚合歌单 + 主窗口大小/位置/最大化。
/// Windows 存 %LocalAppData%\ALyricEase\config\state.json,Android 存应用私有
/// FilesDir\ALyricEase\config\state.json(折叠状态同样有意义;窗口字段仅桌面端读写)。
/// 写入走 tmp+Move 原子替换;文件损坏按全部默认值处理。</summary>
public sealed class AppStateStore
{
    private readonly string _path;

    /// <summary>网易云歌单分组是否展开。</summary>
    public bool IsNetEaseGroupExpanded { get; set; } = true;

    /// <summary>QQ音乐歌单分组是否展开。</summary>
    public bool IsQqGroupExpanded { get; set; } = true;

    /// <summary>用户创建的聚合歌单(持久化;顺序即侧栏显示顺序)。</summary>
    public List<AggregatePlaylist> AggregatePlaylists { get; } = new();

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

    // ---- 设置页偏好(SettingsViewModel 绑定;改任意项即 Save) ----

    /// <summary>主题:System/Light/Dark。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>界面语言:System/zh-CN(当前仅存储偏好)。</summary>
    public string Language { get; set; } = "System";

    /// <summary>性能与体验:Balanced/Quality(当前仅存储偏好)。</summary>
    public string PerformanceMode { get; set; } = "Balanced";

    /// <summary>播放详情页动态背景效果。</summary>
    public bool DynamicBackground { get; set; } = true;

    /// <summary>兼容的视觉效果。</summary>
    public bool CompatibilityVisual { get; set; } = true;

    /// <summary>音频质量档位(0=标准 1=较高 2=极高 3=无损)。</summary>
    public int AudioQuality { get; set; }

    /// <summary>传统播放控制。</summary>
    public bool LegacyPlaybackControl { get; set; }

    /// <summary>音频交叉淡化开关。</summary>
    public bool Crossfade { get; set; }

    /// <summary>交叉淡化时长(秒)。</summary>
    public double CrossfadeSeconds { get; set; } = 4;

    public AppStateStore()
    {
#if ANDROID
        var root = Path.Combine(
            global::Android.App.Application.Context.FilesDir!.AbsolutePath, "ALyricEase");
#else
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALyricEase");
#endif
        Directory.CreateDirectory(Path.Combine(root, "config"));
        _path = Path.Combine(root, "config", "state.json");
        Load();
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
            WindowWidth = dto.WindowWidth;
            WindowHeight = dto.WindowHeight;
            WindowX = dto.WindowX;
            WindowY = dto.WindowY;
            WindowMaximized = dto.WindowMaximized ?? false;
            Theme = string.IsNullOrEmpty(dto.Theme) ? "System" : dto.Theme;
            Language = string.IsNullOrEmpty(dto.Language) ? "System" : dto.Language;
            PerformanceMode = string.IsNullOrEmpty(dto.PerformanceMode) ? "Balanced" : dto.PerformanceMode;
            DynamicBackground = dto.DynamicBackground ?? true;
            CompatibilityVisual = dto.CompatibilityVisual ?? true;
            AudioQuality = dto.AudioQuality ?? 0;
            LegacyPlaybackControl = dto.LegacyPlaybackControl ?? false;
            Crossfade = dto.Crossfade ?? false;
            CrossfadeSeconds = dto.CrossfadeSeconds ?? 4;

            AggregatePlaylists.Clear();
            foreach (var f in dto.AggregatePlaylists ?? new List<AggregatePlaylistFile>())
            {
                var members = (f.Members ?? new List<AggregateMemberFile>())
                    .Where(m => m.PlaylistId is > 0)
                    .Select(m => new AggregatePlaylistMember
                    {
                        Source = (MusicSource)(m.Source ?? 0),
                        PlaylistId = m.PlaylistId ?? 0,
                        PlaylistName = m.PlaylistName ?? "",
                    })
                    .ToList();
                if (members.Count == 0) continue; // 无有效成员的旧数据丢弃
                AggregatePlaylists.Add(new AggregatePlaylist
                {
                    Id = string.IsNullOrEmpty(f.Id) ? System.Guid.NewGuid().ToString("N") : f.Id,
                    Name = string.IsNullOrEmpty(f.Name) ? "聚合歌单" : f.Name,
                    SourceOrder = (AggregateSourceOrder)(f.SourceOrder ?? 0),
                    Members = members,
                });
            }
        }
        catch
        {
            // 文件损坏不致命,按默认值
        }
    }

    public void Save()
    {
        try
        {
            var dto = new AppStateFile
            {
                NetEaseGroupExpanded = IsNetEaseGroupExpanded,
                QqGroupExpanded = IsQqGroupExpanded,
                AggregatePlaylists = AggregatePlaylists
                    .Select(a => new AggregatePlaylistFile
                    {
                        Id = a.Id,
                        Name = a.Name,
                        SourceOrder = (int)a.SourceOrder,
                        Members = a.Members
                            .Select(m => new AggregateMemberFile
                            {
                                Source = (int)m.Source,
                                PlaylistId = m.PlaylistId,
                                PlaylistName = m.PlaylistName,
                            })
                            .ToList(),
                    })
                    .ToList(),
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowX = WindowX,
                WindowY = WindowY,
                WindowMaximized = WindowMaximized,
                Theme = Theme,
                Language = Language,
                PerformanceMode = PerformanceMode,
                DynamicBackground = DynamicBackground,
                CompatibilityVisual = CompatibilityVisual,
                AudioQuality = AudioQuality,
                LegacyPlaybackControl = LegacyPlaybackControl,
                Crossfade = Crossfade,
                CrossfadeSeconds = CrossfadeSeconds,
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, AppStateJsonContext.Default.AppStateFile));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 写失败不致命,下次再存
        }
    }
}
