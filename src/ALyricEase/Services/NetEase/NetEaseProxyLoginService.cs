using System.Text.Json.Nodes;
using Copycat;

namespace ALyricEase.Services.NetEase;

public enum NetEaseProxyLoginStage
{
    /// <summary>代理已就绪并监听,等待用户在官方客户端设置代理并发起请求。</summary>
    Listening,

    /// <summary>已捕获到 MUSIC_U,正在调资料接口验证。</summary>
    Verifying,

    /// <summary>验证通过,拿到昵称,登录态已持久化。</summary>
    Ready,

    /// <summary>捕获到的凭证验证未通过(已回滚),继续监听下一次请求。</summary>
    Rejected,
}

/// <summary>一次代理登录状态更新。Ready/Rejected 之外 Error 有值表示代理本身故障。</summary>
public sealed record NetEaseProxyLoginUpdate(
    NetEaseProxyLoginStage Stage,
    string? Nickname = null,
    int Port = 0,
    string? Error = null);

/// <summary>网易云官方客户端代理登录:本地起一个 MITM 代理(Copycat,Go 实现),
/// 引导用户在网易云 PC 客户端「设置 → 工具 → HTTP 代理」填入 127.0.0.1:端口。
/// 客户端不校验 TLS 证书,代理可解密其 eapi 请求,从解密后的内嵌 header /
/// Cookie 中提取 MUSIC_U,再调服务端资料接口验证,通过后走既有
/// <see cref="NetEaseApiClient.SetMusicUCookie"/> 持久化链路 —— 与粘贴 Cookie 登录完全同路。
/// Copycat 只带 win-x64/win-arm64 native,非 Windows 平台入口应依据 <see cref="IsSupported"/> 隐藏。</summary>
public sealed class NetEaseProxyLoginService : IDisposable
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    private readonly NetEaseApiClient _api;
    private readonly Func<string, Task<string>>? _verifierOverride;
    private readonly SemaphoreSlim _verifyGate = new(1, 1);
    private CopycatProxy? _proxy;
    private Action<NetEaseProxyLoginUpdate>? _observer;
    private bool _succeeded;

    /// <summary>探针注入的自定义验证器:返回昵称视为通过;null 走真实链路(SetMusicUCookie + 资料接口)。</summary>
    public NetEaseProxyLoginService(NetEaseApiClient api, Func<string, Task<string>>? verifierOverride = null)
    {
        _api = api;
        _verifierOverride = verifierOverride;
    }

    /// <summary>启动代理并开始监听。返回实际监听端口;失败抛异常由调用方提示。</summary>
    public int Start(Action<NetEaseProxyLoginUpdate> observer)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("官方客户端代理登录仅支持 Windows");
        if (_proxy is not null) throw new InvalidOperationException("代理已在运行");

        _observer = observer;
        _succeeded = false;
        var proxy = new CopycatProxy();
        proxy.HeadersCaptured += OnHeadersCaptured;
        proxy.Error += OnProxyError;
        _proxy = proxy;
        var port = proxy.Start(0); // 0 = 由系统分配空闲端口
        if (port <= 0)
        {
            CleanupProxy();
            throw new ApiException("代理端口分配失败,请重试", -1);
        }
        Notify(new NetEaseProxyLoginUpdate(NetEaseProxyLoginStage.Listening, Port: port));
        return port;
    }

    /// <summary>停止代理并解除监听;幂等。</summary>
    public void Stop()
    {
        CleanupProxy();
        _observer = null;
    }

    private void CleanupProxy()
    {
        var proxy = _proxy;
        _proxy = null;
        if (proxy is null) return;
        proxy.HeadersCaptured -= OnHeadersCaptured;
        proxy.Error -= OnProxyError;
        try
        {
            proxy.Terminate();
        }
        catch
        {
            // Go 侧关闭失败不阻断 UI 流程;进程内非托管资源随回调句柄一并失效。
        }
    }

    private void OnProxyError(object? sender, Exception ex)
        => Notify(new NetEaseProxyLoginUpdate(NetEaseProxyLoginStage.Listening, Error: ex.Message));

    /// <summary>Copycat 回调:解密后的 eapi 请求摘要。结构 {EApiHeaders:{...}, Cookies:{...}, Headers:{...}}。
    /// 只有带 MUSIC_U 的请求才有登录价值(客户端未登录时的匿名 eapi 请求会频繁触发,直接忽略);
    /// 验证并发用信号量 try-occupy 折叠 —— 客户端一次登录会连发多个 eapi 请求。</summary>
    private async void OnHeadersCaptured(object? sender, HeadersCapturedEventArgs e)
    {
        var musicU = ExtractMusicU(e.Headers);
        if (musicU is null || _succeeded) return;
        if (!await _verifyGate.WaitAsync(0)) return;
        try
        {
            Notify(new NetEaseProxyLoginUpdate(NetEaseProxyLoginStage.Verifying));
            string nickname;
            if (_verifierOverride is { } verifier)
            {
                nickname = await verifier(musicU); // 探针路径:不碰真实存档
            }
            else
            {
                _api.SetMusicUCookie(musicU); // 解析+持久化,与粘贴登录同一条容错链路
                nickname = (await _api.GetUserProfileAsync()).Nickname; // 服务端验证,失败抛 ApiException
            }
            _succeeded = true;
            Notify(new NetEaseProxyLoginUpdate(NetEaseProxyLoginStage.Ready, nickname));
        }
        catch (Exception ex)
        {
            // 服务端不认:不留半残登录态(与 QQ Cookie 登录同样的回滚哲学)
            if (_verifierOverride is null)
            {
                try { _api.ClearCookie(); } catch { /* 回滚失败不阻断继续监听 */ }
            }
            Notify(new NetEaseProxyLoginUpdate(NetEaseProxyLoginStage.Rejected, Error: ex.Message));
        }
        finally
        {
            _verifyGate.Release();
        }
    }

    /// <summary>优先取 Cookies(客户端原样 cookie),回退 EApiHeaders(eapi 内嵌 header)。</summary>
    private static string? ExtractMusicU(JsonObject headers)
    {
        if (TryGetString(headers, "Cookies", "MUSIC_U", out var value) && value.Length > 0) return value;
        if (TryGetString(headers, "EApiHeaders", "MUSIC_U", out value) && value.Length > 0) return value;
        return null;
    }

    private static bool TryGetString(JsonObject root, string section, string key, out string value)
    {
        value = "";
        if (root.TryGetPropertyValue(section, out var node) && node is JsonObject obj &&
            obj.TryGetPropertyValue(key, out var item) && item is JsonValue jsonValue &&
            jsonValue.TryGetValue(out string? raw) && raw is not null && raw.Length > 0)
        {
            value = raw;
            return true;
        }
        return false;
    }

    private void Notify(NetEaseProxyLoginUpdate update) => _observer?.Invoke(update);

    public void Dispose()
    {
        Stop();
        _verifyGate.Dispose();
    }
}
