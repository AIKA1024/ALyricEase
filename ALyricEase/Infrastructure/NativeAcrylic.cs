using System;
using System.Runtime.InteropServices;

namespace ALyricEase.Infrastructure;

/// <summary>Windows 原生亚克力(DWM SetWindowCompositionAttribute)。
/// Avalonia 的 ExperimentalAcrylicMaterial 只有 Digger(采样自身视觉树,做不出 WinUI HostBackdrop
/// 那种透桌面的玻璃感),故在 Win10+ 直接调 DWM 开 ACCENT_ENABLE_ACRYLICBLURBEHIND。
/// 非 Windows / 失败时回退 false,调用方保持纯色背景。用法:窗口 Opened 后调一次。
/// 拖动性能:亚克力拖动时每帧重算模糊会很卡,须在 BeginMoveDrag 前临时降级为轻量
/// ACCENT_ENABLE_BLURBEHIND,松手恢复(TranslucentTB 等同样做法)。</summary>
public static class NativeAcrylic
{
    public const int WcaAccentPolicy = 19;
    public const int AccentDisabled = 0;
    public const int AccentEnableBlurBehind = 3;   // 轻量模糊(拖动占位,性能好)
    public const int AccentEnableAcrylicBlurBehind = 4; // 亚克力(静态,贵)

    /// <summary>当前主题的亚克力 tint(AABBGGRR)。暗色 #202020,亮色 #F3F3F3。</summary>
    public const uint DefaultGradientColor = 0x99202020; // 60% 不透明度暗色 tint

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor; // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attrib;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    /// <summary>让 DWM 管理整窗背景(-1 = 全窗扩展),内容区覆盖到边缘,消除系统边框线。
    /// 在窗口句柄就绪后调用一次即可。</summary>
    public static bool ExtendFrameIntoClientArea(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var margins = new Margins { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            return DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>给窗口开原生亚克力。gradientColor 是 AABBGGRR。返回 true 表示设置成功。</summary>
    public static bool Enable(IntPtr hwnd, uint gradientColor) => Apply(hwnd, AccentEnableAcrylicBlurBehind, gradientColor, 2);

    /// <summary>拖动窗口时临时降级为轻量模糊(无 tint),大幅降低拖动卡顿。</summary>
    public static bool EnableBlur(IntPtr hwnd) => Apply(hwnd, AccentEnableBlurBehind, 0, 0);

    /// <summary>完全禁用背景效果(回到不透明)。</summary>
    public static bool Disable(IntPtr hwnd) => Apply(hwnd, AccentDisabled, 0, 0);

    private static bool Apply(IntPtr hwnd, int accentState, uint gradientColor, int accentFlags)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = accentState,
                AccentFlags = accentFlags,
                GradientColor = unchecked((int)gradientColor),
            };
            var size = Marshal.SizeOf<AccentPolicy>();
            var p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, p, false);
                var data = new WindowCompositionAttribData
                {
                    Attrib = WcaAccentPolicy,
                    Data = p,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }
        catch
        {
            return false;
        }
    }
}
