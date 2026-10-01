namespace ALyricEase.Services;

/// <summary>用户数据根目录的唯一定义点(状态/登录凭证/缓存都挂在这下面)。
/// ⚠ Windows 不能用 %LOCALAPPDATA%\ALyricEase —— 那是 Velopack 的安装根
/// (Update.exe / app-* / packages),重跑 Setup 或卸载时会被整体清空,
/// 用户数据会陪葬(2026-10-01 实测:重装一次,登录态+偏好+缓存全丢)。
/// ALyricEase.Data 与安装根同名加 .Data 后缀,目录树里成组且不冲突。
/// Android 的 LocalApplicationData 是应用私有目录,无此冲突,沿用 ALyricEase
/// 保住既有安装的数据连续性。</summary>
public static class AppDataRoot
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        OperatingSystem.IsAndroid() ? "ALyricEase" : "ALyricEase.Data");
}
