using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ALyricEase.Services.Update;

/// <summary>GitHub Release 资产(安装包)描述。</summary>
public sealed record GitHubReleaseAsset(string Name, string Url, long SizeBytes);

/// <summary>GitHub Release 概要:tag、说明原文、资产列表。</summary>
public sealed record GitHubRelease(string Tag, string Body, IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>GitHub Releases 的抓取与说明文本清洗(桌面 Velopack 与安卓自更新共用)。
/// 解析走 JsonDocument 手工取字段,不做反射反序列化 —— AOT/裁剪安全。</summary>
public static partial class GitHubReleaseFetcher
{
    private const string RepoApiBase = "https://api.github.com/repos/AIKA1024/ALyricEase/releases";

    /// <summary>取最新正式 release(releases/latest 自动忽略 prerelease)。失败返回 null。</summary>
    public static Task<GitHubRelease?> GetLatestAsync() => FetchAsync($"{RepoApiBase}/latest");

    /// <summary>按 tag 取 release(vpk 发布的 tag = 版本号)。失败返回 null。</summary>
    public static Task<GitHubRelease?> GetByTagAsync(string tag) =>
        FetchAsync($"{RepoApiBase}/tags/{Uri.EscapeDataString(tag)}");

    private static async Task<GitHubRelease?> FetchAsync(string url)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ALyricEase-UpdateCheck");
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await client.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            var assets = new List<GitHubReleaseAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() : null;
                    var dl = item.TryGetProperty("browser_download_url", out var u) &&
                             u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (name is null || dl is null) continue;
                    var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var s64) ? s64 : 0;
                    assets.Add(new GitHubReleaseAsset(name, dl, size));
                }
            }

            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            if (tag is null) return null;

            var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() : null;
            return new GitHubRelease(tag, body ?? "", assets);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 release notes 的 markdown 压成可读纯文本:去图片/链接语法,收敛多余空行。
    /// 失败/空返回 null —— 说明文本只是展示,拉不到不能挡更新。</summary>
    public static string? CleanMarkdown(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = ImageSyntax().Replace(text, "");
        text = LinkSyntax().Replace(text, "$1");
        text = HeadingSyntax().Replace(text, "$1");
        text = BoldSyntax().Replace(text, "$1");
        text = text.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageSyntax();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkSyntax();

    [GeneratedRegex(@"(?m)^\s*#{1,6}\s+(.*)$")]
    private static partial Regex HeadingSyntax();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldSyntax();
}
