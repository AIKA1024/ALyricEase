using System.Text.Json.Serialization;

namespace ALyricEase.Models.Dtos;

/// <summary>应用界面状态(state.json)的 JSON 源生成上下文(NativeAOT 兼容,与 CookieFile 同模式)。
/// 可空字段区分"从未记录过"与已记录值;加载端对 null 回退默认。</summary>
[JsonSerializable(typeof(AppStateFile))]
internal sealed partial class AppStateJsonContext : JsonSerializerContext
{
}

/// <summary>state.json 的落盘形态(字段全部可空)。</summary>
public sealed class AppStateFile
{
    public bool? NetEaseGroupExpanded { get; set; }

    public bool? QqGroupExpanded { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public bool? WindowMaximized { get; set; }
}
