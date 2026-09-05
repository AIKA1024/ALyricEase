#if WINDOWS
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace ALyricEase.Services.Taskbar;
/// <summary>任务栏缩略图工具栏(ITaskbarList3::ThumbBarAddButtons):鼠标 hover 任务栏窗口图标时,
/// 在缩略图下方显示上一曲/播放暂停/下一曲按钮。按钮点击经 WM_COMMAND(THBN_CLICKED)接收,
/// 通过 SetWindowSubclass 子类化窗口挂 WndProc(不破坏 Avalonia 自身 WndProc)。与 SMTC 独立。
/// 图标用内嵌 FluentSystemIcons Regular/Filled 字体渲染(与 UI 内 Icons.axaml 同一套字形),颜色随系统深浅色。</summary>
public sealed class TaskbarThumbButtons : IDisposable
{
    // THUMBBUTTONMASK(shobjidl_core.h):THB_BITMAP=0x1 THB_ICON=0x2 THB_TOOLTIP=0x4 THB_FLAGS=0x8
    public const uint ThbBitmaskBitmap = 0x1;
    public const uint ThbBitmaskIcon = 0x2;
    public const uint ThbBitmaskTooltip = 0x4;
    public const uint ThbBitmaskFlags = 0x8;
    public const uint ThbFlagEnabled = 0x0;
    public const uint ThbFlagDisabled = 0x1;
    public const uint ThbFlagDismissonclick = 0x2;
    public const uint ThbFlagNobackground = 0x4;
    public const uint ThbFlagHidden = 0x8;
    public const uint ThbBhId = 1;      // 子类 id
    // THBN_CLICKED(shobjidl_core.h):WM_COMMAND 的 HIWORD(wParam)
    public const int ThbnClicked = 0x1800;
    public const uint IdPrevious = 0;
    public const uint IdPlayPause = 1;
    public const uint IdNext = 2;

    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid TaskbarList3Iid = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");

    // THUMBBUTTON 官方布局:szTip 是内联 WCHAR[260],不是指针!
    // 偏移:dwMask(0) iId(4) iBitmap(8) hIcon(12) szTip(16,520字节) dwFlags(536)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    private readonly IntPtr _hwnd;
    private IntPtr _taskbar;
    private bool _initialized;
    private GCHandle _selfHandle;
    private IntPtr _iconPlay;
    private IntPtr _iconPause;
    private IntPtr _iconPrev;
    private IntPtr _iconNext;
    private IntPtr _imageList;
    private WndProcDelegate? _subclassProc;

    public event Action<uint>? ButtonClicked;

    public TaskbarThumbButtons(IntPtr hwnd) => _hwnd = hwnd;

    public unsafe bool Initialize()
    {
        if (_hwnd == IntPtr.Zero || _initialized) return false;
        try
        {
            var hr = CoCreateInstance(TaskbarListClsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, TaskbarList3Iid, out _taskbar);
            Log($"CoCreateInstance hr=0x{hr:x} taskbar={_taskbar}");
            if (hr != 0 || _taskbar == IntPtr.Zero) return false;

            var hrInit = ((delegate* unmanaged[Stdcall]<IntPtr, int>)GetVtable(_taskbar, 3))(_taskbar);
            Log($"HrInit hr=0x{hrInit:x}");
            if (hrInit != 0) return false;

            // 图标:内嵌 FluentSystemIcons 字体渲染字形 → HICON(经 hIcon 直连,绕开 ImageList)
            _iconPrev = RenderGlyphIcon(MediaShape.Previous);
            _iconPlay = RenderGlyphIcon(MediaShape.Play);
            _iconPause = RenderGlyphIcon(MediaShape.Pause);
            _iconNext = RenderGlyphIcon(MediaShape.Next);
            Log($"icons prev={_iconPrev} play={_iconPlay} pause={_iconPause} next={_iconNext}");

            // 按钮数组(THB_ICON 方式,hIcon 直连;iBitmap 不用 ImageList 时必须为 0)
            var size = Marshal.SizeOf<ThumbButton>();
            var pButtons = Marshal.AllocHGlobal(size * 3);
            try
            {
                var b = new ThumbButton[3];
                b[0] = new ThumbButton { dwMask = ThbBitmaskIcon | ThbBitmaskTooltip, iId = IdPrevious, iBitmap = 0, hIcon = _iconPrev, szTip = "上一曲", dwFlags = ThbFlagEnabled };
                b[1] = new ThumbButton { dwMask = ThbBitmaskIcon | ThbBitmaskTooltip, iId = IdPlayPause, iBitmap = 0, hIcon = _iconPlay, szTip = "播放/暂停", dwFlags = ThbFlagEnabled };
                b[2] = new ThumbButton { dwMask = ThbBitmaskIcon | ThbBitmaskTooltip, iId = IdNext, iBitmap = 0, hIcon = _iconNext, szTip = "下一曲", dwFlags = ThbFlagEnabled };
                for (var i = 0; i < 3; i++)
                    Marshal.StructureToPtr(b[i], pButtons + size * i, false);

                var hrAdd = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, ThumbButton*, int>)GetVtable(_taskbar, 15))(_taskbar, _hwnd, 3, (ThumbButton*)pButtons);
                Log($"ThumbBarAddButtons hr=0x{hrAdd:x} hwnd=0x{_hwnd:x}");
                if (hrAdd != 0) return false;
            }
            finally
            {
                Marshal.FreeHGlobal(pButtons);
            }

            // 子类化窗口接收 WM_COMMAND(THBN_CLICKED),dwRefData 指向 GCHandle(this)
            _subclassProc = SubclassProc;
            _selfHandle = GCHandle.Alloc(this);
            var subclassed = SetWindowSubclass(_hwnd, _subclassProc, ThbBhId, GCHandle.ToIntPtr(_selfHandle));
            Log($"SetWindowSubclass={subclassed}");

            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            Log($"Initialize EX: {ex}");
            return false;
        }
    }

    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ale_taskbar.log"), $"{DateTime.Now:HH:mm:ss} {msg}\n"); } catch { }
    }

    /// <summary>更新播放/暂停按钮图标(播放→暂停图标,暂停→播放图标)。</summary>
    public unsafe void SetPlaying(bool playing)
    {
        if (!_initialized) return;
        try
        {
            var size = Marshal.SizeOf<ThumbButton>();
            var p = Marshal.AllocHGlobal(size);
            try
            {
                var b = new ThumbButton { dwMask = ThbBitmaskIcon, iId = IdPlayPause, iBitmap = 0, hIcon = playing ? _iconPause : _iconPlay, dwFlags = ThbFlagEnabled };
                Marshal.StructureToPtr(b, p, false);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, ThumbButton*, int>)GetVtable(_taskbar, 16))(_taskbar, _hwnd, 1, (ThumbButton*)p);
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        catch { }
    }

    private static unsafe IntPtr GetVtable(IntPtr com, int slot) => (*(IntPtr**)com)[slot];

    private static unsafe IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == 0x0111 /* WM_COMMAND */ && (uint)(wParam.ToInt64() >> 16) == ThbnClicked)
        {
            var id = (uint)(wParam.ToInt64() & 0xFFFF);
            // 通过 GCHandle 找实例(存于 dwRefData)
            if (dwRefData != IntPtr.Zero && GCHandle.FromIntPtr(dwRefData).Target is TaskbarThumbButtons self)
                self.ButtonClicked?.Invoke(id);
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Fluent UI System Icons 字形码位,与应用内 Styles/Foundation/Icons.axaml 同源;
    /// 任务栏按钮统一用 Filled 字重(与应用主字体一致),码位为 Filled 字体的值。</summary>
    private enum MediaShape
    {
        Previous = 0xF633, // ic_fluent_previous_24_filled
        Play = 0xF610,     // ic_fluent_play_24_filled
        Pause = 0xF5AC,    // ic_fluent_pause_24_filled
        Next = 0xF574,     // ic_fluent_next_24_filled
    }

    private const int IconSize = 48; // 高分辨率渲染,系统缩到按钮尺寸更清晰

    private static PrivateFontCollection? _iconFontCollection;
    private static FontFamily? _iconFontFamily;
    private static IntPtr _fontMemory; // AddMemoryFont 要求内存在字体生命周期内存活

    /// <summary>从 Avalonia 资源加载内嵌 FluentSystemIcons-Filled 字体 → GDI+ FontFamily(仅一次)。</summary>
    private static FontFamily GetIconFontFamily()
    {
        if (_iconFontFamily is { } family) return family;
        using var stream = AssetLoader.Open(new Uri("avares://ALyricEase/Assets/Fonts/FluentSystemIcons-Filled.ttf"));
        using var ms = new System.IO.MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        _fontMemory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, _fontMemory, bytes.Length);
        _iconFontCollection = new PrivateFontCollection();
        _iconFontCollection.AddMemoryFont(_fontMemory, bytes.Length);
        return _iconFontFamily = _iconFontCollection.Families[0];
    }

    /// <summary>缩略图预览底色随系统深浅色:浅色主题用深色图标,深色主题用白色图标。
    /// 读 AppsUseLightTheme;运行中切主题不刷新(重启生效)。</summary>
    private static Color GetIconColor()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light && light == 1)
                return Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1A);
        }
        catch { }
        return Color.White;
    }

    /// <summary>用 Fluent System Icons 字体绘制媒体字形到透明位图 → HICON(经 hIcon 直连,绕开 ImageList)。</summary>
    private static IntPtr RenderGlyphIcon(MediaShape shape)
    {
        try
        {
            var family = GetIconFontFamily();
            using var bmp = new Bitmap(IconSize, IconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using var font = new Font(family, IconSize * 5f / 6, FontStyle.Regular, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(GetIconColor());
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(((char)shape).ToString(), font, brush, new RectangleF(0, 0, IconSize, IconSize), fmt);
            }
            return BitmapToAlphaIcon(bmp); // 手动构建带 alpha 的 HICON(CreateIconIndirect)
        }
        catch (Exception ex)
        {
            Log($"RenderGlyph EX: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>把 32bpp ARGB 位图转成带正确 alpha 的 HICON:用 DIBSection 32bpp 位图(保留 alpha)
    /// 作 XOR,AND mask 全 0(由 alpha 通道决定透明)。这是 Win32 alpha 图标的正确构造方式。
    /// GetHicon()/GetHbitmap() 都丢 alpha → 缩略图按钮图标变空。</summary>
    private static IntPtr BitmapToAlphaIcon(Bitmap bmp)
    {
        try
        {
            var w = bmp.Width; var h = bmp.Height;
            var hbmColor = IntPtr.Zero;
            var hbmMask = IntPtr.Zero;
            var hIcon = IntPtr.Zero;
            try
            {
                // 构造 32bpp DIBSection:预乘 alpha 的颜色 + alpha 通道
                var bmi = new BitmapInfo { bmiHeader = new BitmapInfoHeader { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 /*BI_RGB*/ } };
                IntPtr bitsPtr;
                hbmColor = CreateDIBSection(IntPtr.Zero, ref bmi, 0 /*DIB_RGB_COLORS*/, out bitsPtr, IntPtr.Zero, 0);
                if (hbmColor == IntPtr.Zero) return IntPtr.Zero;
                unsafe
                {
                    var bits = (uint*)bitsPtr;
                    for (var y = 0; y < h; y++)
                    {
                        for (var x = 0; x < w; x++)
                        {
                            var c = bmp.GetPixel(x, y);
                            // BI_RGB DIBSection 期望 BGRA(x86 小端:低字节=B),不预乘
                            bits[y * w + x] = (uint)((c.A << 24) | (c.B << 16) | (c.G << 8) | c.R);
                        }
                    }
                }
                // AND mask:全 0(alpha 通道负责透明)
                hbmMask = CreateBitmap(w, h, 1, 1, null);
                var info = new IconInfo { fIcon = true, hbmColor = hbmColor, hbmMask = hbmMask };
                hIcon = CreateIconIndirect(ref info);
                return hIcon;
            }
            finally
            {
                // CreateIconIndirect 会拷贝位图,这里可安全释放源位图
                if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
                if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
            }
        }
        catch (Exception ex)
        {
            Log($"BitmapToAlphaIcon EX: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero && _subclassProc is not null)
            RemoveWindowSubclass(_hwnd, _subclassProc, ThbBhId);
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        if (_taskbar != IntPtr.Zero) { try { Marshal.Release(_taskbar); } catch { } _taskbar = IntPtr.Zero; }
        foreach (var icon in new[] { _iconPrev, _iconPlay, _iconPause, _iconNext })
            if (icon != IntPtr.Zero) DestroyIcon(icon);
        _initialized = false;
    }

    // ---- P/Invoke ----
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr hwnd, WndProcDelegate proc, uint uIdSubclass, IntPtr dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, WndProcDelegate proc, uint uIdSubclass);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct InitCommonControlsExStruct
    {
        public int dwSize;
        public uint dwICC;
    }

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref InitCommonControlsExStruct lpInitCtrls);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_Create(int cx, int cy, uint flags, int cInitial, int cGrow);
    [DllImport("comctl32.dll")] private static extern int ImageList_Add(IntPtr himl, IntPtr hbmImage, IntPtr hbmMask);
    [DllImport("comctl32.dll")] private static extern int ImageList_AddIcon(IntPtr himl, IntPtr hicon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, byte[]? lpvBits);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);
}
#endif
