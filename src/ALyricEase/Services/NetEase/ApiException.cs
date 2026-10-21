namespace ALyricEase.Services.NetEase;

/// <summary>网易云接口业务错误。Code 透出接口返回码(403 风控、404、VIP 等),供 UI 判断。</summary>
public sealed class ApiException : Exception
{
    public int Code { get; }

    /// <summary>风控行为验证页(-462 等)的地址;非空时 UI 应用 WebView 打开让用户完成验证。</summary>
    public string? VerifyUrl { get; }

    public ApiException(string message, int code = -1, string? verifyUrl = null)
        : base(message)
    {
        Code = code;
        VerifyUrl = verifyUrl;
    }
}
