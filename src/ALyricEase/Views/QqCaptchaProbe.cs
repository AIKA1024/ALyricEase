using System.Threading.Tasks;
using Avalonia.Controls;

namespace ALyricEase.Views;

/// <summary>
/// 腾讯防水墙验证成功探测：读取页面标题+正文文本，命中"验证成功/验证通过/已通过"返回 true。
/// 桌面模态窗口与安卓登录卡片覆盖层共用。
/// </summary>
public static class QqCaptchaProbe
{
    private const string Script = """
        (function(){
            var t = (document.title || '') + '\n' + (document.body ? document.body.innerText : '');
            return (t.indexOf('验证成功') >= 0 || t.indexOf('验证通过') >= 0 || t.indexOf('已通过') >= 0)
                ? 'captcha-yes' : 'captcha-no';
        })()
        """;

    /// <summary>InvokeScript 结果为 JSON 编码(WebView2/Android WebView 一致)，比较前去掉引号。</summary>
    public static async Task<bool> IsSuccessAsync(NativeWebView webView)
    {
        try
        {
            var result = await webView.InvokeScript(Script);
            return result is not null && result.Replace("\"", string.Empty).Trim().Contains("captcha-yes");
        }
        catch
        {
            // 适配器未就绪或页面跨域限制，等下一轮
            return false;
        }
    }
}
