using Microsoft.Extensions.DependencyInjection;

namespace ALyricEase.Infrastructure;

/// <summary>静态服务定位器。Avalonia 12.1.1 稳定版无内置 DI 扩展,
/// 用 MEDI 建容器后经此访问,View 的无参构造取服务。</summary>
public static class ServiceLocator
{
    public static IServiceProvider? Provider { get; set; }

    public static T Get<T>() where T : notnull
        => Provider!.GetRequiredService<T>();
}
