using System.Text.Json;
using ALyricEase.Models.Dtos;

namespace ALyricEase.Services;

/// <summary>应用界面状态持久化:侧栏歌单分组折叠状态 + 主窗口大小/位置/最大化。
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
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowX = WindowX,
                WindowY = WindowY,
                WindowMaximized = WindowMaximized,
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
