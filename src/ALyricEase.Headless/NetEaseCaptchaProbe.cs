using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ALyricEase.Services.Crypto;
using ALyricEase.Services.Auth;
using ALyricEase.Services.NetEase;
using ALyricEase.Views;

namespace ALyricEase.Headless;

/// <summary>网易云 -462 验证页真窗口探针(--necaptcha):走一遍二维码 key/check 拿到验证地址,
/// 用 QqCaptchaWindow 的真 WebView 打开,几秒后把 DOM 文本打出来 —— 用来确认
/// "伪装 UA 后页面到底渲染了滑块还是仍在提示请在 App 内打开"。</summary>
public static class NetEaseCaptchaProbe
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? url = null;
        NetEaseApiClient? api = null;
        try
        {
            api = new NetEaseApiClient(new CryptoService(), new CnIpPool(), new CookieStore());
            var session = await api.CreateQrLoginAsync().ConfigureAwait(true);
            try
            {
                await api.CheckQrLoginAsync(session.Key).ConfigureAwait(true);
            }
            catch (ApiException ex) when (ex.VerifyUrl is { Length: > 0 })
            {
                url = ex.VerifyUrl;
                Console.WriteLine($"[necap] 验证地址 code={ex.Code}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[necap] 取验证地址失败: {ex.Message}");
        }

        if (url is null)
        {
            Console.WriteLine("[necap] 本次没有触发 -462(风控已解除?)，无验证页可测");
            return 1;
        }

        var window = new QqCaptchaWindow(new Uri(url), isNetEase: true, api?.GetCaptchaCookies());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // 等页面脚本渲染完(滑块组件要拉校验配置)
        await Task.Delay(25000);

        var web = window.GetVisualDescendants().OfType<NativeWebView>().FirstOrDefault();
        if (web is null)
        {
            Console.WriteLine("[necap] 找不到 NativeWebView");
            return 1;
        }

        try
        {
            var text = await web.InvokeScript("document.body.innerText").ConfigureAwait(true);
            var plain = (text ?? "<null>").Replace("\r", " ").Replace("\n", " | ");
            Console.WriteLine($"[necap] innerText(len={(text?.Length ?? 0)})={Cut(plain, 400)}");
            var canvas = await web.InvokeScript("document.querySelectorAll('canvas').length").ConfigureAwait(true);
            var inputs = await web.InvokeScript("document.querySelectorAll('input').length").ConfigureAwait(true);
            Console.WriteLine($"[necap] canvas={canvas} input={inputs}");
            var ready = await web.InvokeScript("document.readyState + ' | ' + location.href").ConfigureAwait(true);
            Console.WriteLine($"[necap] state={Cut(ready ?? "", 200)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[necap] InvokeScript 失败: {ex.Message}");
        }

        window.Close();
        return 0;
    }

    private static string Cut(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
