namespace ALyricEase.Services;

/// <summary>调用当前宿主的系统分享面板。平台实现负责把原生 UI 关联到当前窗口或 Activity。</summary>
public interface IPlatformShareService
{
    Task<bool> ShareUriAsync(nint ownerHandle, string title, string description, Uri uri);
}

/// <summary>无系统分享面的宿主（例如无头测试）使用的安全空实现。</summary>
public sealed class PlatformShareServiceStub : IPlatformShareService
{
    public Task<bool> ShareUriAsync(nint ownerHandle, string title, string description, Uri uri)
        => Task.FromResult(false);
}
