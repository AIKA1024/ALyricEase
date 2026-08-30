using System.Threading.Tasks;
using Android.Content;
using Android.Runtime;

namespace ALyricEase.Infrastructure;

/// <summary>Android 车机剪贴板兼容读取。
/// 不依赖 ClipDescription 的 MIME 声明，直接让系统把每个 ClipData.Item 转成文本。</summary>
internal static class AndroidClipboardTextReader
{
    public static Task<string?> TryGetTextAsync()
    {
        var context = global::Android.App.Application.Context;
        var clipboard = context
            .GetSystemService(Context.ClipboardService)?
            .JavaCast<ClipboardManager>();
        var clip = clipboard?.PrimaryClip;

        if (clip is null)
            return Task.FromResult<string?>(null);

        for (var i = 0; i < clip.ItemCount; i++)
        {
            var item = clip.GetItemAt(i);
            if (item is null)
                continue;

            // Text 先走零开销路径；CoerceToText 还能处理 HTML、URI 以及厂商自定义条目。
            var text = item.Text?.ToString();
            if (string.IsNullOrEmpty(text))
                text = item.CoerceToText(context)?.ToString();

            if (!string.IsNullOrEmpty(text))
                return Task.FromResult<string?>(text);
        }

        return Task.FromResult<string?>(null);
    }
}
