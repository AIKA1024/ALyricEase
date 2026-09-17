namespace ALyricEase.Services.Audio;

/// <summary>音频输出设备条目(设置页下拉用)。
///
/// Id 是**后端自己的**标识(Windows = WinRT 设备 Id 字符串,Android = AudioDeviceInfo.Id),
/// 只保证在同一平台同一会话内可比对:Windows 上设备重插/换 USB 口会换新 Id,
/// Android 上 Id 甚至可能跨重启变化 ⇒ 落盘只存 Id 不够,还得存展示名做二次匹配。
/// 相关回退逻辑见 SettingsViewModel.RestoreAudioDeviceAsync。</summary>
/// <param name="Id">后端稳定标识(仅同平台内可比对)。</param>
/// <param name="Name">展示名(如 "扬声器 (Realtek(R) Audio)")。</param>
/// <param name="IsDefault">是否是当前系统默认输出设备(仅用于标注,不参与选择)。</param>
public sealed record AudioOutputDevice(string Id, string Name, bool IsDefault);
