using System.Runtime.CompilerServices;

// Headless 是同解决方案下的无头诊断/逆向探针工程(各种 --xxx 探针),需要访问客户端
// 内部的写通道(如 QQMusicApiClient 的 asset 写方法)来盲试端点。它不参与发布,
// 仅本地验证用,故对其开放 internal 可见性。
[assembly: InternalsVisibleTo("ALyricEase.Headless")]
