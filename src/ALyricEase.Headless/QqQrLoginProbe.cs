using System;
using System.Threading;
using System.Threading.Tasks;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Services.QQMusic;

namespace ALyricEase.Headless;

/// <summary>QQ 扫码登录管线探针(--qqqr):跑通扫码前的全部阶段(QIMEI → GetSession →
/// CreateQRCode → MQTT 连接/订阅 → 等待手机推送),不做真实扫码。30 秒后主动取消,
/// 以各阶段是否走通定位"扫不了/登不上"的死点。</summary>
public static class QqQrLoginProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var service = new QQMusicQrLoginService(new CookieStore());
        var stage = QQMusicQrLoginStage.Preparing;
        var progress = new Progress<QQMusicQrLoginUpdate>(update =>
        {
            stage = update.Stage;
            var png = update.QrPng is { Length: > 0 } bytes ? $"二维码 {bytes.Length}B" : "无二维码";
            Console.WriteLine($"[qqqr] 阶段 → {update.Stage}({png})");
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var credential = await service.LoginAsync(progress, cts.Token).ConfigureAwait(false);
            Console.WriteLine($"[qqqr] 意外直接登录成功? musicid={credential.MusicId}(不应发生,没人扫码)");
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.WriteLine($"[qqqr] 探针超时主动取消 —— 已走到 {stage},此前各阶段全部正常" +
                              (stage == QQMusicQrLoginStage.Waiting
                                  ? "(二维码就绪、MQTT 在线,管线健康)"
                                  : "(注意:未到 Waiting,存在死点)"));
            return stage == QQMusicQrLoginStage.Waiting ? 0 : 1;
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"[qqqr][FAIL] {sw.ElapsedMilliseconds}ms 后在 {stage} 阶段失败: code={ex.Code} {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qqqr][FAIL] {sw.ElapsedMilliseconds}ms 后在 {stage} 阶段异常: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
