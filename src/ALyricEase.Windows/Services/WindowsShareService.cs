using System;
using System.IO;
using System.Runtime.InteropServices;
using ALyricEase.Services;
using Windows.ApplicationModel.DataTransfer;
using WinRT;

namespace ALyricEase.Services.Sharing;

/// <summary>Windows 桌面 Share Sheet。DataTransferManager 的普通静态入口仅适用于 UWP；
/// Win32/Avalonia 窗口必须通过 IDataTransferManagerInterop 传入 HWND。
/// ⚠ ShowShareUIForWindow 要求目标窗口处于前台,否则静默无效 —— 调用前必须 SetForegroundWindow
/// (--sharetest 探针实测,2026-10-02)。</summary>
public sealed class WindowsShareService : IPlatformShareService, IDisposable
{
    private static readonly Guid DataTransferManagerIid =
        new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private readonly IDataTransferManagerInterop _interop =
        DataTransferManager.As<IDataTransferManagerInterop>();
    private DataTransferManager? _manager;
    private nint _ownerHandle;
    private SharePayload? _pending;

    public Task<bool> ShareUriAsync(nint ownerHandle, string title, string description, Uri uri)
    {
        if (ownerHandle == 0)
        {
            ShareDiag("ownerHandle=0 ⇒ 直接返回 false(检查 anchor 的 TopLevel 是否失联)");
            return Task.FromResult(false);
        }

        try
        {
            EnsureManager(ownerHandle);
            _pending = new SharePayload(title, description, uri);
            // ⚠ ShowShareUIForWindow 要求 owner 是**前台**窗口,否则静默无效(不弹、不抛、返回 false)
            // (--sharetest 探针实测:DataRequested 不触发)。歌曲菜单是 Avalonia Popup(独立 HWND),
            // 点击菜单项时前台是弹窗而非主窗口 ⇒ 必须先把主窗口拉回前台;调用方进程拥有前台时
            // SetForegroundWindow 必定成功,不会被抢焦点防护拦截。
            var fgBefore = GetForegroundWindow();
            var setOk = SetForegroundWindow(ownerHandle);
            ShareDiag($"owner=0x{ownerHandle:X} 前台(调前)=0x{fgBefore:X} SetForegroundWindow={setOk} " +
                $"前台(调后)=0x{GetForegroundWindow():X}");
            _interop.ShowShareUIForWindow(ownerHandle);
            ShareDiag("ShowShareUIForWindow 已调(未抛异常;是否真弹看 DataRequested)");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            ShareDiag($"异常: {ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
            return Task.FromResult(false);
        }
    }

    /// <summary>分享链路诊断(ALY_SHARE_DIAG=1 或 Debug 构建开启):写 %TEMP%\aly-share-diag.log。
    /// 生产路径把异常吞成 false,UI 上"点击没反应"时靠它定位断在哪一环。</summary>
    internal static void ShareDiag(string message)
    {
        if (!s_diagEnabled) return;
        try
        {
            var text = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
            if (!s_diagHeaderWritten)
            {
                s_diagHeaderWritten = true;
                var buildPath = typeof(WindowsShareService).Assembly.Location;
                text = $"===== 新进程 PID={Environment.ProcessId} " +
                       $"构建={Path.GetFileName(buildPath)}@{File.GetLastWriteTime(buildPath):MM-dd HH:mm:ss} ====={Environment.NewLine}{text}";
            }

            File.AppendAllText(Path.Combine(Path.GetTempPath(), "aly-share-diag.log"), text);
        }
        catch
        {
            // 诊断绝不影响分享本身
        }
    }

    private static readonly bool s_diagEnabled = IsShareDiagEnabled();
    private static bool s_diagHeaderWritten;

    private static bool IsShareDiagEnabled()
    {
        var env = Environment.GetEnvironmentVariable("ALY_SHARE_DIAG");
        if (env == "0") return false;
        if (env == "1") return true;
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private void EnsureManager(nint ownerHandle)
    {
        if (_manager is not null && _ownerHandle == ownerHandle) return;

        if (_manager is not null)
            _manager.DataRequested -= OnDataRequested;

        var iid = DataTransferManagerIid;
        var abi = _interop.GetForWindow(ownerHandle, ref iid);
        _manager = MarshalInterface<DataTransferManager>.FromAbi(abi);
        _manager.DataRequested += OnDataRequested;
        _ownerHandle = ownerHandle;
    }

    private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        ShareDiag($"DataRequested 触发(面板已打开并要数据) pending={_pending is not null}");
        if (_pending is not { } payload)
        {
            args.Request.FailWithDisplayText("没有可分享的内容");
            return;
        }

        var data = args.Request.Data;
        data.Properties.Title = payload.Title;
        data.Properties.Description = payload.Description;
        data.SetText($"{payload.Description}{Environment.NewLine}{payload.Uri}");
        data.SetWebLink(payload.Uri);
        data.RequestedOperation = DataPackageOperation.Copy;
    }

    public void Dispose()
    {
        if (_manager is not null)
            _manager.DataRequested -= OnDataRequested;
        _manager = null;
    }

    private sealed record SharePayload(string Title, string Description, Uri Uri);

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow([In] nint appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] nint appWindow);
    }
}
