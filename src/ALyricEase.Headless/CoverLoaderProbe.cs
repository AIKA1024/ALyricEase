using System.Net;
using System.Net.Sockets;
using System.Text;
using ALyricEase.Infrastructure;

namespace ALyricEase.Headless;

/// <summary>封面加载回归探针：同一 URL 并发请求只下载/解码一次，随后命中内存缓存。</summary>
internal static class CoverLoaderProbe
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public static async Task<int> RunAsync()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var stopped = new CancellationTokenSource();
        var requestCount = 0;
        var serveTask = ServeAsync(server, stopped.Token, () => Interlocked.Increment(ref requestCount));

        try
        {
            var url = $"http://127.0.0.1:{port}/cover.png?probe={Guid.NewGuid():N}";
            var loads = Enumerable.Range(0, 32).Select(_ => CoverLoader.LoadAsync(url)).ToArray();
            var images = await Task.WhenAll(loads);
            var first = images[0];
            var shared = first is not null && images.All(image => ReferenceEquals(first, image));

            var cached = await CoverLoader.LoadAsync(url);
            var playerSizedUrl = CoverLoader.BuildSizedUrl(url, 640);
            var variant = CoverLoader.TryGetLoadedVariant(playerSizedUrl);
            var netEaseFamily = CoverLoader.GetCoverFamilyKey(url)
                                == CoverLoader.GetCoverFamilyKey(playerSizedUrl);
            const string qqSmall = "https://y.gtimg.cn/music/photo_new/T002R150x150M000abc.jpg";
            var qqLarge = CoverLoader.BuildSizedUrl(qqSmall, 640);
            var qqFamily = CoverLoader.GetCoverFamilyKey(qqSmall)
                           == CoverLoader.GetCoverFamilyKey(qqLarge);
            var passed = requestCount == 1 && shared && ReferenceEquals(first, cached)
                         && ReferenceEquals(first, variant) && netEaseFamily && qqFamily;
            Console.WriteLine($"[cover-cache] requests={requestCount}, shared={shared}, " +
                              $"cached={ReferenceEquals(first, cached)}, variant={ReferenceEquals(first, variant)}");
            return passed ? 0 : 1;
        }
        finally
        {
            stopped.Cancel();
            server.Stop();
            await serveTask;
        }
    }

    private static async Task ServeAsync(TcpListener server, CancellationToken cancellationToken, Action onRequest)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(cancellationToken);
                _ = ReplyAsync(client, onRequest);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task ReplyAsync(TcpClient client, Action onRequest)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[1024];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return;
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            onRequest();
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {Png.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(Png);
        }
    }
}
