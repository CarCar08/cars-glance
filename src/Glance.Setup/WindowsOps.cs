using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Text;
using Glance.Setup;
using Microsoft.Win32;

namespace Glance.SetupApp
{
    /// <summary>The real thing: what Setup does to Windows, all of it for the current user only (no admin).</summary>
    internal sealed class WindowsOps : ISystemOps
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private readonly InstallLayout _layout;

        public WindowsOps(InstallLayout layout) => _layout = layout;

        public string SelfPath => Process.GetCurrentProcess().MainModule!.FileName;

        // ---------- the running app ----------

        /// <summary>Ends copies of Glance still running from the install folder (Setup already asked them to quit);
        /// a development build somewhere else is left alone.</summary>
        public void StopApp(Action<string> log)
        {
            foreach (var id in RunningFrom(_layout.InstallDir))
            {
                try
                {
                    using (var p = Process.GetProcessById(id))
                    {
                        log("Closing " + Product.ProcessName + " (process " + id + ")");
                        if (!p.WaitForExit(3000)) p.Kill();
                        p.WaitForExit(8000);
                    }
                }
                catch (ArgumentException) { /* gone */ }
                catch (Win32Exception) { /* access denied or already gone */ }
                catch (InvalidOperationException) { /* exited meanwhile */ }
            }
        }

        public static List<int> RunningFrom(string location)
        {
            var prefix = location.TrimEnd('\\') + "\\";
            var ids = new List<int>();
            foreach (var p in Process.GetProcessesByName(Product.ProcessName))
            {
                // A process can exit while it's being looked at: .NET Framework then throws anything from Win32Exception
                // to a NullReferenceException inside ProcessModule.FileName.
                try { if (p.MainModule?.FileName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true) ids.Add(p.Id); }
                catch (Exception) { /* gone, or not ours to inspect */ }
                finally { p.Dispose(); }
            }
            return ids;
        }

        // ---------- Installed apps, Start menu, sign-in ----------

        public void WriteArp(IDictionary<string, object> values)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(InstallLayout.ArpKey, true)!)
            {
                foreach (var kv in values)
                {
                    if (kv.Value is int i) key.SetValue(kv.Key, i, RegistryValueKind.DWord);
                    else key.SetValue(kv.Key, Convert.ToString(kv.Value) ?? "", RegistryValueKind.String);
                }
            }
        }

        public void RemoveArp() => Registry.CurrentUser.DeleteSubKeyTree(InstallLayout.ArpKey, throwOnMissingSubKey: false);

        public void CreateShortcut(string path, string target, string arguments, string workingDir, string description, string? appUserModelId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var link = (IShellLinkW)new CShellLink();
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            if (appUserModelId != null)
            {
                // System.AppUserModel.ID: Windows attributes the app's notifications to this shortcut.
                var store = (IPropertyStore)link;
                var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                var value = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(appUserModelId) };
                try
                {
                    store.SetValue(ref key, ref value);
                    store.Commit();
                }
                finally { Marshal.FreeCoTaskMem(value.p); }
            }
            ((IPersistFile)link).Save(path, true);
            Marshal.ReleaseComObject(link);
        }

        public void RemoveShortcut(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public void SetRunAtSignIn(bool on, string exe)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)!)
            {
                if (on) key.SetValue(Product.RunValue, "\"" + exe + "\" --autostart", RegistryValueKind.String);
                else key.DeleteValue(Product.RunValue, throwOnMissingValue: false);
            }
        }

        /// <summary>A per-user task every 5 minutes that runs "CarsGlance.exe --ensure-running": it starts the widget
        /// only if it isn't running and you didn't quit it yourself, then exits. Works on battery too.</summary>
        public void SetWatchdog(bool on, string exe, Action<string> log)
        {
            if (!on)
            {
                Schtasks("/Delete /TN \"" + Product.WatchdogTask + "\" /F", log, quietIfMissing: true);
                return;
            }
            var xml = Path.Combine(Path.GetTempPath(), "CarsGlance-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(xml, WatchdogXml(exe), Encoding.Unicode);
                Schtasks("/Create /TN \"" + Product.WatchdogTask + "\" /XML \"" + xml + "\" /F", log, quietIfMissing: false);
            }
            finally { try { File.Delete(xml); } catch (IOException) { } }
        }

        internal static string WatchdogXml(string exe)
        {
            string X(string s) => SecurityElement.Escape(s) ?? "";
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <RegistrationInfo><Description>" + X("Starts " + Product.Name + " again if it stopped without being told to.") + "</Description></RegistrationInfo>\n" +
                "  <Triggers><TimeTrigger><Repetition><Interval>PT5M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
                "<StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>\n" +
                "  <Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle>" +
                "<ExecutionTimeLimit>PT1M</ExecutionTimeLimit><Priority>7</Priority></Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + X(exe) + "</Command><Arguments>--ensure-running</Arguments></Exec></Actions>\n" +
                "</Task>\n";
        }

        private static void Schtasks(string args, Action<string> log, bool quietIfMissing)
        {
            var exe = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
            {
                var err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0 && !quietIfMissing) log("The keep-running task couldn't be set (" + err.Trim() + "). Glance still works; it just won't restart itself.");
            }
        }

        // ---------- saved sign-ins ----------

        public void ForgetCredentials(Action<string> log)
        {
            if (!CredEnumerate(Product.CredentialPrefix + "*", 0, out var count, out var list)) return;
            try
            {
                var targets = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var cred = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(cred, 8)); // CREDENTIALW.TargetName
                    if (name != null) targets.Add(name);
                }
                foreach (var t in targets)
                    log(CredDelete(t, 1, 0) ? "Removed the saved sign-in " + t : "Couldn't remove " + t);
            }
            finally { CredFree(list); }
        }

        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        // ---------- shell link COM ----------

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int show);
            void SetShowCmd(int show);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
        }
    }
}
