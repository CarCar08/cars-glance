using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Glance.Setup;
using Microsoft.Win32;

namespace Glance.SetupApp
{
    internal enum Intent { Install, Update, Repair, Reinstall, Uninstall }

    internal sealed class Options
    {
        public bool StartWithWindows { get; set; }
        public bool LaunchWhenDone { get; set; }
        public bool RemoveUserData { get; set; }
    }

    internal sealed class InstalledInfo
    {
        public SemVer? Version { get; set; }
        public string? Location { get; set; }
        public bool IsInstalled => Version != null;
    }

    /// <summary>Everything Setup does, with no window: the wizard and --silent both drive this. Glance installs for
    /// you only, so all of it runs as you: no administrator prompt, ever.</summary>
    internal sealed class SetupController
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public InstallLayout Layout { get; }
        public SetupCommandLine Cmd { get; }
        public PayloadManifest? Manifest { get; }
        public string LogFile { get; } = Path.Combine(Path.GetTempPath(), Product.SetupLogName);

        public event Action<int, string>? Progress;
        public event Action<string>? LogLine;

        public SetupController(SetupCommandLine cmd)
        {
            Cmd = cmd;
            Layout = new InstallLayout(cmd.Sandbox);
            Manifest = Embedded.Manifest();
            try { File.WriteAllText(LogFile, Product.Name + " Setup " + Embedded.SetupVersion + " - " + DateTime.Now + Environment.NewLine); } catch (IOException) { }
        }

        public bool HasPayload => Embedded.HasPayload && Manifest != null;
        public SemVer ThisVersion => SemVer.TryParse(Manifest?.Version ?? Embedded.SetupVersion, out var v) ? v : new SemVer(0, 0, 0);

        public void Log(string text)
        {
            try { File.AppendAllText(LogFile, text + Environment.NewLine); } catch (IOException) { }
            LogLine?.Invoke(text);
        }

        public InstalledInfo Installed()
        {
            var info = new InstalledInfo();
            if (Layout.IsSandbox)
            {
                if (InstallEngine.InstalledVersionFromSandbox(Layout) is { } sv && SemVer.TryParse(sv, out var v)) { info.Version = v; info.Location = Layout.InstallDir; }
                return info;
            }
            using (var key = Registry.CurrentUser.OpenSubKey(InstallLayout.ArpKey))
            {
                if (key?.GetValue("DisplayVersion") is string text && SemVer.TryParse(text, out var v))
                {
                    info.Version = v;
                    info.Location = key.GetValue("InstallLocation") as string ?? Layout.InstallDir;
                }
            }
            return info;
        }

        public bool StartWithWindowsNow()
        {
            if (Layout.IsSandbox) return File.Exists(Path.Combine(Layout.Sandbox!, "run-at-sign-in.txt"));
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue(Product.RunValue) is string;
        }

        // ---------- the work ----------

        public Task<InstallResult> RunAsync(Intent intent, Options options, CancellationToken cancel = default) =>
            Task.Run(() => intent == Intent.Uninstall ? Uninstall(options) : Install(options), cancel);

        private ISystemOps Ops() => Layout.IsSandbox
            ? new SandboxOps(Layout, Process.GetCurrentProcess().MainModule!.FileName)
            : new WindowsOps(Layout);

        private InstallResult Install(Options options)
        {
            if (!HasPayload) return Fail("This Setup file has no app inside it (it was built without one). Download " + Product.SetupFileName.Replace(".exe", "") + " from " + Product.RepositoryUrl + "/releases.");
            WaitForCaller();
            Report(1, "Starting");
            Log("Installing " + Product.Name + " " + Manifest!.Version + (Layout.IsSandbox ? " into the sandbox " + Layout.Sandbox : " into " + Layout.InstallDir));
            AskAppToQuit();

            var result = new InstallEngine(Layout, Manifest, Embedded.OpenPayload, Ops()).Install(options.StartWithWindows, Report, Log);
            if (!result.Ok) return Fail(result.Message);
            if (options.LaunchWhenDone && !Layout.IsSandbox) Launch();
            Log(result.Message);
            return result;
        }

        private InstallResult Uninstall(Options options)
        {
            Report(1, "Starting");
            Log("Uninstalling " + Product.Name);
            AskAppToQuit();
            var result = new InstallEngine(Layout, Manifest ?? new PayloadManifest(), Embedded.OpenPayload, Ops()).Uninstall(options.RemoveUserData, Report, Log);
            if (!result.Ok) return Fail(result.Message);
            Report(100, "Done");
            return result;
        }

        private InstallResult Fail(string message)
        {
            Log("FAILED: " + message);
            return new InstallResult { Ok = false, Message = message };
        }

        private void Report(int percent, string text)
        {
            Log("[" + percent + "%] " + text);
            Progress?.Invoke(percent, text);
            if (Layout.IsSandbox && Environment.GetEnvironmentVariable("GLANCE_SETUP_SLOW") == "1") Thread.Sleep(150); // screenshots
        }

        /// <summary>The app's own "Update now" passes its process ID: wait for it to finish exiting.</summary>
        private void WaitForCaller()
        {
            if (Cmd.WaitForPid == null || !int.TryParse(Cmd.WaitForPid, out var pid)) return;
            try { using (var p = Process.GetProcessById(pid)) p.WaitForExit(15000); }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { }
        }

        /// <summary>Asks the running widget to exit cleanly (its --quit signal) and waits a few seconds;
        /// <see cref="WindowsOps.StopApp"/> closes whatever is left.</summary>
        private void AskAppToQuit()
        {
            if (Layout.IsSandbox) return;
            var location = Installed().Location ?? Layout.InstallDir;
            var exe = Path.Combine(location, Product.MainExe);
            if (WindowsOps.RunningFrom(location).Count == 0 || !File.Exists(exe)) return;
            Report(3, "Closing " + Product.Name);
            try { using (Process.Start(new ProcessStartInfo(exe, "--quit --for-update") { UseShellExecute = false, CreateNoWindow = true })!) { } }
            catch (Exception ex) { Log("Couldn't ask the app to close: " + ex.Message); }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && WindowsOps.RunningFrom(location).Count > 0) Thread.Sleep(200);
        }

        /// <summary>Through Explorer, so the app starts as a normal desktop program (not as Setup's child).</summary>
        public void Launch()
        {
            var exe = Layout.MainExe(Manifest ?? new PayloadManifest());
            if (!File.Exists(exe)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception ex) { Log("Couldn't start the app: " + ex.Message); }
        }
    }
}
