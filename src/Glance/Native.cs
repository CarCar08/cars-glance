using System.Drawing;
using System.Runtime.InteropServices;

namespace Glance;

static class Native
{
    public const uint WS_POPUP = 0x80000000;
    public const int WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x00000008,
                     WS_EX_TRANSPARENT = 0x00000020;
    const uint LWA_ALPHA = 0x2;
    public const int WM_MOUSEACTIVATE = 0x0021, WM_SETCURSOR = 0x0020, WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202,
                     WM_MOUSEWHEEL = 0x020A, WM_MOUSELEAVE = 0x02A3, WM_DPICHANGED = 0x02E0;
    public const int MA_NOACTIVATE = 3;
    public static readonly IntPtr IDC_HAND = new(32649), IDC_ARROW = new(32512);
    public const int WM_RBUTTONUP = 0x0205;
    public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
                      SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UPDATELAYEREDWINDOWINFO
    {
        public int cbSize;
        public IntPtr hdcDst, pptDst, psize, hdcSrc, pptSrc;
        public int crKey;
        public IntPtr pblend;
        public int dwFlags;
        public IntPtr prcDirty;
    }

    [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] struct WinCompAttrData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>One display: its whole area, the part apps use (without the taskbar), and Windows' name for it.</summary>
    public readonly record struct Display(Rectangle Bounds, Rectangle Work, string Name, bool Primary);

    public static List<Display> Displays()
    {
        var list = new List<Display>();
        MonitorEnumProc cb = (IntPtr m, IntPtr h, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            Rectangle bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (GetMonitorInfo(m, ref mi))
                list.Add(new Display(bounds, Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom), mi.szDevice ?? "", (mi.dwFlags & 1) != 0));
            else list.Add(new Display(bounds, bounds, "", false));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list;
    }

    /// <summary>Windows Settings > Accessibility > Visual effects > Animation effects.</summary>
    public static bool WindowsAnimationsOn() => !SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out bool on, 0) || on;

    /// <summary>Battery saver (or Energy saver) is on.</summary>
    public static bool BatterySaverOn() => GetSystemPowerStatus(out SYSTEM_POWER_STATUS p) && p.SystemStatusFlag == 1;

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UPDATELAYEREDWINDOWINFO info);
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WinCompAttrData data);
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static List<Rectangle> Monitors()
    {
        var list = new List<Rectangle>();
        MonitorEnumProc cb = (IntPtr m, IntPtr h, ref RECT r, IntPtr d) => { list.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)); return true; };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list;
    }

    // Windows blurs whatever is behind the window (accent policy 3 = blur-behind); works while inactive.
    public static void EnableBlurBehind(IntPtr hwnd)
    {
        var policy = new AccentPolicy { AccentState = 3 };
        int size = Marshal.SizeOf(policy);
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, p, false);
            var d = new WinCompAttrData { Attribute = 19, Data = p, SizeOfData = size }; // WCA_ACCENT_POLICY
            SetWindowCompositionAttribute(hwnd, ref d);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // Windows 11 rounded corners (they clip the blur too), without the system's 1px border:
    // the panel draws its own fine translucent edge.
    public static void SetRoundedCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, 34, ref none, 4);
    }

    public static void TrackLeave(IntPtr hwnd)
    {
        var e = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = hwnd };
        TrackMouseEvent(ref e);
    }

    public static bool IsTopmost(IntPtr hwnd) => (GetWindowLong(hwnd, -20) & WS_EX_TOPMOST) != 0;

    public static bool IsOnScreen(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return false;
        int ex = GetWindowLong(hwnd, -20);
        bool hasAlpha = GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) && (flags & LWA_ALPHA) != 0;
        return !IsSeeThrough(ex, hasAlpha ? alpha : (byte?)null);
    }

    // Invisible overlays that sit over a whole screen without hiding anything, like NVIDIA's
    // "GeForce Overlay" (layered + click-through, alpha 0). Counting it as a cover stopped the
    // animations on the main monitor (2026-11-16).
    public static bool IsSeeThrough(int exStyle, byte? layeredAlpha)
    {
        bool layered = (exStyle & WS_EX_LAYERED) != 0;
        if (layered && layeredAlpha == 0) return true;
        return layered && (exStyle & WS_EX_TRANSPARENT) != 0;
    }

    public static bool Intersects(IntPtr hwnd, RECT ours)
    {
        if (!GetWindowRect(hwnd, out RECT r) || r.Right <= r.Left || r.Bottom <= r.Top) return false;
        return r.Left < ours.Right && ours.Left < r.Right && r.Top < ours.Bottom && ours.Top < r.Bottom;
    }

    public static bool ContainsRect(IntPtr hwnd, RECT ours) =>
        GetWindowRect(hwnd, out RECT r) && r.Left <= ours.Left && r.Top <= ours.Top && r.Right >= ours.Right && r.Bottom >= ours.Bottom;
}
