namespace ALyricEase.Services.NetEase;

/// <summary>网易云接口业务错误。Code 透出接口返回码(403 风控、404、VIP 等),供 UI 判断。</summary>
public sealed class ApiException : Exception
{
    public int Code { get; }

    public ApiException(string message, int code = -1)
        : base(message)
    {
        Code = code;
    }
}
