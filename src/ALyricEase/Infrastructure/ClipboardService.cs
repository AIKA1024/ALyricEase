using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace ALyricEase.Infrastructure;

/// <summary>VM/视图层剪贴板访问:按生命周期形态解析当前 TopLevel(桌面取 MainWindow,
/// 单视图(Android)取 MainView);无窗口句柄(如无头探针)时静默失败。</summary>
public static class ClipboardService
{
    private static Func<Task<string?>>? _platformTextReaderFallback;

    public static TopLevel? TopLevel => Application.Current?.ApplicationLifetime switch
    {
        IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
        ISingleViewApplicationLifetime single => TopLevel.GetTopLevel(single.MainView),
        _ => null,
    };

    /// <summary>写文本到系统剪贴板;解析不到 TopLevel/剪贴板时静默失败返回 false。</summary>
    public static async Task<bool> TryCopyTextAsync(string text)
    {
        var clipboard = TopLevel?.Clipboard;
        if (clipboard is null) return false;
        await clipboard.SetTextAsync(text); // Avalonia 12 起为 ClipboardExtensions 扩展方法
        return true;
    }

    /// <summary>注册平台文本读取后备。平台实现只应在 Avalonia 标准读取返回空时被调用。</summary>
    public static void RegisterTextReaderFallback(Func<Task<string?>> reader) =>
        _platformTextReaderFallback = reader;

    /// <summary>读取系统剪贴板中的文本;优先走 Avalonia,再尝试平台后备。</summary>
    public static async Task<string?> TryGetTextAsync()
    {
        var clipboard = TopLevel?.Clipboard;
        if (clipboard is not null)
        {
            try
            {
                var text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            catch
            {
                // 标准实现失败后继续尝试平台后备。
            }
        }

        if (_platformTextReaderFallback is not { } fallback)
            return null;

        try
        {
            return await fallback();
        }
        catch
        {
            // 粘贴属于可恢复的 UI 操作，不让厂商平台异常击穿事件循环。
            return null;
        }
    }
}
