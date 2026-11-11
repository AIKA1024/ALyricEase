using System.Net;

namespace ALyricEase.Services.Network;

/// <summary>应用网络代理模式(state.json 按 int 存,顺序即兼容性,勿调整)。</summary>
public enum ProxyMode
{
    /// <summary>跟随系统:Windows 的系统代理设置,或 HTTP_PROXY/HTTPS_PROXY 环境变量(默认,与历史行为一致)。</summary>
    System = 0,

    /// <summary>直连:完全绕过系统代理(系统代理指向失效地址时用)。</summary>
    Direct = 1,

    /// <summary>手动:用户指定的 HTTP 代理,HTTPS 请求走 CONNECT 隧道。</summary>
    Manual = 2,
}

/// <summary>
/// 代理配置的不可变快照。手动模式地址不完整时 <see cref="HasEndpoint"/> 为 false,
/// 解析阶段按直连处理 —— 但保存前必须校验,别让用户以为自己设上了代理。
/// <para>
/// 刻意用引用类型(record class)而非 struct:切换配置时只写一个引用(原子),读方
/// 最多看到"完整的旧快照",不会读到 mode 已是手动、host 还是空串的撕裂状态。
/// </para>
/// </summary>
public sealed record ProxySnapshot(
    ProxyMode Mode,
    string Host = "",
    int Port = 0,
    string Username = "",
    string Password = "")
{
    /// <summary>默认配置:跟随系统。</summary>
    public static ProxySnapshot SystemDefault => new(ProxyMode.System);

    /// <summary>手动模式且端点合法(主机非空、端口 1~65535)。</summary>
    public bool HasEndpoint =>
        Mode == ProxyMode.Manual && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535;

    /// <summary>手动端点的 URI;不合法返回 null。</summary>
    public Uri? Endpoint => HasEndpoint
        ? new Uri("http://" + Host.Trim() + ":" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UriKind.Absolute)
        : null;

    /// <summary>填了用户名才走 407 认证。</summary>
    public bool HasCredential => Mode == ProxyMode.Manual && !string.IsNullOrEmpty(Username);

    /// <summary>设置页卡片的副文案。</summary>
    public string DisplayText => Mode switch
    {
        ProxyMode.Direct => "不使用代理",
        ProxyMode.Manual => HasEndpoint ? $"手动 · {Host.Trim()}:{Port}" : "手动(未配置)",
        _ => "跟随系统",
    };
}

/// <summary>
/// 全应用统一的代理出口。
/// <para>
/// 各处 <c>HttpClientHandler</c>/<c>SocketsHttpHandler</c> 一律把 <c>Proxy</c> 指向
/// <see cref="Instance"/> —— 这个实例**整个进程生命周期内不替换**,每次请求时它才去读
/// <see cref="Current"/> 快照。于是用户在设置里改代理**不用重启、不用重建 client**:
/// 新连接自然走新出口,旧 keep-alive 连接由 <c>PooledConnectionIdleTimeout</c>(默认 1 分钟)
/// 自然回收后也换过来。
/// </para>
/// <para>
/// 反过来,如果直接把 <c>HttpClientHandler.Proxy</c> 设成快照里算出来的具体 <c>WebProxy</c>,
/// 那 handler 建好后就没有公开的可靠途径去改它(连接池还按 host 复用),改设置就只能重启。
/// </para>
/// </summary>
public static class NetworkProxy
{
    private static volatile ProxySnapshot _current = ProxySnapshot.SystemDefault;

    /// <summary>当前生效的配置(读写只在启动加载与用户保存两处)。</summary>
    public static ProxySnapshot Current => _current;

    /// <summary>交给所有 handler 的恒定代理实例(见类型注释)。</summary>
    public static IWebProxy Instance { get; } = new SnapshotProxy();

    /// <summary>切换代理。只改快照,不动任何已建好的 handler。</summary>
    public static void Apply(ProxySnapshot snapshot) => _current = snapshot;

    /// <summary>解析目标地址该走的代理;null = 直连(.NET 的 IWebProxy 约定)。</summary>
    internal static Uri? Resolve(Uri destination, ProxySnapshot snapshot)
    {
        switch (snapshot.Mode)
        {
            case ProxyMode.Direct:
                return null;
            case ProxyMode.Manual:
                return snapshot.Endpoint; // 不完整 ⇒ null ⇒ 直连(保存前已校验)
            default:
                try
                {
                    return HttpClient.DefaultProxy.GetProxy(destination);
                }
                catch
                {
                    // 个别平台读系统代理会抛(无 WinINET/无环境变量);此时直连好过整个请求挂掉。
                    return null;
                }
        }
    }

    internal static bool IsBypassed(Uri host, ProxySnapshot snapshot)
    {
        switch (snapshot.Mode)
        {
            case ProxyMode.Direct:
                return true;
            case ProxyMode.Manual:
                return false;
            default:
                try
                {
                    return HttpClient.DefaultProxy.IsBypassed(host);
                }
                catch
                {
                    return true;
                }
        }
    }

    internal static ICredentials? Credentials(ProxySnapshot snapshot) =>
        snapshot.HasCredential ? new NetworkCredential(snapshot.Username, snapshot.Password) : null;

    /// <summary>把快照转成一个固定的 IWebProxy("测试连接"用,不受当前设置影响)。</summary>
    internal static IWebProxy CreateFixed(ProxySnapshot snapshot) => new FixedProxy(snapshot);

    private sealed class SnapshotProxy : IWebProxy
    {
        // getter 每次现算:凭据永远跟着当前快照走,不需要在切换代理时重新挂到 handler 上。
        public ICredentials? Credentials
        {
            get => NetworkProxy.Credentials(_current);
            set { }
        }

        public Uri? GetProxy(Uri destination) => Resolve(destination, _current);

        public bool IsBypassed(Uri host) => NetworkProxy.IsBypassed(host, _current);
    }

    private sealed class FixedProxy(ProxySnapshot snapshot) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => NetworkProxy.Credentials(snapshot);
            set { }
        }

        public Uri? GetProxy(Uri destination) => Resolve(destination, snapshot);

        public bool IsBypassed(Uri host) => NetworkProxy.IsBypassed(host, snapshot);
    }
}
