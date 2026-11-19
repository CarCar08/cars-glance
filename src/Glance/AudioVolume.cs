// Per-app volume (Windows' audio sessions, the same thing the Volume Mixer changes): scrolling over the cover
// changes only the player's volume. The player is found by its media ID: a session whose process (or one of
// its parents: browser-engine apps like Cider play sound from a helper process) runs the player's program.
using System.Runtime.InteropServices;
using System.Text;

namespace Glance;

static class AppVolume
{
    /// <summary>Changes the player's volume by <paramref name="delta"/> (0..1 scale). Returns the new volume,
    /// or null when no audio session belongs to that player.</summary>
    public static float? Change(string appId, float delta)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        string key = Key(appId);
        object deviceEnum = null;
        try
        {
            deviceEnum = new MMDeviceEnumerator();
            var enumerator = (IMMDeviceEnumerator)deviceEnum;
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out IMMDevice device) != 0) return null;
            Guid iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) != 0) return null;
            var manager = (IAudioSessionManager2)o;
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0) return null;
            sessions.GetCount(out int count);
            Dictionary<uint, uint> parents = Parents();
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out IAudioSessionControl2 control) != 0 || control == null) continue;
                control.GetSessionInstanceIdentifier(out string id);
                control.GetProcessId(out uint pid);
                if (!Belongs(id, pid, key, parents)) continue;
                var volume = (ISimpleAudioVolume)control;
                volume.GetMasterVolume(out float v);
                float nv = Math.Clamp(v + delta, 0f, 1f);
                Guid none = Guid.Empty;
                volume.SetMasterVolume(nv, ref none);
                return nv;
            }
        }
        catch (Exception ex) { Log.Write("volume: " + ex.Message); }
        finally { if (deviceEnum != null) Marshal.ReleaseComObject(deviceEnum); }
        return null;
    }

    // "CiderCollective.Cider_a6qxe093bx5xj!App" -> "CiderCollective.Cider"; "Spotify.exe" -> "Spotify".
    internal static string Key(string appId)
    {
        string a = appId.Split('!')[0];
        int u = a.IndexOf('_');
        if (u > 0) a = a[..u];
        if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a[..^4];
        return a;
    }

    static bool Belongs(string sessionId, uint pid, string key, Dictionary<uint, uint> parents)
    {
        if (!string.IsNullOrEmpty(sessionId) && sessionId.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
        for (int depth = 0; depth < 4 && pid != 0; depth++)
        {
            string path = ImagePath(pid);
            if (path != null && path.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
            if (!parents.TryGetValue(pid, out pid)) break;
        }
        return false;
    }

    static string ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static Dictionary<uint, uint> Parents()
    {
        var map = new Dictionary<uint, uint>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
                do { map[e.th32ProcessID] = e.th32ParentProcessID; } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    // ---- interop

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notifications);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
    }
}
