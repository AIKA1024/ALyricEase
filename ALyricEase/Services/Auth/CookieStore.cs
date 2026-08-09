using System.Text.Json;

namespace ALyricEase.Services.Auth;

/// <summary>MUSIC_U(登录)与匿名 cookie 的本地持久化。
/// 存 %LocalAppData%\ALyricEase\config\cookie.json。私有用户目录,明文存储;
/// 日志输出 cookie 一律打码,不写完整值。</summary>
public sealed class CookieStore
{
    private readonly string _path;

    /// <summary>登录用户的 MUSIC_U 值(仅值,不含 cookie 名)。</summary>
    public string? MusicU { get; set; }

    /// <summary>匿名认证 cookie MUSIC_A 的值。</summary>
    public string? AnonymousMusicA { get; set; }

    public DateTime AnonymousExpiresUtc { get; set; } = DateTime.MinValue;

    public CookieStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ALyricEase");
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
            var dto = JsonSerializer.Deserialize<CookieFile>(File.ReadAllText(_path));
            if (dto is null) return;
            MusicU = dto.MusicU;
            AnonymousMusicA = dto.AnonymousMusicA;
            AnonymousExpiresUtc = dto.AnonymousExpiresUtc;
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
            };
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 写失败不致命,下次再存
        }
    }

    private sealed class CookieFile
    {
        public string? MusicU { get; set; }

        public string? AnonymousMusicA { get; set; }

        public DateTime AnonymousExpiresUtc { get; set; }
    }
}
