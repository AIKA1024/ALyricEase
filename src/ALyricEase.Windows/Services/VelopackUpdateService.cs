using System.Reflection;
using ALyricEase.Services.Update;
using Velopack;
using Velopack.Sources;

namespace ALyricEase.Services;

/// <summary>Velopack 实现的应用自更新(GitHub Releases 更新源)。
/// 检查:UpdateManager.CheckForUpdatesAsync(无更新返回 null)→ 命中后暂存 UpdateInfo
/// 供下载/应用复用,并另拉 GitHub release 的说明文本(vpk 发布的 release tag = 版本号,
/// 抓取/清洗共用 GitHubReleaseFetcher);下载:DownloadUpdatesAsync 原生支持 delta 增量包,
/// 失败自动回退全量包;应用:ApplyUpdatesAndRestart 退出进程 → Update.exe 落盘 → 重启。
/// 仅 vpk 安装后的目录结构里 IsInstalled 为 true;开发目录直接跑 = 不支持(按钮随之隐藏)。
/// ⚠ 必须显式 GithubSource:UpdateManager(string) 对 URL 一律按 SimpleWebSource 处理,
/// 不会识别 github.com 仓库地址(实测 404:它会去抓 github.com/{repo}/releases.win.json)。</summary>
public sealed class VelopackUpdateService : IAppUpdateService
{
    private const string RepoUrl = "https://github.com/AIKA1024/ALyricEase";

    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
        }
        catch
        {
            _manager = null; // 定位器异常等极端情况:视作不支持
        }
    }

    public bool IsSupported => _manager?.IsInstalled == true;

    public string? CurrentVersion
    {
        get
        {
            var v = _manager?.CurrentVersion?.ToNormalizedString();
            if (v is not null) return v;
            // 未打包(开发运行)回退程序集版本
            return Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion is { } iv ? iv : null;
        }
    }

    public DateTime? InstallDate
    {
        get
        {
            try
            {
                // vpk 安装/更新时把当前版本目录整个写入,exe 的创建时间即本次安装时间
                var path = Environment.ProcessPath;
                return path is null ? null : File.GetCreationTime(path);
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<AppUpdateInfo?> CheckForUpdatesAsync()
    {
        var mgr = _manager;
        if (mgr is null || !mgr.IsInstalled) return null;

        var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;

        _pending = info;
        var version = info.TargetFullRelease.Version.ToNormalizedString();
        var notes = await GitHubReleaseFetcher.GetByTagAsync(version).ConfigureAwait(false);
        return new AppUpdateInfo(version, GitHubReleaseFetcher.CleanMarkdown(notes?.Body) ?? "");
    }

    public async Task DownloadUpdatesAsync(Action<int> progress)
    {
        var mgr = _manager;
        var info = _pending;
        if (mgr is null || info is null)
            throw new InvalidOperationException("尚未检查到可用更新");
        // 原生增量更新:feed 里有 delta 包就用 delta 重建,失败自动回退全量包
        await mgr.DownloadUpdatesAsync(info, p => progress(p)).ConfigureAwait(false);
    }

    public void ApplyUpdatesAndRestart()
    {
        var mgr = _manager;
        if (mgr is null || !mgr.IsInstalled)
            throw new InvalidOperationException("当前应用不是 vpk 安装版,无法应用更新");
        mgr.ApplyUpdatesAndRestart(null);
    }
}
