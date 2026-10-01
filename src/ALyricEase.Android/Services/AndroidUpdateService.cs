using ALyricEase.Services.Update;
using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidX.Core.Content;
using AApplication = global::Android.App.Application;
using AUri = Android.Net.Uri;

namespace ALyricEase.Services;

/// <summary>Android 实现的应用自更新(不走 Velopack —— 它没有 Android 运行形态)。
/// 链路:GitHub releases/latest 对比 PackageInfo.VersionName → 选设备 ABI 匹配的 .apk 资产
/// → HttpClient 流式下载到 CacheDir/update(FileProvider 暴露范围)→ 调系统安装器
/// (Android 8.0+ 先跳"安装未知应用"授权页,授权回来再点一次「立即安装」)。
/// 注意:接口名沿用 ApplyUpdatesAndRestart,但安卓语义是"调起安装器",不重启进程 ——
/// 安装由用户在系统安装器里确认,装完系统自行决定是否重启应用。</summary>
public sealed class AndroidUpdateService : IAppUpdateService
{
    private const string FileProviderAuthoritySuffix = ".fileprovider";
    /// <summary>下载完成后写入 update 目录的版本标记文件(记录的是包对应的版本号)。</summary>
    private const string VersionStampFileName = "downloaded-version.txt";

    private GitHubReleaseAsset? _pendingApk;
    private string? _pendingVersion;
    private string? _downloadedApkPath;

    public AndroidUpdateService()
    {
        // 服务在启动解析 SettingsViewModel 时构造:借这个时机清掉"装完没用掉"的安装包
        CleanupStaleDownloads();
    }

    public bool IsSupported => true;

    public string? CurrentVersion
    {
        get
        {
            try
            {
                var context = AApplication.Context;
                return context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.VersionName;
            }
            catch
            {
                return null;
            }
        }
    }

    public DateTime? InstallDate
    {
        get
        {
            try
            {
                var context = AApplication.Context;
                var info = context.PackageManager?.GetPackageInfo(context.PackageName!, 0);
                return info is null
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(info.FirstInstallTime).LocalDateTime;
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<AppUpdateInfo?> CheckForUpdatesAsync()
    {
        var release = await GitHubReleaseFetcher.GetLatestAsync().ConfigureAwait(false);
        if (release is null) return null;

        // 版本比较:tag(versionName 形如 1.0 / 1.2.3,允许 v 前缀)按数值段比较,远端不高直接算无更新
        var local = CurrentVersion;
        if (local is null || CompareVersions(release.Tag, local) <= 0) return null;

        // release 里没有可安装的 APK 资产 = 对安卓不可更新,视作无更新
        var apk = PickApkAsset(release);
        if (apk is null) return null;

        _pendingApk = apk;
        _pendingVersion = release.Tag;
        _downloadedApkPath = null;
        return new AppUpdateInfo(release.Tag, GitHubReleaseFetcher.CleanMarkdown(release.Body) ?? "");
    }

    public async Task DownloadUpdatesAsync(Action<int> progress)
    {
        var apk = _pendingApk ?? throw new InvalidOperationException("尚未检查到可用更新");
        var dir = GetUpdateDir() ?? throw new InvalidOperationException("无法定位下载目录");
        dir.Mkdirs();
        foreach (var stale in dir.ListFiles() ?? [])
            stale.Delete(); // 清掉上一轮没装完的旧包(含旧版本标记),防 CacheDir 被堆积撑爆

        var dest = new Java.IO.File(dir, apk.Name);
        using var client = new HttpClient();
        using var resp = await client.GetAsync(apk.Url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = apk.SizeBytes > 0 ? apk.SizeBytes : resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = File.Create(dest.AbsolutePath);
        var buffer = new byte[81920];
        long read = 0;
        var lastReported = -1;
        int n;
        while ((n = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
            read += n;
            if (total > 0)
            {
                var percent = (int)(read * 100 / total);
                if (percent != lastReported)
                {
                    lastReported = percent;
                    progress(percent);
                }
            }
        }

        // 记下这个包对应的版本:启动清理靠它判断"当前已 >= 包版本 = 已装过,可删"
        await File.WriteAllTextAsync(
            Path.Combine(dir.AbsolutePath, VersionStampFileName),
            _pendingVersion ?? "").ConfigureAwait(false);

        progress(100);
        _downloadedApkPath = dest.AbsolutePath;
    }

    public void ApplyUpdatesAndRestart()
    {
        if (_downloadedApkPath is null || !File.Exists(_downloadedApkPath))
            throw new InvalidOperationException("尚未下载更新安装包");

        var context = AApplication.Context;
        // Android 8.0+ 装非市场 APK 需要"安装未知应用"授权;没授权先跳系统设置页,
        // 返回后 _downloadedApkPath 仍在,再点一次「立即安装」即可
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O &&
            context.PackageManager!.CanRequestPackageInstalls() == false)
        {
            var settings = new Intent(Settings.ActionManageUnknownAppSources,
                AUri.Parse("package:" + context.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(settings);
            return;
        }

        var apk = new Java.IO.File(_downloadedApkPath);
        var uri = FileProvider.GetUriForFile(context,
            context.PackageName + FileProviderAuthoritySuffix, apk);
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    /// <summary>下载目录:CacheDir/update(与清单 FileProvider 暴露的 cache-path 对应)。</summary>
    private static Java.IO.File? GetUpdateDir()
    {
        var cache = AApplication.Context.CacheDir?.AbsolutePath;
        return cache is null ? null : new Java.IO.File(Path.Combine(cache, "update"));
    }

    /// <summary>装完即删:系统安装器不回调应用,无法在安装成功那一刻感知,退而求其次在
    /// 启动时清理 —— 下载完成时记下包版本,启动时当前版本已 ≥ 包版本(= 装过了)就整目录清空。
    /// 用户下载了却没装的包(当前版本 < 包版本)保留,回来还能继续点「立即安装」。</summary>
    private void CleanupStaleDownloads()
    {
        try
        {
            var dir = GetUpdateDir();
            if (dir is null || !dir.Exists()) return;

            var stamp = new Java.IO.File(dir, VersionStampFileName);
            var recorded = stamp.Exists() ? File.ReadAllText(stamp.AbsolutePath).Trim() : null;
            var current = CurrentVersion;

            // 没有标记(半途而废的残留)或已装到同/更高版本 ⇒ 一律清空;只有"下载了但还没装"才保留
            if (recorded is null || current is null || CompareVersions(current, recorded) >= 0)
                foreach (var f in dir.ListFiles() ?? [])
                    f.Delete();
        }
        catch
        {
            // 清理失败只影响几十 MB 的缓存,不值得打断启动
        }
    }

    /// <summary>挑可安装的 APK 资产:发布是 per-ABI 分包,优先命中设备 ABI 的那个;兜底取第一个 .apk。</summary>
    private static GitHubReleaseAsset? PickApkAsset(GitHubRelease release)
    {
        GitHubReleaseAsset? fallback = null;
        foreach (var asset in release.Assets)
        {
            if (!asset.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) continue;
            fallback ??= asset;
            foreach (var abi in Build.SupportedAbis)
                if (asset.Name.Contains(abi, StringComparison.OrdinalIgnoreCase))
                    return asset;
        }
        return fallback;
    }

    private static int CompareVersions(string a, string b)
    {
        static int[] Parse(string v) => v.TrimStart('v', 'V').Split('.', '-', '+')
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var pa = Parse(a);
        var pb = Parse(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
