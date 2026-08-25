using System.Text.Json;
using ALyricEase.Models.Dtos;

namespace ALyricEase.Services.Auth;

/// <summary>MUSIC_U(登录)与匿名 cookie 的本地持久化。
/// Windows 存 %LocalAppData%\ALyricEase\config\cookie.json,Android 存应用私有
/// FilesDir\ALyricEase\config\cookie.json。私有用户目录,明文存储;
/// 日志输出 cookie 一律打码,不写完整值。</summary>
public sealed class CookieStore
{
    private readonly string _path;

    /// <summary>登录用户的 MUSIC_U 值(仅值,不含 cookie 名)。</summary>
    public string? MusicU { get; set; }

    /// <summary>匿名认证 cookie MUSIC_A 的值。</summary>
    public string? AnonymousMusicA { get; set; }

    public DateTime AnonymousExpiresUtc { get; set; } = DateTime.MinValue;

    /// <summary>QQ 音乐登录 cookie 原文(需含 uin 与 qqmusic_key,由 QQMusicApiClient 解析)。</summary>
    public string? QQCookieRaw { get; set; }

    public CookieStore()
    {
#if ANDROID
        // Android 上 GetFolderPath(LocalApplicationData) 返回空串,路径退化为不可写的
        // 相对路径,Save 静默失败 → 退出后登录态丢失。改用应用私有 FilesDir。
        var root = Path.Combine(
            global::Android.App.Application.Context.FilesDir!.AbsolutePath, "ALyricEase");
#else
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALyricEase");
#endif
        Directory.CreateDirectory(Path.Combine(root, "config"));
        _path = Path.Combine(root, "config", "cookie.json");
        Load();
    }

    /// <summary>打码显示:MUSIC_U=ab***cd,用于日志。</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(空)";
        if (value.Length <= 8) return "***";
        return $"{value[..4]}***{value[^4..]}";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var dto = JsonSerializer.Deserialize(File.ReadAllText(_path), NetEaseJsonContext.Default.CookieFile);
            if (dto is null) return;
            MusicU = dto.MusicU;
            AnonymousMusicA = dto.AnonymousMusicA;
            AnonymousExpiresUtc = dto.AnonymousExpiresUtc;
            QQCookieRaw = dto.QQCookieRaw;
        }
        catch
        {
            // 文件损坏不致命,忽略
        }
    }

    public void Save()
    {
        try
        {
            var dto = new CookieFile
            {
                MusicU = MusicU,
                AnonymousMusicA = AnonymousMusicA,
                AnonymousExpiresUtc = AnonymousExpiresUtc,
                QQCookieRaw = QQCookieRaw,
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, NetEaseJsonContext.Default.CookieFile));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 写失败不致命,下次再存
        }
    }

    internal sealed class CookieFile
    {
        public string? MusicU { get; set; }

        public string? AnonymousMusicA { get; set; }

        public DateTime AnonymousExpiresUtc { get; set; }

        public string? QQCookieRaw { get; set; }
    }
}
