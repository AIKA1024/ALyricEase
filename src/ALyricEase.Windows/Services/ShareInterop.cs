using System;
using System.Runtime.InteropServices;

namespace ALyricEase.Services.Sharing;

/// <summary>IDataTransferManagerInterop 的取用路径(NativeAOT 可用版)。
///
/// 通行写法 `DataTransferManager.As&lt;IDataTransferManagerInterop&gt;()` 依赖**内置 COM 互操作**
/// (Marshal.GetComInterfaceForObject + ComImport 接口的反射建桩)。NativeAOT 默认不编内置 COM ⇒
/// 发布版里这行在 WindowsShareService 的**字段初始化**就抛,调用方的 catch 把它吞掉,
/// 用户看到的就是"点分享没反应";而 Debug/Release JIT 一切正常 ⇒ 只在 --sharetest(JIT) 里验不出来。
///
/// 这里不碰内置 COM:直接 RoGetActivationFactory 取 DataTransferManager 的激活工厂,
/// 再按 vtable 槽位调 IDataTransferManagerInterop(IUnknown 派生:3=GetForWindow,4=ShowShareUIForWindow)。
/// 返回的 IDataTransferManager ABI 指针仍交给 CsWinRT 的 MarshalInterface 包成托管对象,
/// 事件订阅/数据填充走正常投影,不受影响。</summary>
internal static unsafe class ShareInterop
{
    private const string ClassName = "Windows.ApplicationModel.DataTransfer.DataTransferManager";

    // IDataTransferManagerInterop 的 IID;也是 GetForWindow 要的 riid 之外的工厂接口。
    private static readonly Guid InteropIid = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");

    // IDataTransferManagerInterop 的 vtable 槽位(IUnknown 的 0~2 之后)。
    private const int SlotGetForWindow = 3;
    private const int SlotShowShareUIForWindow = 4;

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    private static nint s_factory;

    /// <summary>激活工厂对象(进程内复用;失败抛,由调用方转成"分享不可用")。</summary>
    private static nint Factory
    {
        get
        {
            if (s_factory != 0) return s_factory;

            var hr = WindowsCreateString(ClassName, ClassName.Length, out var classId);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                var iid = InteropIid;
                hr = RoGetActivationFactory(classId, ref iid, out var factory);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                s_factory = factory;
                return s_factory;
            }
            finally
            {
                WindowsDeleteString(classId);
            }
        }
    }

    /// <summary>IDataTransferManagerInterop.GetForWindow;返回的 ABI 指针由调用方包成 DataTransferManager。</summary>
    public static nint GetForWindow(nint appWindow, ref Guid riid)
    {
        var factory = Factory;
        var getForWindow = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Vtable(factory)[SlotGetForWindow];

        nint abi = 0;
        int hr;
        fixed (Guid* pIid = &riid)
        {
            hr = getForWindow(factory, appWindow, pIid, &abi);
        }

        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return abi;
    }

    /// <summary>IDataTransferManagerInterop.ShowShareUIForWindow —— 要求 appWindow 是前台窗口,否则静默无效果。</summary>
    public static void ShowShareUIForWindow(nint appWindow)
    {
        var factory = Factory;
        var show = (delegate* unmanaged[Stdcall]<nint, nint, int>)Vtable(factory)[SlotShowShareUIForWindow];

        var hr = show(factory, appWindow);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static nint* Vtable(nint instance) => *(nint**)instance;

    /// <summary>诊断用:返回能否在本宿主取到 interop(不抛)。</summary>
    public static bool TryProbe(out string detail)
    {
        try
        {
            _ = Factory;
            detail = "RoGetActivationFactory OK";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})";
            return false;
        }
    }
}
