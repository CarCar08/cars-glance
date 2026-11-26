// Every page of the Settings window, built in code from a few helpers (a card per option, like Windows Settings).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glance.Setup;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Glance.SettingsApp;

sealed class Pages
{
    readonly Settings s;
    readonly Action changed;
    readonly MainWindow window;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string AppDir => AppContext.BaseDirectory.TrimEnd('\\');
    static string WidgetExe => Path.Combine(AppDir, Product.MainExe);

    public Pages(Settings settings, Action changed, MainWindow window)
    {
        s = settings;
        this.changed = changed;
        this.window = window;
    }

    public UIElement Build(string id) => id switch
    {
        "welcome" => Welcome(),
        "views" => Views(),
        "weather" => Weather(),
        "agenda" => AgendaPage(),
        "music" => Music(),
        "countdowns" => Countdowns(),
        "appearance" => Appearance(),
        "accessibility" => Accessibility(),
        "updates" => Updates(),
        "about" => About(),
        _ => General(),
    };

    // ------------------------------------------------------------------ helpers

    static StackPanel Page(string title, string intro = null)
    {
        var p = new StackPanel { Margin = new Thickness(28, 14, 34, 34), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Stretch };
        p.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, intro == null ? 14 : 6) });
        if (intro != null) p.Children.Add(Note(intro, 14, new Thickness(0, 0, 0, 16)));
        return p;
    }

    static TextBlock Note(string text, double size = 13, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    static TextBlock Section(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 20, 0, 8) };

    static CardControl Card(SymbolRegular icon, string header, string description, UIElement control)
    {
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = header, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (description != null) head.Children.Add(Note(description));
        return new CardControl
        {
            Icon = new SymbolIcon { Symbol = icon },
            Header = head,
            Content = control,
            Margin = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(18, 14, 18, 14),
        };
    }

    // A card whose own content goes underneath (lists, search results, editors).
    static Border Panel(UIElement content)
    {
        var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(0, 0, 0, 5), Child = content, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return b;
    }

    ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = value, OnContent = "On", OffContent = "Off", MinWidth = 90 };
        t.Checked += (o, e) => { set(true); changed(); };
        t.Unchecked += (o, e) => { set(false); changed(); };
        return t;
    }

    ComboBox Choice<T>(IEnumerable<(string Label, T Value)> options, T current, Action<T> set, double width = 200)
    {
        var c = new ComboBox { MinWidth = width };
        int sel = -1, i = 0;
        foreach (var (label, value) in options)
        {
            c.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            if (EqualityComparer<T>.Default.Equals(value, current)) sel = i;
            i++;
        }
        if (sel < 0)
        {
            c.Items.Add(new ComboBoxItem { Content = current?.ToString(), Tag = current });
            sel = c.Items.Count - 1;
        }
        c.SelectedIndex = sel;
        c.SelectionChanged += (o, e) =>
        {
            if (c.SelectedItem is ComboBoxItem item && item.Tag is T v) { set(v); changed(); }
        };
        return c;
    }

    static IEnumerable<(string, int)> Minutes(params int[] values) =>
        values.Select(v => (v == 0 ? "No" : v == 60 ? "1 hour" : v + (v == 1 ? " minute" : " minutes"), v));

    static Button ActionButton(string text, SymbolRegular icon, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Icon = new SymbolIcon { Symbol = icon }, Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (o, e) => click();
        return b;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch (Exception ex) { Log.Write("settings open: " + ex.Message); }
    }

    static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement i in items) p.Children.Add(i);
        return p;
    }

    static void RunWidget(string args)
    {
        try { Process.Start(new ProcessStartInfo(WidgetExe, args) { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Write("settings: couldn't run the widget: " + ex.Message); }
    }

    static bool WidgetRunning()
    {
        if (!Mutex.TryOpenExisting("Local\\CarsGlance.Instance", out Mutex m)) return false;
        m.Dispose();
        return true;
    }

    // ------------------------------------------------------------------ welcome (first run)

    UIElement Welcome()
    {
        StackPanel p = Page("Welcome to C.A.R.S. Glance", "Glance sits on your desktop, under your windows: the time, the weather, your day, the music and your countdowns. " +
            "A few choices to start (all of them can change later). Everything is optional.");
        p.Children.Add(Section("1.  Your city, for the weather"));
        p.Children.Add(CitySearch());
        p.Children.Add(Section("2.  Your day"));
        p.Children.Add(Card(SymbolRegular.CheckmarkCircle24, "Todoist tasks", TodoistStatus(), TodoistButtons()));
        p.Children.Add(Card(SymbolRegular.CalendarLtr24, "Apple Calendar through Spark", "Only if you use Spark Desktop: Glance reads your calendar from it.", Toggle(s.Agenda.Calendar, v => s.Agenda.Calendar = v)));
        p.Children.Add(Section("3.  Music"));
        p.Children.Add(Card(SymbolRegular.MusicNote224, "Show the song that's playing", "From any player that shows up in Windows' media controls (Spotify, Apple Music, Cider and more).", Toggle(s.NowPlaying.Enabled, v => s.NowPlaying.Enabled = v)));
        p.Children.Add(Section("4.  How to use it"));
        p.Children.Add(Panel(Note("•  Scroll over the panel, or use the dots and the ‹ › arrows at its edges, to switch views.\n" +
                                  "•  Click things to use them: tick a task, open an event, seek a song, scroll over the cover for its volume.\n" +
                                  "•  Right-click for more: pin a countdown, hide it for an hour, open these settings.", 14, new Thickness(0))));
        var done = ActionButton("Done", SymbolRegular.Checkmark24, () => window.Close(), primary: true);
        done.Margin = new Thickness(0, 18, 0, 0);
        p.Children.Add(done);
        return p;
    }

    // ------------------------------------------------------------------ general

    UIElement General()
    {
        StackPanel p = Page("General");
        p.Children.Add(Card(SymbolRegular.Power24, "Start Glance when you sign in", null, Toggle(StartsAtSignIn(), SetStartAtSignIn)));
        p.Children.Add(Card(SymbolRegular.Clock24, "24-hour clock", "Off: 2:05 PM. On: 14:05.", Toggle(s.Use24HourClock, v => s.Use24HourClock = v)));
        p.Children.Add(Card(SymbolRegular.CursorClick24, "After you pick a view, stay on it for", "Then Glance goes back to choosing by itself (music, what's next, countdowns).",
            Choice(Minutes(1, 2, 3, 5, 10, 15, 30, 60), s.ManualViewMinutes, v => s.ManualViewMinutes = v)));
        p.Children.Add(Card(SymbolRegular.BatterySaver24, "Calmer on battery saver", "When battery saver is on, changes happen at once instead of animating.",
            Toggle(s.QuietOnBattery, v => s.QuietOnBattery = v)));

        p.Children.Add(Section("The widget"));
        bool running = WidgetRunning();
        var status = Note(running ? "Glance is running." : "Glance isn't running.", 14, new Thickness(0, 0, 0, 10));
        var buttons = Row(
            ActionButton(running ? "Restart" : "Start", SymbolRegular.Play24, () =>
            {
                if (WidgetRunning()) { RunWidget("--quit --for-update"); Thread.Sleep(900); }
                RunWidget("");
                status.Text = "Glance is running.";
            }),
            ActionButton("Quit", SymbolRegular.Dismiss24, () => { RunWidget("--quit"); status.Text = "Glance isn't running. Start it here or from the Start menu."; }));
        var stack = new StackPanel();
        stack.Children.Add(status);
        stack.Children.Add(buttons);
        p.Children.Add(Panel(stack));
        return p;
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool StartsAtSignIn()
    {
        using RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Product.RunValue) != null;
    }

    static void SetStartAtSignIn(bool on)
    {
        using RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Product.RunValue, "\"" + WidgetExe + "\" --autostart", RegistryValueKind.String);
        else k.DeleteValue(Product.RunValue, false);
    }

    // ------------------------------------------------------------------ views

    UIElement Views()
    {
        StackPanel p = Page("Views", "What Glance can show, and in which order the dots, arrows and scroll wheel go through them.");
        p.Children.Add(Card(SymbolRegular.Home24, "First view", "What shows when nothing else needs the screen. Home: the time with your weather, next event, tasks, music and countdown beside it.",
            Choice(new[] { ("Home", "home"), ("Big clock", "clock") }, s.FirstView, v => { s.FirstView = v; window.Show("views"); })));
        if (s.FirstView == "home")
            p.Children.Add(Card(SymbolRegular.Clock24, "Also keep the big clock", "The time with seconds, as its own view.", Toggle(s.ShowClockView, v => s.ShowClockView = v)));

        p.Children.Add(Section("Order"));
        var list = new StackPanel();
        Dictionary<string, (string Name, string Hint)> names = new()
        {
            ["nextup"] = ("Next up", "Takes over a few minutes before an event (Calendar & tasks)"),
            ["agenda"] = ("Agenda", "Today's events and tasks (Calendar & tasks)"),
            ["nowplaying"] = ("Now Playing", "While music plays (Music)"),
            ["countdowns"] = ("Countdowns", "One view each (Countdowns)"),
            ["clock"] = ("Big clock", "With Home first, when turned on above"),
        };
        void Fill()
        {
            list.Children.Clear();
            for (int i = 0; i < s.ViewOrder.Count; i++)
            {
                int k = i;
                string id = s.ViewOrder[i];
                var (name, hint) = names.TryGetValue(id, out var n) ? n : (id, "");
                var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = (i + 1) + ".  " + name, FontSize = 15 });
                label.Children.Add(Note(hint, 12, new Thickness(22, 0, 0, 0)));
                g.Children.Add(label);
                var up = new Button { Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowUp24 }, IsEnabled = k > 0, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Earlier" };
                var down = new Button { Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowDown24 }, IsEnabled = k < s.ViewOrder.Count - 1, ToolTip = "Later" };
                up.Click += (o, e) => { (s.ViewOrder[k - 1], s.ViewOrder[k]) = (s.ViewOrder[k], s.ViewOrder[k - 1]); changed(); Fill(); };
                down.Click += (o, e) => { (s.ViewOrder[k + 1], s.ViewOrder[k]) = (s.ViewOrder[k], s.ViewOrder[k + 1]); changed(); Fill(); };
                var bs = Row(up, down);
                Grid.SetColumn(bs, 1);
                g.Children.Add(bs);
                list.Children.Add(g);
            }
        }
        Fill();
        p.Children.Add(Panel(list));
        p.Children.Add(Note("Home (or the big clock) always comes first. Glance still jumps to what matters by itself: a countdown's last hour, Next up, then the music."));
        return p;
    }

    // ------------------------------------------------------------------ weather

    UIElement Weather()
    {
        StackPanel p = Page("Weather", "From Open-Meteo: free, no account. Only your city's position (rounded to about a kilometre) is sent.");
        p.Children.Add(Card(SymbolRegular.WeatherPartlyCloudyDay24, "Show the weather", "Under the clock and on Home.", Toggle(s.Weather.Enabled, v => s.Weather.Enabled = v)));
        p.Children.Add(Card(SymbolRegular.Temperature24, "Units", null, Choice(new[] { ("Fahrenheit (°F)", true), ("Celsius (°C)", false) }, s.Weather.Fahrenheit, v => s.Weather.Fahrenheit = v)));
        p.Children.Add(Section("City"));
        p.Children.Add(CitySearch());
        return p;
    }

    UIElement CitySearch()
    {
        var stack = new StackPanel();
        var current = new TextBlock { FontSize = 15, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        void ShowCurrent() => current.Text = s.Weather.HasLocation
            ? "Now: " + (string.IsNullOrWhiteSpace(s.Weather.Place) ? "your location" : s.Weather.Place) + "  (" + s.Weather.Latitude.Value.ToString("0.00", Inv) + ", " + s.Weather.Longitude.Value.ToString("0.00", Inv) + ")"
            : "No city yet: the weather is hidden until you pick one.";
        ShowCurrent();
        stack.Children.Add(current);
        var box = new TextBox { PlaceholderText = "Type a city, e.g. Kansas City", MinWidth = 320, Margin = new Thickness(0, 0, 8, 0) };
        var results = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        async void Search()
        {
            string q = box.Text.Trim();
            if (q.Length < 2) return;
            results.Children.Clear();
            results.Children.Add(Note("Searching…"));
            List<Place> found;
            try { found = await Geocode(q); }
            catch (Exception ex) { results.Children.Clear(); results.Children.Add(Note("Couldn't search right now (" + ex.Message + ").")); return; }
            results.Children.Clear();
            if (found.Count == 0) { results.Children.Add(Note("No city by that name. Try adding the state or country.")); return; }
            foreach (Place pl in found)
            {
                var b = new Button { Content = pl.Label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4), Appearance = ControlAppearance.Secondary };
                b.Click += (o, e) =>
                {
                    s.Weather.Place = pl.Short;
                    s.Weather.Latitude = Math.Round(pl.Lat, 2); // city level: about a kilometre
                    s.Weather.Longitude = Math.Round(pl.Lon, 2);
                    s.Weather.Enabled = true;
                    changed();
                    ShowCurrent();
                    results.Children.Clear();
                    box.Text = "";
                };
                results.Children.Add(b);
            }
        }
        box.KeyDown += (o, e) => { if (e.Key == System.Windows.Input.Key.Enter) Search(); };
        var go = ActionButton("Search", SymbolRegular.Search24, Search);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(box);
        Grid.SetColumn(go, 1);
        g.Children.Add(go);
        stack.Children.Add(g);
        stack.Children.Add(results);
        return Panel(stack);
    }

    sealed record Place(string Label, string Short, double Lat, double Lon);

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    // Open-Meteo's geocoding: only the typed name is sent.
    static async Task<List<Place>> Geocode(string name)
    {
        string url = "https://geocoding-api.open-meteo.com/v1/search?count=6&language=en&format=json&name=" + Uri.EscapeDataString(name);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("CarsGlance/1.0 (+" + Product.RepositoryUrl + ")");
        using HttpResponseMessage res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var list = new List<Place>();
        if (!doc.RootElement.TryGetProperty("results", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (JsonElement r in arr.EnumerateArray())
        {
            string n = r.TryGetProperty("name", out var v) ? v.GetString() : "";
            string a1 = r.TryGetProperty("admin1", out v) ? v.GetString() : null;
            string country = r.TryGetProperty("country", out v) ? v.GetString() : null;
            if (!r.TryGetProperty("latitude", out var la) || !r.TryGetProperty("longitude", out var lo)) continue;
            string label = string.Join(", ", new[] { n, a1, country }.Where(x => !string.IsNullOrWhiteSpace(x)));
            string shortName = string.Join(", ", new[] { n, a1 ?? country }.Where(x => !string.IsNullOrWhiteSpace(x)));
            list.Add(new Place(label, shortName, la.GetDouble(), lo.GetDouble()));
        }
        return list;
    }

    // ------------------------------------------------------------------ calendar & tasks

    UIElement AgendaPage()
    {
        StackPanel p = Page("Calendar & tasks", "Today's events and Todoist tasks on the Agenda view and on Home; Next up counts down to your next event.");
        p.Children.Add(Card(SymbolRegular.CalendarLtr24, "Show the agenda", "Off hides the Agenda, Next up and Home's event and task lines.", Toggle(s.Agenda.Enabled, v => s.Agenda.Enabled = v)));
        p.Children.Add(Section("Todoist"));
        p.Children.Add(Card(SymbolRegular.CheckmarkCircle24, "Todoist tasks", "Tasks due today and overdue. Tick one on the widget to complete it (with 5 seconds to undo); + adds a task.",
            Toggle(s.Agenda.Todoist, v => s.Agenda.Todoist = v)));
        p.Children.Add(Card(SymbolRegular.Key24, "Connection", TodoistStatus(), TodoistButtons()));
        p.Children.Add(Section("Calendar"));
        string spark = FindSpark();
        p.Children.Add(Card(SymbolRegular.Mail24, "Apple Calendar through Spark", spark != null
                ? "Spark Desktop is installed. Glance reads the calendar while Spark is open and keeps the last copy when it's closed."
                : "Spark Desktop isn't installed on this PC. (iCloud, Google and Outlook calendars without Spark arrive in version 1.1.)",
            Toggle(s.Agenda.Calendar, v => s.Agenda.Calendar = v)));
        p.Children.Add(Section("Next up"));
        p.Children.Add(Card(SymbolRegular.Alert24, "Next up", "A countdown to your next event that takes the screen shortly before it starts.", Toggle(s.Agenda.NextUp, v => s.Agenda.NextUp = v)));
        p.Children.Add(Card(SymbolRegular.Timer24, "Show it this long before", null,
            Choice(Minutes(5, 10, 15, 20, 30, 45, 60), s.Agenda.NextUpMinutes, v => s.Agenda.NextUpMinutes = v)));
        p.Children.Add(Card(SymbolRegular.History24, "Keep it up after the start for", "Shows NOW · STARTED.",
            Choice(Minutes(0, 1, 2, 5, 10, 15), s.Agenda.NextUpHoldMinutes, v => s.Agenda.NextUpHoldMinutes = v)));
        return p;
    }

    static string TodoistStatus() => Cred.Exists(Product.CredentialPrefix + "Todoist")
        ? "Connected. The token is kept in Windows Credential Manager on this PC only."
        : "Not connected. Connect with your own API token from Todoist's settings (Integrations > Developer).";

    UIElement TodoistButtons()
    {
        bool on = Cred.Exists(Product.CredentialPrefix + "Todoist");
        if (!on) return ActionButton("Connect", SymbolRegular.PlugConnected24, () => RunWidget("--connect-todoist"), primary: true);
        return ActionButton("Disconnect", SymbolRegular.PlugDisconnected24, () =>
        {
            var r = System.Windows.MessageBox.Show("Disconnect Todoist? Glance forgets the token (Todoist itself isn't changed).", App.WindowTitle,
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
            if (r != System.Windows.MessageBoxResult.OK) return;
            RunWidget("--disconnect-todoist");
            Thread.Sleep(600);
            window.Show("agenda");
        });
    }

    static string FindSpark()
    {
        string known = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SparkDesktop",
            @"resources\app.asar.unpacked\node_modules\@readdle\sparkcore-win\bin\Release\SparkCore.bundle\spark.exe");
        return File.Exists(known) ? known : null;
    }

    // ------------------------------------------------------------------ music

    UIElement Music()
    {
        StackPanel p = Page("Music", "Now Playing shows the song from Windows' media controls, with its cover, the controls and the time.");
        p.Children.Add(Card(SymbolRegular.MusicNote224, "Show Now Playing", "It takes the screen while music plays.", Toggle(s.NowPlaying.Enabled, v => s.NowPlaying.Enabled = v)));
        p.Children.Add(Card(SymbolRegular.History24, "Stay up after a pause for", null,
            Choice(Minutes(0, 1, 2, 5, 10, 15, 30), (int)Math.Round(s.NowPlaying.KeepAfterPauseMinutes), v => s.NowPlaying.KeepAfterPauseMinutes = v)));
        var players = new TextBox { Text = string.Join(", ", s.NowPlaying.Apps), PlaceholderText = "Any player", MinWidth = 260 };
        players.LostFocus += (o, e) =>
        {
            s.NowPlaying.Apps = players.Text.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changed();
        };
        p.Children.Add(Card(SymbolRegular.Apps24, "Only these players", "Part of the app's name, separated by commas: Spotify, Cider, AppleMusic. Empty: any player (browsers too).", players));
        p.Children.Add(Panel(Note("On the widget: click the cover to open the player, click or drag along the bar to jump, scroll over the cover to change the player's volume. " +
                                  "Right-click for shuffle and repeat (when the player allows them).", 14, new Thickness(0))));
        return p;
    }

    // ------------------------------------------------------------------ countdowns

    UIElement Countdowns()
    {
        StackPanel p = Page("Countdowns", "Each countdown is its own view. Pinned ones stay on screen until they end; the others show up in their last 24 hours, and every one takes over in its final hour.");
        var list = new StackPanel();
        void Fill()
        {
            list.Children.Clear();
            if (s.Countdowns.Count == 0) list.Children.Add(Note("No countdowns yet.", 14, new Thickness(2, 0, 0, 10)));
            foreach (CountdownSetting c in s.Countdowns.ToList()) list.Children.Add(CountdownEditor(c, Fill));
        }
        Fill();
        p.Children.Add(list);
        var add = ActionButton("Add a countdown", SymbolRegular.Add24, () =>
        {
            DateTime when = DateTime.Today.AddDays(7).AddHours(9);
            s.Countdowns.Add(new CountdownSetting { Name = "New countdown", At = when.ToString("yyyy-MM-ddTHH:mm", Inv), DoneText = "IT'S TIME", ShowDoneHours = 24 });
            Revalidate();
            changed();
            Fill();
        }, primary: true);
        add.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(add);
        return p;
    }

    void Revalidate()
    {
        // re-parse so Valid/AtLocal follow the edits (the widget does the same when it reads the file)
        Settings fresh = Settings.Parse(s.ToJson());
        for (int i = 0; i < s.Countdowns.Count && i < fresh.Countdowns.Count; i++)
        {
            s.Countdowns[i].Valid = fresh.Countdowns[i].Valid;
            s.Countdowns[i].AtLocal = fresh.Countdowns[i].AtLocal;
        }
    }

    UIElement CountdownEditor(CountdownSetting c, Action refill)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        UIElement Field(string label, UIElement control, int row, int col)
        {
            var f = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            f.Children.Add(Note(label, 12, new Thickness(0, 0, 0, 4)));
            f.Children.Add(control);
            Grid.SetRow(f, row);
            Grid.SetColumn(f, col);
            grid.Children.Add(f);
            return f;
        }

        DateTime at = DateTime.TryParseExact(c.At, new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" }, Inv, DateTimeStyles.None, out DateTime t) ? t : DateTime.Today.AddDays(1);
        var name = new TextBox { Text = c.Name, PlaceholderText = "Name" };
        var date = new DatePicker { SelectedDate = at.Date };
        var time = new TextBox { Text = at.TimeOfDay == TimeSpan.Zero && c.At.Length <= 10 ? "" : at.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US")), PlaceholderText = "Midnight (or e.g. 8:00 PM)" };
        var done = new TextBox { Text = c.DoneText, PlaceholderText = "IT'S TIME" };
        var problem = Note("", 12);
        problem.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 110));

        void Commit()
        {
            string n = name.Text.Trim();
            if (n.Length == 0) { problem.Text = "Give it a name."; return; }
            if (s.Countdowns.Any(x => !ReferenceEquals(x, c) && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))) { problem.Text = "Another countdown already has that name."; return; }
            if (date.SelectedDate is not DateTime d) { problem.Text = "Pick a date."; return; }
            if (!TryTime(time.Text, out TimeSpan tod)) { problem.Text = "Write the time like 8:00 PM or 20:00 (empty means midnight)."; return; }
            problem.Text = "";
            c.Name = n;
            c.At = d.Date.Add(tod).ToString("yyyy-MM-ddTHH:mm", Inv);
            c.DoneText = string.IsNullOrWhiteSpace(done.Text) ? "IT'S TIME" : done.Text.Trim();
            Revalidate();
            changed();
        }
        name.LostFocus += (o, e) => Commit();
        time.LostFocus += (o, e) => Commit();
        done.LostFocus += (o, e) => Commit();
        date.SelectedDateChanged += (o, e) => Commit();

        Field("Name", name, 0, 0);
        Field("Date", date, 0, 2);
        Field("Time (empty = midnight)", time, 1, 0);
        Field("When it ends, show", done, 1, 2);
        Field("Keep that up for", Choice(new[] { ("1 hour", 1.0), ("6 hours", 6.0), ("12 hours", 12.0), ("1 day", 24.0), ("2 days", 48.0), ("1 week", 168.0) },
            c.ShowDoneHours, v => c.ShowDoneHours = v, 160), 2, 0);
        var pin = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12) };
        pin.Children.Add(Toggle(c.Pinned, v => c.Pinned = v));
        pin.Children.Add(new TextBlock { Text = "Pinned: always on screen", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        Grid.SetRow(pin, 2);
        Grid.SetColumn(pin, 2);
        grid.Children.Add(pin);
        var remove = ActionButton("Remove", SymbolRegular.Delete24, () =>
        {
            var r = System.Windows.MessageBox.Show("Remove the countdown \"" + c.Name + "\"?", App.WindowTitle, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
            if (r != System.Windows.MessageBoxResult.OK) return;
            s.Countdowns.Remove(c);
            changed();
            refill();
        });
        var bottom = new StackPanel();
        bottom.Children.Add(problem);
        bottom.Children.Add(remove);
        Grid.SetRow(bottom, 3);
        Grid.SetColumnSpan(bottom, 3);
        grid.Children.Add(bottom);
        return Panel(grid);
    }

    // "8:00 PM", "8 pm", "20:00", "" (midnight)
    internal static bool TryTime(string text, out TimeSpan t)
    {
        t = TimeSpan.Zero;
        text = (text ?? "").Trim();
        if (text.Length == 0) return true;
        var en = CultureInfo.GetCultureInfo("en-US");
        string[] formats = { "h:mm tt", "h:mmtt", "h tt", "htt", "H:mm", "HH:mm", "h:mm t", "h t" };
        if (DateTime.TryParseExact(text.ToUpperInvariant(), formats, en, DateTimeStyles.None, out DateTime d)) { t = d.TimeOfDay; return true; }
        return false;
    }

    // ------------------------------------------------------------------ appearance

    static readonly (string Name, string Hex)[] Accents =
    {
        ("Blue (default)", "#3D8BFF"), ("Teal", "#1ABC9C"), ("Green", "#2ECC71"), ("Amber", "#FFB300"), ("Orange", "#FF7A1A"),
        ("Red", "#FF281C"), ("Crimson", "#E0103C"), ("Pink", "#FF4FA3"), ("Purple", "#9B59FF"), ("White", "#F2F2F2"),
    };

    UIElement Appearance()
    {
        StackPanel p = Page("Appearance");
        var swatches = new WrapPanel();
        var hex = new TextBox { Text = s.Appearance.Accent, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), PlaceholderText = "#RRGGBB" };
        void Mark()
        {
            foreach (Border b in swatches.Children.OfType<Border>())
                b.BorderBrush = string.Equals((string)b.Tag, s.Appearance.Accent, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent;
        }
        foreach (var (n, h) in Accents)
        {
            var col = (Color)ColorConverter.ConvertFromString(h);
            var b = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 0, 10, 8), BorderThickness = new Thickness(3), Tag = h, ToolTip = n, Cursor = System.Windows.Input.Cursors.Hand };
            b.Child = new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(col), Margin = new Thickness(1) };
            b.MouseLeftButtonUp += (o, e) => { s.Appearance.Accent = h; hex.Text = h; changed(); Mark(); };
            swatches.Children.Add(b);
        }
        Mark();
        hex.LostFocus += (o, e) =>
        {
            string v = hex.Text.Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            if (v.Length == 7 && int.TryParse(v[1..], NumberStyles.HexNumber, Inv, out _)) { s.Appearance.Accent = v.ToUpperInvariant(); changed(); Mark(); }
            else hex.Text = s.Appearance.Accent;
        };
        var accent = new StackPanel();
        accent.Children.Add(new TextBlock { Text = "Accent colour", FontSize = 15 });
        accent.Children.Add(Note("Titles, the NOW and overdue marks, the page dot and the buttons. Blue is the C.A.R.S. look.", 13, new Thickness(0, 2, 0, 12)));
        accent.Children.Add(swatches);
        accent.Children.Add(hex);
        p.Children.Add(Panel(accent));

        p.Children.Add(Card(SymbolRegular.Blur24, "Glass", "How dark the frosted glass is over your wallpaper.", Slider(s.Appearance.Glass, 0, 100, 5, v => s.Appearance.Glass = v, v => v + "%")));
        p.Children.Add(Card(SymbolRegular.ZoomIn24, "Size", "Everything grows with it, text included.", Slider(s.Appearance.Size, 70, 160, 10, v => s.Appearance.Size = v, v => v + "%")));
        p.Children.Add(Card(SymbolRegular.ArrowMove24, "Position", "On each display, clear of the taskbar.",
            Choice(new[] { ("Centre", "center"), ("Top", "top"), ("Bottom", "bottom"), ("Top left", "top-left"), ("Top right", "top-right"), ("Bottom left", "bottom-left"), ("Bottom right", "bottom-right") },
                s.Appearance.Position, v => s.Appearance.Position = v)));

        p.Children.Add(Section("Displays"));
        var displays = new StackPanel();
        foreach (DisplayInfo d in DisplayInfo.All())
        {
            string id = d.Name;
            bool shown = !s.Appearance.HiddenDisplays.Contains(id, StringComparer.OrdinalIgnoreCase);
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = d.Label, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            var t = Toggle(shown, v =>
            {
                s.Appearance.HiddenDisplays.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
                if (!v) s.Appearance.HiddenDisplays.Add(id);
            });
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            displays.Children.Add(g);
        }
        p.Children.Add(Panel(displays));
        return p;
    }

    UIElement Slider(int value, int min, int max, int step, Action<int> set, Func<int, string> label)
    {
        var text = new TextBlock { Text = label(value), Width = 52, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(10, 0, 0, 0) };
        var sl = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, Width = 220, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        sl.ValueChanged += (o, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            text.Text = label(v);
            set(v);
            changed();
        };
        return Row(sl, text);
    }

    // ------------------------------------------------------------------ accessibility

    UIElement Accessibility()
    {
        StackPanel p = Page("Accessibility");
        p.Children.Add(Card(SymbolRegular.Pause24, "Reduce motion", "Follow Windows: when Settings > Accessibility > Visual effects > Animation effects is off, Glance changes things at once too.",
            Choice(new[] { ("Follow Windows", "system"), ("Always", "on"), ("Never", "off") }, s.Accessibility.ReduceMotion, v => s.Accessibility.ReduceMotion = v)));
        p.Children.Add(Card(SymbolRegular.DarkTheme24, "High contrast", "Solid dark glass and full-strength text, whatever the wallpaper.", Toggle(s.Accessibility.HighContrast, v => s.Accessibility.HighContrast = v)));
        var bigger = ActionButton("Open Appearance", SymbolRegular.ZoomIn24, () => window.Show("appearance"));
        p.Children.Add(Card(SymbolRegular.TextFont24, "Bigger text", "Appearance > Size makes the whole panel bigger, text included (up to 160%).", bigger));
        return p;
    }

    // ------------------------------------------------------------------ updates

    UIElement Updates()
    {
        StackPanel p = Page("Updates", "Glance updates itself from its GitHub releases: it downloads the new Setup, checks it against the published checksum and shows UPDATE READY on the panel. Click that to install (about ten seconds).");
        p.Children.Add(Card(SymbolRegular.Info24, "This version", Version(), new TextBlock()));
        p.Children.Add(Card(SymbolRegular.ArrowSync24, "Check for updates by itself", "A minute after it starts, then once a day (never during a full-screen game).", Toggle(s.CheckForUpdates, v => s.CheckForUpdates = v)));
        var note = Note("", 13, new Thickness(0, 8, 0, 0));
        var row = Row(
            ActionButton("Check now", SymbolRegular.ArrowClockwise24, () =>
            {
                if (EventWaitHandle.TryOpenExisting("Local\\CarsGlance.CheckUpdates", out EventWaitHandle ev)) { ev.Set(); ev.Dispose(); note.Text = "Checking: if there's an update, UPDATE READY appears on the panel within a minute."; }
                else note.Text = "Glance isn't running: start it (General) and it checks a minute later.";
            }),
            ActionButton("What's new", SymbolRegular.Document24, () => OpenUrl(Product.RepositoryUrl + "/releases")));
        var st = new StackPanel();
        st.Children.Add(row);
        st.Children.Add(note);
        p.Children.Add(Panel(st));
        return p;
    }

    internal static string Version()
    {
        try
        {
            string v = FileVersionInfo.GetVersionInfo(WidgetExe).ProductVersion ?? "";
            int plus = v.IndexOf('+');
            return "C.A.R.S. Glance " + (plus > 0 ? v[..plus] : v);
        }
        catch { return "C.A.R.S. Glance"; }
    }

    // ------------------------------------------------------------------ about

    UIElement About()
    {
        StackPanel p = Page("About");
        p.Children.Add(Card(SymbolRegular.Info24, Version(), Product.Tagline + ". Free and open source (MIT licence). No account, no ads, no tracking.", new TextBlock()));
        p.Children.Add(Panel(Row(
            ActionButton("Website", SymbolRegular.Globe24, () => OpenUrl(Product.RepositoryUrl)),
            ActionButton("Privacy", SymbolRegular.Shield24, () => OpenUrl(Product.RepositoryUrl + "/blob/main/docs/privacy.md")),
            ActionButton("Report a problem", SymbolRegular.Bug24, () => OpenUrl(Product.RepositoryUrl + "/issues")))));
        p.Children.Add(Section("Diagnostics"));
        var note = Note("A short report for a bug report: versions, displays, which features are on and the recent log. No tokens, places, names, titles or links.", 13, new Thickness(0, 0, 0, 10));
        var st = new StackPanel();
        st.Children.Add(note);
        st.Children.Add(Row(
            ActionButton("Copy diagnostics", SymbolRegular.Copy24, () =>
            {
                try { Clipboard.SetText(Diagnostics()); note.Text = "Copied. Paste it into your report."; }
                catch (Exception ex) { note.Text = "Couldn't copy: " + ex.Message; }
            }),
            ActionButton("Open the data folder", SymbolRegular.Folder24, () => OpenUrl(Paths.DataDir))));
        p.Children.Add(Panel(st));
        return p;
    }

    string Diagnostics()
    {
        var b = new StringBuilder();
        b.AppendLine(Version());
        b.AppendLine("Windows: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription + "  (" + Environment.OSVersion.Version + ")");
        b.AppendLine(".NET: " + Environment.Version);
        b.AppendLine("Widget running: " + (WidgetRunning() ? "yes" : "no"));
        foreach (DisplayInfo d in DisplayInfo.All()) b.AppendLine("Display: " + d.Label);
        b.AppendLine($"First view: {s.FirstView}; clock view: {s.ShowClockView}; order: {string.Join(",", s.ViewOrder)}");
        b.AppendLine($"Weather: {(s.Weather.Enabled ? "on" : "off")}, city set: {s.Weather.HasLocation}, °F: {s.Weather.Fahrenheit}");
        b.AppendLine($"Agenda: {(s.Agenda.Enabled ? "on" : "off")}, Spark: {s.Agenda.Calendar} (installed: {FindSpark() != null}), Todoist: {s.Agenda.Todoist} (connected: {Cred.Exists(Product.CredentialPrefix + "Todoist")}), next up: {s.Agenda.NextUp} {s.Agenda.NextUpMinutes}/{s.Agenda.NextUpHoldMinutes}");
        b.AppendLine($"Music: {(s.NowPlaying.Enabled ? "on" : "off")}, players listed: {s.NowPlaying.Apps.Count}");
        b.AppendLine($"Countdowns: {s.Countdowns.Count} ({s.Countdowns.Count(c => c.Pinned)} pinned)");
        b.AppendLine($"Look: size {s.Appearance.Size}%, glass {s.Appearance.Glass}%, position {s.Appearance.Position}, hidden displays {s.Appearance.HiddenDisplays.Count}, reduce motion {s.Accessibility.ReduceMotion}, high contrast {s.Accessibility.HighContrast}");
        b.AppendLine("Recent log:");
        try
        {
            string log = Path.Combine(Paths.LogsDir, "glance.log");
            if (File.Exists(log))
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                string[] lines = r.ReadToEnd().Split('\n');
                foreach (string line in lines.Skip(Math.Max(0, lines.Length - 40)))
                    b.AppendLine("  " + System.Text.RegularExpressions.Regex.Replace(line.TrimEnd(), @"https?://\S+", "[link]"));
            }
        }
        catch (Exception ex) { b.AppendLine("  (couldn't read the log: " + ex.Message + ")"); }
        return b.ToString();
    }
}

// ------------------------------------------------------------------ small Windows helpers

static class Cred
{
    [System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);

    /// <summary>Whether a saved sign-in exists (its secret is never read into this process's memory beyond Windows' copy).</summary>
    public static bool Exists(string target)
    {
        if (!CredRead(target, 1, 0, out IntPtr p)) return false;
        CredFree(p);
        return true;
    }
}

sealed record DisplayInfo(string Name, string Label)
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [System.Runtime.InteropServices.DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    public static List<DisplayInfo> All()
    {
        var list = new List<(DisplayInfo Info, int X, int Y)>();
        MonitorEnumProc cb = (IntPtr m, IntPtr h, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(m, ref mi)) return true;
            int w = mi.rcMonitor.Right - mi.rcMonitor.Left, hgt = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            string num = new string((mi.szDevice ?? "").Where(char.IsDigit).ToArray());
            int scale = GetDpiForMonitor(m, 0, out uint dx, out _) == 0 ? (int)Math.Round(dx / 96.0 * 100) : 100;
            string label = "Display " + (num.Length > 0 ? num : "?") + "  ·  " + w + " × " + hgt + (scale != 100 ? "  ·  " + scale + "% scale" : "") + ((mi.dwFlags & 1) != 0 ? "  ·  main display" : "");
            list.Add((new DisplayInfo(mi.szDevice ?? "", label), mi.rcMonitor.Left, mi.rcMonitor.Top));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list.OrderBy(x => x.X).ThenBy(x => x.Y).Select(x => x.Info).ToList();
    }
}
