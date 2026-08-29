using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace ALyricEase.Infrastructure;

/// <summary>VM 层剪贴板访问:按生命周期形态解析当前 TopLevel(桌面取 MainWindow,
/// 单视图(Android)取 MainView)后写系统剪贴板;无窗口句柄(如无头探针)返回 false。</summary>
public static class ClipboardService
{
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
}
