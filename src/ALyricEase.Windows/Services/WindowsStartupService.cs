using Microsoft.Win32;

namespace ALyricEase.Services;

/// <summary>当前用户登录自启动，无需管理员权限。</summary>
public sealed class WindowsStartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ALyricEase";

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("无法打开系统启动项设置");
        if (enabled)
            key.SetValue(ValueName, BuildStartupCommand(), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static string BuildStartupCommand()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从 ALyricEase.exe 启动应用后设置开机自启");

        // Velopack 安装使用稳定的 Update.exe 入口，更新后仍能启动当前版本。
        var directory = Path.GetDirectoryName(executable)!;
        var updater = Path.Combine(Path.GetDirectoryName(directory) ?? directory, "Update.exe");
        if (Path.GetFileName(directory).Equals("current", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(updater))
            return $"\"{updater}\" start \"{Path.GetFileName(executable)}\"";

        return $"\"{executable}\"";
    }
}
