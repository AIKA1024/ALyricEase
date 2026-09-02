using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ALyricEase.Models;
using ALyricEase.Models.Dtos;
using Avalonia.Threading;

namespace ALyricEase.Services;

/// <summary>应用界面状态持久化:侧栏歌单分组折叠状态 + 聚合歌单 + 主窗口大小/位置/最大化。
/// Windows 存 %LocalAppData%\ALyricEase\config\state.json,Android 存应用私有
/// FilesDir\ALyricEase\config\state.json(折叠状态同样有意义;窗口字段仅桌面端读写)。
/// 写入走 tmp+Move 原子替换;文件损坏按全部默认值处理。</summary>
public sealed class AppStateStore
{
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

    /// <summary>搜索历史(最新在前,SearchViewModel 维护与落盘)。</summary>
    public List<string> SearchHistory { get; } = new();

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

    /// <summary>统一音频质量选项(0-3；旧值 4 加载时自动归一为无损)。</summary>
    public int AudioQuality { get; set; }

    /// <summary>传统播放控制。</summary>
    public bool LegacyPlaybackControl { get; set; }

    /// <summary>音频交叉淡化开关。</summary>
    public bool Crossfade { get; set; }

    /// <summary>交叉淡化时长(秒)。</summary>
    public double CrossfadeSeconds { get; set; } = 4;

    /// <summary>播放模式(0=列表循环 1=单曲循环 2=随机播放)。</summary>
    public int PlaybackMode { get; set; }

    /// <summary>播放器音量(0-100)。</summary>
    public int Volume { get; set; } = 80;

    /// <summary>音乐、封面和歌词的统一磁盘缓存容量上限(MB)。</summary>
    public int MusicCacheMaximumSizeMb { get; set; } = MusicCacheService.DefaultMaximumSizeMb;

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
            AudioQuality = AudioQualityMapper.NormalizeIndex(dto.AudioQuality ?? 0);
            LegacyPlaybackControl = dto.LegacyPlaybackControl ?? false;
            Crossfade = dto.Crossfade ?? false;
            CrossfadeSeconds = dto.CrossfadeSeconds ?? 4;
            PlaybackMode = dto.PlaybackMode is >= 0 and <= 2 ? dto.PlaybackMode.Value : 0;
            Volume = Math.Clamp(dto.Volume ?? 80, 0, 100);
            MusicCacheMaximumSizeMb = MusicCacheService.NormalizeMaximumSizeMb(
                dto.MusicCacheMaximumSizeMb ?? MusicCacheService.DefaultMaximumSizeMb);

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

            SearchHistory.Clear();
            foreach (var w in dto.SearchHistory ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(w))
                    SearchHistory.Add(w.Trim());
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
                SearchHistory = SearchHistory.ToList(),
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
                PlaybackMode = PlaybackMode,
                Volume = Volume,
                MusicCacheMaximumSizeMb = MusicCacheMaximumSizeMb,
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
}
