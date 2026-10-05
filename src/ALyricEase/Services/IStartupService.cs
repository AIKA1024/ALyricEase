namespace ALyricEase.Services;

/// <summary>平台登录自启动设置；以系统配置为准，不在 state.json 中重复保存。</summary>
public interface IStartupService
{
    bool IsSupported { get; }
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}
