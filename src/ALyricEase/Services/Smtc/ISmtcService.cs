namespace ALyricEase.Services.Smtc;

/// <summary>SMTC(系统媒体传输控制)抽象:PlayerViewModel 只依赖它,不直接碰 WinRT。
/// Windows 用 SmtcService(WinRT 实现),Android/其他平台用 SmtcServiceStub(空实现)。</summary>
public interface ISmtcService
{
    event Action? PlayPauseRequested;

    event Action? NextRequested;

    event Action? PreviousRequested;

    event Action<long>? SeekRequested;

    /// <summary>初始化(Windows 需前台窗口 HWND;其他平台 Stub 空实现)。</summary>
    void Initialize(IntPtr hwnd);

    void SetNowPlaying(string title, string artist, string album, string coverUrl);
}
