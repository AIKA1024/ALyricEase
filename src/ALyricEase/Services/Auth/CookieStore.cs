using System.Text.Json;
using ALyricEase.Models.Dtos;

namespace ALyricEase.Services.Auth;

/// <summary>MUSIC_U(登录)与匿名 cookie 的本地持久化。
/// 桌面存 %LocalAppData%\ALyricEase\config\cookie.json;Android 走同一段 #else 代码,
/// 落在应用私有目录下的 .local/share/ALyricEase\config\cookie.json(见构造函数的说明)。
/// 私有用户目录,明文存储;日志输出 cookie 一律打码,不写完整值。</summary>
public sealed class CookieStore
{
    private readonly string _path;

    /// <summary>与 Cookie 同级的私有配置目录，供账号协议保存非敏感设备上下文。</summary>
    internal string ConfigDirectory => Path.GetDirectoryName(_path)!;

    /// <summary>登录用户的 MUSIC_U 值(仅值,不含 cookie 名)。</summary>
    public string? MusicU { get; set; }

    /// <summary>匿名认证 cookie MUSIC_A 的值。</summary>
    public string? AnonymousMusicA { get; set; }

    public DateTime AnonymousExpiresUtc { get; set; } = DateTime.MinValue;

    /// <summary>CSRF 令牌(__csrf cookie 的值,32 位 hex)。写接口(创建/删除歌单)服务端强校验:
    /// 缺失时返回 code 403 "illegal request!",与 UA/Referer/csrf_token 参数无关。
    /// 由带 MUSIC_U 访问站内页面时通过 Set-Cookie 下发,与登录态绑定(同一 MUSIC_U 取值稳定),
    /// 故按登录态持久化;换号时由 SetMusicUCookie 清空。写入 JSON 与其它 cookie 同文件。</summary>
    public string? Csrf { get; set; }

    /// <summary>QQ 音乐登录 cookie 原文(需含 uin 与 qqmusic_key,由 QQMusicApiClient 解析)。</summary>
    public string? QQCookieRaw { get; set; }

    public CookieStore()
    {
        // Android 上 LocalApplicationData = <应用私有 files 目录>/.local/share,
        // 登录态落盘正常;Windows 的实际目录见 AppDataRoot
        // (⚠ 不能落在 Velopack 安装根里,重装会被清空、用户被踢下线)。
        var root = AppDataRoot.Path;
        Directory.CreateDirectory(Path.Combine(root, "config"));
        _path = Path.Combine(root, "config", "cookie.json");
        Load();
    }

    /// <summary>指定文件路径的实例:探针/回归用,避免 SetMusicUCookie / ClearCookie
    /// 这类会 Save 的操作覆盖用户真实的 cookie.json(那等于把用户踢下线)。</summary>
    internal CookieStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
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
            Csrf = dto.Csrf;
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
                Csrf = Csrf,
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

        public string? Csrf { get; set; }
    }
}
