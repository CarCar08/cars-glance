using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Glance.Setup;

namespace Glance.SetupApp
{
    internal enum Stage { Welcome, ConfirmUninstall, Working, Done }

    internal sealed class RelayCommand : ICommand
    {
        private readonly Action _run;
        private readonly Func<bool>? _can;

        public RelayCommand(Action run, Func<bool>? can = null)
        {
            _run = run;
            _can = can;
        }

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;
        public void Execute(object? parameter) => _run();
        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the wizard window shows. The words live here, so they're easy to find and to review.</summary>
    internal sealed class WizardViewModel : INotifyPropertyChanged
    {
        private readonly SetupController _setup;
        private readonly Func<bool> _canClose;
        private readonly StringBuilder _log = new StringBuilder();
        private Intent _intent;
        private bool _ok;

        public event PropertyChangedEventHandler? PropertyChanged;

        public WizardViewModel(SetupController setup, Action requestClose, Func<bool> canClose)
        {
            _setup = setup;
            _canClose = canClose;
            _setup.Progress += (p, t) => Ui(() => { Percent = p; Step = t; });
            _setup.LogLine += t => Ui(() => { _log.AppendLine(t); Raise(nameof(LogText)); });
            var installed = setup.Installed();
            StartWithWindows = installed.IsInstalled ? setup.StartWithWindowsNow() : true;
            LaunchWhenDone = true;
            RemoveUserData = false;

            Primary = new RelayCommand(() => _ = RunPrimaryAsync(), () => PrimaryEnabled);
            Secondary = new RelayCommand(() => _ = RunSecondaryAsync());
            CloseCommand = new RelayCommand(requestClose, () => _canClose());
            LaunchCommand = new RelayCommand(() => { _setup.Launch(); requestClose(); });
            ShowLogCommand = new RelayCommand(() => { try { Process.Start(new ProcessStartInfo(_setup.LogFile) { UseShellExecute = true })?.Dispose(); } catch (Win32Exception) { } });
            CopyDetailsCommand = new RelayCommand(() => { try { Clipboard.SetText(_log.ToString()); CopyHint = "Copied."; Raise(nameof(CopyHint)); } catch (System.Runtime.InteropServices.ExternalException) { } });
            ShowIntro(installed);
        }

        public ICommand Primary { get; }
        public ICommand Secondary { get; }
        public ICommand CloseCommand { get; }
        public ICommand LaunchCommand { get; }
        public ICommand ShowLogCommand { get; }
        public ICommand CopyDetailsCommand { get; }
        public string? CopyHint { get; private set; }

        // ---------- what's on screen ----------

        private Stage _stage;
        public Stage Stage
        {
            get => _stage;
            private set { _stage = value; RaiseAll(); }
        }

        public bool IsWelcome => Stage == Stage.Welcome;
        public bool IsConfirm => Stage == Stage.ConfirmUninstall;
        public bool IsWorking => Stage == Stage.Working;
        public bool IsDone => Stage == Stage.Done;
        public bool ShowFooterButtons => Stage != Stage.Working;

        public string Headline { get; private set; } = "";
        public string Subline { get; private set; } = "";
        public string Body { get; private set; } = "";
        public string Fine { get; private set; } = "";
        public string PrimaryText { get; private set; } = "Install";
        public bool PrimaryEnabled { get; private set; } = true;
        public string? SecondaryText { get; private set; }
        public bool HasSecondary => SecondaryText != null;
        public bool HasSubline => !string.IsNullOrEmpty(Subline);
        public bool HasSecondaryOnWelcome => IsWelcome && HasSecondary;
        public bool ShowPrimary => !IsDone;
        public string CloseText { get; private set; } = "Cancel";
        public bool ShowStartOption { get; private set; } = true;
        public bool ShowLaunchOption { get; private set; } = true;
        public string StartOptionText => "Start " + Product.Name + " when I sign in";
        public string LaunchOptionText => "Open " + Product.Name + " when Setup is finished";
        public string LaunchButtonText => "Open " + Product.Name;

        private bool _startWithWindows, _launchWhenDone, _removeUser;
        public bool StartWithWindows { get => _startWithWindows; set { _startWithWindows = value; Raise(); } }
        public bool LaunchWhenDone { get => _launchWhenDone; set { _launchWhenDone = value; Raise(); } }
        public bool RemoveUserData { get => _removeUser; set { _removeUser = value; Raise(); } }

        private int _percent;
        public int Percent { get => _percent; private set { _percent = value; Raise(); } }
        private string _step = "";
        public string Step { get => _step; private set { _step = value; Raise(); } }
        public string LogText => _log.ToString();

        public bool ResultOk => _ok;
        public string ResultTitle { get; private set; } = "";
        public string ResultText { get; private set; } = "";
        public bool ShowLaunchButton { get; private set; }

        // ---------- stages ----------

        private void ShowIntro(InstalledInfo installed)
        {
            var mine = _setup.ThisVersion;
            if (!_setup.HasPayload && !_setup.Layout.IsSandbox && !installed.IsInstalled)
            {
                _intent = Intent.Install;
                Headline = "This Setup file is empty";
                Subline = "It was built without the app inside it";
                Body = "Download " + Product.SetupFileName.Replace(".exe", "") + " from the project's Releases page (" + Product.RepositoryUrl.Replace("https://", "") + "/releases) and run that one.";
                PrimaryText = "Install";
                PrimaryEnabled = false;
                SecondaryText = null;
                CloseText = "Close";
                ShowStartOption = ShowLaunchOption = false;
                Stage = Stage.Welcome;
                return;
            }

            if (!installed.IsInstalled)
            {
                _intent = Intent.Install;
                Headline = "Install " + Product.Name;
                Subline = "Version " + mine;
                Body = "A frosted-glass panel in the middle of your desktop: the time and weather, what's playing, your calendar and tasks, " +
                       "and countdowns. It sits under your windows, never takes focus and pauses its animations while a game is full screen.\n\n" +
                       "It installs just for you into " + _setup.Layout.InstallDir + " and adds Start menu entries. No administrator approval needed.";
                Fine = "Nothing else is added: no services, no browser extensions. A small task restarts Glance if it ever stops unexpectedly.";
                PrimaryText = "Install";
                SecondaryText = null;
                CloseText = "Cancel";
                ShowStartOption = ShowLaunchOption = true;
            }
            else if (installed.Version!.CompareTo(mine) < 0)
            {
                _intent = Intent.Update;
                Headline = "Update " + Product.Name;
                Subline = installed.Version + "  →  " + mine;
                Body = "Your settings and saved sign-ins are kept. The widget disappears for a moment while its files are replaced, and comes back afterwards.";
                Fine = "If anything goes wrong the version you have now is put back.";
                PrimaryText = "Update";
                SecondaryText = "Uninstall…";
                CloseText = "Cancel";
                ShowStartOption = ShowLaunchOption = true;
            }
            else if (installed.Version.CompareTo(mine) == 0)
            {
                _intent = Intent.Repair;
                Headline = Product.Name + " " + mine + " is installed";
                Subline = "Installed in " + (installed.Location ?? _setup.Layout.InstallDir);
                Body = "Repair puts every file back exactly as shipped (your settings aren't touched). Uninstall removes the program.";
                PrimaryText = "Repair";
                SecondaryText = "Uninstall…";
                CloseText = "Close";
                ShowStartOption = ShowLaunchOption = true;
            }
            else
            {
                _intent = Intent.Reinstall;
                Headline = "A newer " + Product.Name + " is installed";
                Subline = installed.Version + " is installed; this Setup has " + mine;
                Body = "Installing this older version replaces it. Your settings are kept, but a newer version may have changed them in ways an older one doesn't understand.";
                PrimaryText = "Install " + mine + " anyway";
                SecondaryText = "Uninstall…";
                CloseText = "Close";
                ShowStartOption = ShowLaunchOption = true;
            }
            PrimaryEnabled = true;
            Stage = Stage.Welcome;
            RaiseAll();
        }

        public void StartAutomatically(SetupMode mode)
        {
            switch (mode)
            {
                case SetupMode.Install:
                case SetupMode.Update:
                case SetupMode.Repair:
                    _ = RunAsync(_setup.Installed().IsInstalled ? (mode == SetupMode.Repair ? Intent.Repair : Intent.Update) : Intent.Install);
                    break;
                case SetupMode.Uninstall:
                    ShowUninstallConfirm();
                    break;
            }
        }

        private void ShowUninstallConfirm()
        {
            Headline = "Uninstall " + Product.Name;
            Subline = "";
            Body = "This removes the widget from your desktop, its Start menu entries, its Installed apps entry, sign-in start and its keep-running task.";
            Fine = "Your settings stay in " + _setup.Layout.UserData + " and your saved sign-ins (like Todoist) stay in Windows Credential Manager, unless you tick the box below.";
            PrimaryText = "Uninstall";
            SecondaryText = null;
            CloseText = "Cancel";
            PrimaryEnabled = true;
            _intent = Intent.Uninstall;
            Stage = Stage.ConfirmUninstall;
            RaiseAll();
        }

        private async Task RunPrimaryAsync()
        {
            if (Stage == Stage.ConfirmUninstall) await RunAsync(Intent.Uninstall);
            else await RunAsync(_intent);
        }

        private Task RunSecondaryAsync()
        {
            ShowUninstallConfirm();
            return Task.CompletedTask;
        }

        private async Task RunAsync(Intent intent)
        {
            _intent = intent;
            Stage = Stage.Working;
            Percent = 0;
            Step = "Starting";
            RaiseAll();
            InstallResult result;
            try
            {
                result = await _setup.RunAsync(intent, new Options
                {
                    StartWithWindows = StartWithWindows,
                    LaunchWhenDone = LaunchWhenDone,
                    RemoveUserData = RemoveUserData,
                });
            }
            catch (Exception ex)
            {
                // Never leave the window on a step that stopped: an error here would vanish and the wizard would
                // refuse to close because it still thinks it's working.
                result = new InstallResult { Ok = false, Message = "Setup hit an unexpected problem: " + ex.Message + " Nothing after that step was done; you can run Setup again." };
                try { System.IO.File.AppendAllText(_setup.LogFile, "FAILED: " + ex + Environment.NewLine); }
                catch (System.IO.IOException) { }
            }
            _ok = result.Ok;
            if (result.Ok)
            {
                ResultTitle = intent == Intent.Uninstall ? Product.Name + " was removed"
                    : intent == Intent.Update ? "Updated to " + _setup.ThisVersion
                    : intent == Intent.Repair ? "Repaired"
                    : Product.Name + " is installed";
                ResultText = intent == Intent.Uninstall ? "Thanks for trying it."
                    : LaunchWhenDone ? "It's on your desktop now (show the desktop with Windows + D). Scroll over it or click the dots to switch views; right-click it for options."
                    : "Start it from the Start menu: \"" + Product.Name + "\".";
                ShowLaunchButton = intent != Intent.Uninstall && !LaunchWhenDone;
                CloseText = "Close";
            }
            else
            {
                ResultTitle = "Setup couldn't finish";
                ResultText = result.Message;
                ShowLaunchButton = false;
                CloseText = "Close";
            }
            Stage = Stage.Done;
            RaiseAll();
        }

        // ---------- plumbing ----------

        private static void Ui(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
        }

        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
