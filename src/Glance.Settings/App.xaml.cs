using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Glance.SettingsApp;

public partial class App : Application
{
    const string MutexName = "Local\\CarsGlance.SettingsWindow";
    internal const string WindowTitle = "C.A.R.S. Glance Settings";
    Mutex single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, args) =>
        {
            args.Handled = true;
            Log.Write("settings window: " + args.Exception);
            System.Windows.MessageBox.Show("Settings hit an unexpected problem:\n\n" + args.Exception.Message + "\n\nDetails are in the log (Settings > About).",
                WindowTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        };
        base.OnStartup(e);
        string page = e.Args.Length > 0 ? e.Args[0].Trim().ToLowerInvariant() : "general";

        // --render <page> <png> [width height]: draws a page to an image without showing any window (for checks
        // and screenshots while the PC is busy).
        if (page == "--render" && e.Args.Length >= 3)
        {
            Settings rs = Settings.Load() ?? Settings.Parse("{}");
            ApplyTheme(rs);
            int w = e.Args.Length > 3 ? int.Parse(e.Args[3]) : 1000, h = e.Args.Length > 4 ? int.Parse(e.Args[4]) : 900;
            Glance.SettingsApp.MainWindow.RenderTo(rs, e.Args[1], e.Args[2], w, h);
            Shutdown(0);
            return;
        }

        // One Settings window: opening it again brings the open one to the front.
        single = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            IntPtr hwnd = FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
                SetForegroundWindow(hwnd);
            }
            Shutdown(0);
            return;
        }

        // Dark glass with the accent the widget uses (the accent is applied before and after the theme:
        // reloading a theme copies whichever accent is current at that moment).
        Settings settings = Settings.Load() ?? Settings.Parse("{}");
        ApplyTheme(settings);

        var window = new MainWindow(settings, page);
        MainWindow = window;
        window.Show();
    }

    static void ApplyTheme(Settings settings)
    {
        var c = settings.Appearance.AccentColor;
        var accent = Color.FromRgb(c.R, c.G, c.B);
        ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: false);
        ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark);
        Current.Resources["GlanceAccentBrush"] = new SolidColorBrush(accent);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { single?.ReleaseMutex(); } catch (ApplicationException) { }
        single?.Dispose();
        base.OnExit(e);
    }
}
