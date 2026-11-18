using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Glance.Setup
{
    /// <summary>Everything the installer does to Windows itself (registry, shortcuts, the scheduled task, running
    /// processes). All of it is per user, so nothing needs administrator rights. The real version lives in the
    /// setup app; the sandbox version below does the same into a folder, so the whole flow can be tested.</summary>
    public interface ISystemOps
    {
        /// <summary>The setup program's own path (copied into the install folder as the uninstaller).</summary>
        string SelfPath { get; }

        /// <summary>Closes Glance (politely first), but only copies running from the install folder.</summary>
        void StopApp(Action<string> log);

        void WriteArp(IDictionary<string, object> values);
        void RemoveArp();

        /// <summary>A Start menu shortcut carrying the app's AppUserModelID, so its notifications are attributed to it.</summary>
        void CreateShortcut(string path, string target, string arguments, string workingDir, string description, string? appUserModelId);
        void RemoveShortcut(string path);

        /// <summary>Start at sign-in (HKCU Run).</summary>
        void SetRunAtSignIn(bool on, string exe);

        /// <summary>A per-user scheduled task that starts Glance again if it ever stops without being told to.</summary>
        void SetWatchdog(bool on, string exe, Action<string> log);

        /// <summary>Deletes the app's saved sign-ins (Todoist token and the like) from Windows Credential Manager.</summary>
        void ForgetCredentials(Action<string> log);
    }

    public sealed class InstallResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public bool Upgraded { get; set; }
    }

    /// <summary>
    /// Unpack the payload next to the install folder, check every file against the manifest, swap the folders
    /// (with rollback), then register the app: Start menu, Installed apps entry, sign-in start and the
    /// keep-running task.
    /// </summary>
    public sealed class InstallEngine
    {
        private readonly InstallLayout _layout;
        private readonly PayloadManifest _manifest;
        private readonly Func<Stream> _openPayload;
        private readonly ISystemOps _ops;

        public InstallEngine(InstallLayout layout, PayloadManifest manifest, Func<Stream> openPayload, ISystemOps ops)
        {
            _layout = layout;
            _manifest = manifest;
            _openPayload = openPayload;
            _ops = ops;
        }

        public InstallResult Install(bool startAtSignIn, Action<int, string> progress, Action<string> log)
        {
            var upgraded = false;
            try
            {
                progress(4, "Preparing");
                FolderSwap.CleanLeftovers(_layout);
                Directory.CreateDirectory(_layout.ProgramsDir);

                progress(8, "Unpacking " + Product.Name + " " + _manifest.Version);
                using (var zip = _openPayload())
                    SafeZip.Extract(zip, _layout.NewDir, (p, name) => progress(8 + p * 52 / 100, "Unpacking " + name));

                progress(62, "Adding the uninstaller");
                File.Copy(_ops.SelfPath, Path.Combine(_layout.NewDir, Product.SetupFileName), overwrite: true);

                progress(66, "Checking every file");
                var problems = _manifest.Verify(_layout.NewDir).Where(p => p != "Unexpected: " + Product.SetupFileName).ToList();
                if (problems.Count > 0)
                    throw new InvalidDataException("The package is damaged (" + problems.Count + " problem(s)): " + string.Join("; ", problems.Take(3)));

                progress(72, "Installing");
                var exe = _layout.MainExe(_manifest);
                var swap = FolderSwap.Swap(_layout.NewDir, _layout.InstallDir, _layout.OldDir,
                    beforeSwap: () => _ops.StopApp(log),
                    afterSwap: () =>
                    {
                        progress(84, "Adding the Start menu entries and the Installed apps entry");
                        _ops.CreateShortcut(_layout.StartMenuShortcut, exe, "", _layout.InstallDir, Product.Tagline, Product.Aumid);
                        _ops.CreateShortcut(_layout.SettingsShortcut, exe, "--settings", _layout.InstallDir, "Change what " + Product.Name + " shows", null);
                        _ops.CreateShortcut(_layout.TodoistShortcut, exe, "--connect-todoist", _layout.InstallDir, "Paste your Todoist API token so your tasks show in the agenda", null);
                        _ops.WriteArp(ArpEntry.Values(_layout, _manifest, _manifest.TotalSize));
                        progress(90, startAtSignIn ? "Starting with Windows" : "Not starting with Windows");
                        _ops.SetRunAtSignIn(startAtSignIn, exe);
                        _ops.SetWatchdog(startAtSignIn, exe, log);
                    }, log);
                upgraded = swap.PreviousExisted;

                progress(96, "Cleaning up");
                try { FolderSwap.DeleteTree(_layout.OldDir); } catch (IOException ex) { log("Couldn't remove the previous version's folder: " + ex.Message); }
                progress(100, "Done");
                return new InstallResult { Ok = true, Upgraded = upgraded, Message = (upgraded ? "Updated to " : "Installed ") + _manifest.Version };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                log("Install failed: " + ex.Message);
                FolderSwap.CleanLeftovers(_layout);
                var message = ex is IOException && !(ex is FileNotFoundException) && !(ex is DirectoryNotFoundException)
                    ? ex.Message + " A file in " + _layout.InstallDir + " stayed in use for a while (usually antivirus checking the new files, or a window showing that folder). The installed version wasn't changed; run Setup again in a minute."
                    : ex.Message;
                return new InstallResult { Ok = false, Upgraded = upgraded, Message = message };
            }
        }

        /// <summary>Removes the app, its shortcuts, its Installed apps entry, sign-in start and keep-running task.
        /// <paramref name="removeUserData"/> also deletes the settings folder and the saved sign-ins.</summary>
        public InstallResult Uninstall(bool removeUserData, Action<int, string> progress, Action<string> log)
        {
            try
            {
                progress(5, "Closing " + Product.Name);
                _ops.StopApp(log);
                progress(25, "Removing the Start menu entries, the Installed apps entry and sign-in start");
                _ops.SetWatchdog(false, _layout.MainExe(_manifest), log);
                _ops.SetRunAtSignIn(false, _layout.MainExe(_manifest));
                _ops.RemoveArp();
                _ops.RemoveShortcut(_layout.StartMenuShortcut);
                _ops.RemoveShortcut(_layout.SettingsShortcut);
                _ops.RemoveShortcut(_layout.TodoistShortcut);
                progress(50, "Deleting the program files");
                FolderSwap.DeleteTree(_layout.InstallDir);
                FolderSwap.CleanLeftovers(_layout);
                if (removeUserData)
                {
                    progress(85, "Deleting your settings and saved sign-ins");
                    _ops.ForgetCredentials(log);
                    if (Directory.Exists(_layout.UserData)) FolderSwap.DeleteTree(_layout.UserData);
                }
                progress(100, "Done");
                return new InstallResult { Ok = true, Message = Product.Name + " was removed." };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log("Uninstall failed: " + ex.Message);
                return new InstallResult { Ok = false, Message = ex.Message };
            }
        }

        /// <summary>The version the sandbox "Installed apps" file records (tests and --sandbox runs).</summary>
        public static string? InstalledVersionFromSandbox(InstallLayout layout)
        {
            if (!layout.IsSandbox || !File.Exists(layout.SandboxArpFile)) return null;
            using (var doc = JsonDocument.Parse(File.ReadAllText(layout.SandboxArpFile)))
                return doc.RootElement.TryGetProperty("DisplayVersion", out var v) ? v.GetString() : null;
        }
    }

    /// <summary>The same side effects, into a folder (tests, `--sandbox`): arp.json instead of the registry,
    /// a text stand-in instead of a .lnk, marker files for sign-in start and the task, nothing killed.</summary>
    public sealed class SandboxOps : ISystemOps
    {
        private readonly InstallLayout _layout;
        public List<string> Events { get; } = new List<string>();
        public Func<bool>? FailAfterSwap { get; set; }

        public SandboxOps(InstallLayout layout, string selfPath)
        {
            _layout = layout;
            SelfPath = selfPath;
        }

        public string SelfPath { get; }

        public void StopApp(Action<string> log) => Events.Add("stop-app");

        public void WriteArp(IDictionary<string, object> values)
        {
            if (FailAfterSwap != null && FailAfterSwap()) throw new IOException("simulated failure after the swap");
            Directory.CreateDirectory(_layout.Sandbox!);
            File.WriteAllText(_layout.SandboxArpFile, JsonSerializer.Serialize(values));
        }

        public void RemoveArp()
        {
            if (File.Exists(_layout.SandboxArpFile)) File.Delete(_layout.SandboxArpFile);
        }

        public void CreateShortcut(string path, string target, string arguments, string workingDir, string description, string? appUserModelId)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, target + "|" + arguments + "|" + workingDir + "|" + description + "|" + appUserModelId);
        }

        public void RemoveShortcut(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public void SetRunAtSignIn(bool on, string exe) => Marker("run-at-sign-in.txt", on, exe);

        public void SetWatchdog(bool on, string exe, Action<string> log) => Marker("watchdog.txt", on, exe);

        public void ForgetCredentials(Action<string> log) => Events.Add("forget-credentials");

        private void Marker(string name, bool on, string text)
        {
            var file = Path.Combine(_layout.Sandbox!, name);
            Events.Add(name + ":" + on);
            if (on) { Directory.CreateDirectory(_layout.Sandbox!); File.WriteAllText(file, text); }
            else if (File.Exists(file)) File.Delete(file);
        }
    }
}
