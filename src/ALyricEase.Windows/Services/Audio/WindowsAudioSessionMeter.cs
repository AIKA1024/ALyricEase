#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ALyricEase.Services.Audio;

/// <summary>
/// AOT-safe WASAPI session peak meter. It uses the raw IUnknown vtables instead of
/// <c>ComImport</c>, because built-in runtime COM wrappers are unavailable in NativeAOT.
/// Every instance owns QueryInterface references for this process' sessions on one endpoint.
/// </summary>
internal sealed unsafe class WindowsAudioSessionMeter : IDisposable
{
    private const uint ClsctxInprocServer = 0x1;
    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;

    private static readonly Guid s_mmDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid s_mmDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid s_audioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid s_audioSessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid s_audioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    private readonly nint[] _meters;

    private WindowsAudioSessionMeter(nint[] meters) => _meters = meters;

    internal static bool InitializeApartment()
    {
        var hr = CoInitializeEx(0, 0); // COINIT_MULTITHREADED
        return hr >= 0;
    }

    internal static void UninitializeApartment() => CoUninitialize();

    internal static WindowsAudioSessionMeter? TryCreate(string? winRtDeviceId, int processId)
    {
        nint enumerator = 0;
        nint device = 0;
        nint manager = 0;
        nint sessions = 0;
        try
        {
            fixed (Guid* classId = &s_mmDeviceEnumeratorClass)
            fixed (Guid* interfaceId = &s_mmDeviceEnumerator)
            {
                if (CoCreateInstance(classId, 0, ClsctxInprocServer, interfaceId, &enumerator) < 0 || enumerator == 0)
                    return null;
            }

            var endpointId = ExtractEndpointId(winRtDeviceId);
            if (endpointId is null)
            {
                var getDefault = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Vtable(enumerator)[4];
                if (getDefault(enumerator, DataFlowRender, RoleMultimedia, &device) < 0 || device == 0)
                    return null;
            }
            else
            {
                fixed (char* id = endpointId)
                {
                    var getDevice = (delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Vtable(enumerator)[5];
                    if (getDevice(enumerator, id, &device) < 0 || device == 0)
                        return null;
                }
            }

            fixed (Guid* managerId = &s_audioSessionManager2)
            {
                var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Vtable(device)[3];
                if (activate(device, managerId, ClsctxInprocServer, 0, &manager) < 0 || manager == 0)
                    return null;
            }

            var getEnumerator = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(manager)[5];
            if (getEnumerator(manager, &sessions) < 0 || sessions == 0) return null;

            var getCount = (delegate* unmanaged[Stdcall]<nint, int*, int>)Vtable(sessions)[3];
            var count = 0;
            if (getCount(sessions, &count) < 0) return null;

            var meters = new List<nint>(2);
            var getSession = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vtable(sessions)[4];
            for (var index = 0; index < count; index++)
            {
                nint control = 0;
                nint control2 = 0;
                try
                {
                    if (getSession(sessions, index, &control) < 0 || control == 0) continue;
                    fixed (Guid* control2Id = &s_audioSessionControl2)
                    {
                        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtable(control)[0];
                        if (queryInterface(control, control2Id, &control2) < 0 || control2 == 0) continue;
                    }

                    // IAudioSessionEnumerator.GetSession 返回的是 IAudioSessionControl；必须先 QI 到
                    // IAudioSessionControl2 才能读取其第 14 个槽位 GetProcessId。
                    var getProcessId = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtable(control2)[14];
                    uint owner = 0;
                    if (getProcessId(control2, &owner) < 0 || owner != (uint)processId) continue;

                    nint meter = 0;
                    fixed (Guid* meterId = &s_audioMeterInformation)
                    {
                        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtable(control2)[0];
                        if (queryInterface(control2, meterId, &meter) >= 0 && meter != 0)
                            meters.Add(meter);
                    }
                }
                finally
                {
                    Release(control2);
                    Release(control);
                }
            }

            return meters.Count == 0 ? null : new WindowsAudioSessionMeter([.. meters]);
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
            Release(enumerator);
        }
    }

    internal bool TryGetPeak(out float peak)
    {
        peak = 0;
        var any = false;
        foreach (var meter in _meters)
        {
            try
            {
                var value = 0f;
                var getPeak = (delegate* unmanaged[Stdcall]<nint, float*, int>)Vtable(meter)[3];
                if (getPeak(meter, &value) < 0) continue;
                peak = Math.Max(peak, value);
                any = true;
            }
            catch
            {
                // Audio device/session may disappear while switching tracks or output routes.
            }
        }
        return any;
    }

    public void Dispose()
    {
        foreach (var meter in _meters) Release(meter);
    }

    private static string? ExtractEndpointId(string? winRtDeviceId)
    {
        if (string.IsNullOrWhiteSpace(winRtDeviceId)) return null;
        var start = winRtDeviceId.IndexOf("{0.0.0.", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return winRtDeviceId;
        var separator = winRtDeviceId.IndexOf("}.{", start, StringComparison.Ordinal);
        if (separator < 0) return null;
        var end = winRtDeviceId.IndexOf('}', separator + 2);
        return end < 0 ? null : winRtDeviceId[start..(end + 1)];
    }

    private static nint* Vtable(nint instance) => *(nint**)instance;

    private static void Release(nint instance)
    {
        if (instance == 0) return;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)Vtable(instance)[2];
        _ = release(instance);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* interfaceId, nint* instance);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint apartmentType);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
#endif
