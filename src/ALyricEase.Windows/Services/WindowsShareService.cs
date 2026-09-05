using System.Runtime.InteropServices;
using ALyricEase.Services;
using Windows.ApplicationModel.DataTransfer;
using WinRT;

namespace ALyricEase.Services.Sharing;

/// <summary>Windows 桌面 Share Sheet。DataTransferManager 的普通静态入口仅适用于 UWP；
/// Win32/Avalonia 窗口必须通过 IDataTransferManagerInterop 传入 HWND。</summary>
public sealed class WindowsShareService : IPlatformShareService, IDisposable
{
    private static readonly Guid DataTransferManagerIid =
        new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    private readonly IDataTransferManagerInterop _interop =
        DataTransferManager.As<IDataTransferManagerInterop>();
    private DataTransferManager? _manager;
    private nint _ownerHandle;
    private SharePayload? _pending;

    public Task<bool> ShareUriAsync(nint ownerHandle, string title, string description, Uri uri)
    {
        if (ownerHandle == 0) return Task.FromResult(false);

        try
        {
            EnsureManager(ownerHandle);
            _pending = new SharePayload(title, description, uri);
            _interop.ShowShareUIForWindow(ownerHandle);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
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
