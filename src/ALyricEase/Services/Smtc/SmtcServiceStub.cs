namespace ALyricEase.Services.Smtc;

/// <summary>空实现 SMTC(非 Windows 平台用):事件从不触发,SetNowPlaying 无操作。
/// CS0067:接口要求的事件在 Stub 中从不触发,属预期。</summary>
#pragma warning disable CS0067
public sealed class SmtcServiceStub : ISmtcService
{
    public event Action? PlayPauseRequested;

    public event Action? NextRequested;

    public event Action? PreviousRequested;

    public event Action<long>? SeekRequested;

    public void Initialize(IntPtr hwnd)
    {
    }

    public void SetNowPlaying(string title, string artist, string album, string coverUrl)
    {
    }
}
