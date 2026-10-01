namespace ALyricEase.Services.Update;

/// <summary>一次可安装更新的描述:目标版本号 + 更新说明(release notes 原文)。</summary>
public sealed record AppUpdateInfo(string Version, string ReleaseNotes);

/// <summary>应用自更新能力抽象。Windows 宿主用 Velopack(GitHub Releases 源)实现;
/// 不支持自更新的宿主(Android/无头探针)注册 <see cref="NullAppUpdateService"/>。</summary>
public interface IAppUpdateService
{
    /// <summary>当前宿主是否具备检查/下载/应用更新的条件(Velopack 只认 vpk 安装后的目录结构)。</summary>
    bool IsSupported { get; }

    /// <summary>当前安装的版本号(展示用);未安装/未知返回 null,由调用方回退到程序集版本。</summary>
    string? CurrentVersion { get; }

    /// <summary>安装日期(本地时间);未知返回 null,展示为"-"。</summary>
    DateTime? InstallDate { get; }

    /// <summary>检查更新。有更新返回描述(并把下载会话暂存到服务内部);无更新/不支持返回 null。</summary>
    Task<AppUpdateInfo?> CheckForUpdatesAsync();

    /// <summary>下载先前 <see cref="CheckForUpdatesAsync"/> 发现的更新。
    /// <paramref name="progress"/> 以 0-100 整数回报,回调可能来自非 UI 线程。</summary>
    Task DownloadUpdatesAsync(Action<int> progress);

    /// <summary>应用已下载的更新并重启应用(此调用之后进程会退出,不返回)。</summary>
    void ApplyUpdatesAndRestart();
}

/// <summary>不支持自更新的宿主(Android/无头探针)的空实现:能力一律关闭,
/// 下载/应用一调即抛(界面入口本来就该被 <see cref="IsSupported"/> 挡住,这里是兜底)。</summary>
public sealed class NullAppUpdateService : IAppUpdateService
{
    public bool IsSupported => false;

    public string? CurrentVersion => null;

    public DateTime? InstallDate => null;

    public Task<AppUpdateInfo?> CheckForUpdatesAsync() => Task.FromResult<AppUpdateInfo?>(null);

    public Task DownloadUpdatesAsync(Action<int> progress) =>
        throw new NotSupportedException("当前平台不支持应用自更新");

    public void ApplyUpdatesAndRestart() =>
        throw new NotSupportedException("当前平台不支持应用自更新");
}
