// C.A.R.S. Glance: a frosted-glass panel centred on every display, on the desktop layer: the time and weather,
// Now Playing, the agenda and Next up, and countdowns.
//
//   CarsGlance.exe                          run (single instance); clears "I quit it" so the keep-running task works again
//   CarsGlance.exe --autostart              the same, from sign-in (HKCU Run)
//   CarsGlance.exe --ensure-running         the keep-running task: start Glance if it isn't running and you didn't quit it
//   CarsGlance.exe --quit [--for-update]    stop the running copy (without --for-update it stays stopped until you start it)
//   CarsGlance.exe --settings [page]        open the Settings window (CarsGlance.Settings.exe), optionally at a page
//   CarsGlance.exe --disconnect-todoist     forget the Todoist token (Settings > Calendar & tasks)
//   CarsGlance.exe --connect-todoist        the window where you paste your own Todoist API token
//   CarsGlance.exe --migrate-legacy <dir>   one-time: bring settings, calendar cache and the Todoist sign-in over from Desktop Widget
//   CarsGlance.exe --selftest <file>        check the time, view, settings and layout logic
//   CarsGlance.exe --agenda-probe <file>    read-only live check of Todoist and the calendar (never writes the token)
//   CarsGlance.exe --preview <png> --view clock|countdown|done|nowplaying|agenda|nextup [--bg image] [--at yyyy-MM-ddTHH:mm:ss] [--height px] [--weather]
//                     nowplaying: [--live] (the real song, details in <png>.txt) [--art image] [--title text] [--paused] [--hover 0|1|2]
//                     motion: [--transition clock|countdown|nowplaying] (a strip of frames switching to that view) [--buttons] (morph, ring, click)
//                     agenda|nextup: --sample [--empty] [--started] (a made-up day around --at)
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;

namespace Glance;

static class Program
{
    internal const string MutexName = "Local\\CarsGlance.Instance";
    internal const string QuitEventName = "Local\\CarsGlance.Quit";
    internal const string RefreshEventName = "Local\\CarsGlance.Refresh";
    internal const string ShowEventName = "Local\\CarsGlance.Show";
    internal static string AppDir => Paths.AppDir;

    [STAThread]
    static int Main(string[] args)
    {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // the manifest already asks for PerMonitorV2
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (mode)
        {
            case "--quit":
                if (Array.IndexOf(args, "--for-update") < 0) SetQuitFlag(true); // you quit it: the keep-running task leaves it alone
                SignalQuit();
                return 0;
            case "--ensure-running": return EnsureRunning();
            case "--settings": return OpenSettings(args.Length > 1 ? args[1] : null);
            case "--disconnect-todoist":
                Credentials.Delete();
                Log.Write("Todoist disconnected (from Settings)");
                ConnectTodoistForm.SignalWidget();
                return 0;
            case "--selftest": return SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "CarsGlance-selftest.txt"));
            case "--preview": return Preview.Run(args);
            case "--connect-todoist": return ConnectTodoistForm.Run();
            case "--agenda-probe": return AgendaProbe.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "CarsGlance-agenda-probe.txt"));
            case "--migrate-legacy": return Legacy.Migrate(args.Length > 1 ? args[1] : null);
        }

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Started again while running (Start menu, sign-in): show it if it was hidden for an hour.
            if (mode != "--autostart" && EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle sh)) { sh.Set(); sh.Dispose(); }
            return 0;
        }
        SetQuitFlag(false);
        using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        using var refresh = new EventWaitHandle(false, EventResetMode.AutoReset, RefreshEventName);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new WidgetContext(quit, refresh, show));
        return 0;
    }

    internal static void SignalQuit()
    {
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out EventWaitHandle ev))
        {
            ev.Set();
            ev.Dispose();
        }
    }

    internal static void SetQuitFlag(bool on)
    {
        try
        {
            if (on) File.WriteAllText(Paths.QuitFlag, DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
            else if (File.Exists(Paths.QuitFlag)) File.Delete(Paths.QuitFlag);
        }
        catch { }
    }

    // The keep-running task (every 5 minutes): only starts Glance when it isn't running and you didn't quit it.
    // It starts it through Explorer so the widget isn't the task's child (the task ends after a minute).
    static int EnsureRunning()
    {
        if (File.Exists(Paths.QuitFlag)) return 0;
        if (Mutex.TryOpenExisting(MutexName, out Mutex existing)) { existing.Dispose(); return 0; }
        string exe = Environment.ProcessPath;
        Log.Write("keep-running task: Glance wasn't running; starting it");
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false })?.Dispose();
        return 0;
    }

    // The Settings window (CarsGlance.Settings.exe next to this one); page: general, views, weather, agenda, music,
    // countdowns, appearance, accessibility, updates, about or welcome.
    internal static int OpenSettings(string page = null)
    {
        Settings.Load(); // creates settings.json from the defaults the first time
        string exe = Path.Combine(AppDir, "CarsGlance.Settings.exe");
        try
        {
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe, page ?? "") { UseShellExecute = false })?.Dispose();
            else // a development build without the Settings app: the file itself
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Paths.SettingsFile + "\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) { Log.Write("settings: " + ex.Message); }
        return 0;
    }
}

// ------------------------------------------------------------------ one-time move from the old "Desktop Widget"

static class Legacy
{
    // Copies settings.json and calendar-cache.json from the old install folder (unless Glance already has its own,
    // changed settings) and moves the Todoist sign-in to Glance's name in Credential Manager. Changes nothing else.
    public static int Migrate(string oldDir)
    {
        var done = new List<string>();
        try
        {
            if (oldDir != null && Directory.Exists(oldDir))
            {
                string oldSettings = Path.Combine(oldDir, "settings.json");
                string defaults = File.Exists(Paths.DefaultSettingsFile) ? File.ReadAllText(Paths.DefaultSettingsFile) : null;
                bool glanceUntouched = !File.Exists(Paths.SettingsFile) || File.ReadAllText(Paths.SettingsFile) == defaults;
                if (File.Exists(oldSettings) && glanceUntouched)
                {
                    File.Copy(oldSettings, Paths.SettingsFile, true);
                    done.Add("settings");
                }
                string oldCache = Path.Combine(oldDir, "calendar-cache.json");
                string newCache = Path.Combine(Paths.DataDir, "calendar-cache.json");
                if (File.Exists(oldCache) && !File.Exists(newCache))
                {
                    File.Copy(oldCache, newCache);
                    done.Add("calendar cache");
                }
            }
            string token = Credentials.Read(Credentials.LegacyTarget);
            if (token != null)
            {
                if (Credentials.Read() == null && Credentials.Write(token)) done.Add("Todoist sign-in");
                if (Credentials.Read() != null) Credentials.Delete(Credentials.LegacyTarget);
            }
            Log.Write("migrated from Desktop Widget: " + (done.Count == 0 ? "nothing needed" : string.Join(", ", done)));
            ConnectTodoistForm.SignalWidget();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("migration from Desktop Widget failed: " + ex.Message);
            return 1;
        }
    }
}

// ------------------------------------------------------------------ logic checks (no windows)

static class SelfTest
{
    // --selftest: the same checks the xUnit project runs (tests\Glance.Tests), written to a file.
    public static int Run(string output)
    {
        List<(string Name, bool Ok)> checks = Checks();
        int failed = checks.Count(c => !c.Ok);
        var lines = checks.Select(c => (c.Ok ? "PASS  " : "FAIL  ") + c.Name).ToList();
        lines.Add(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        File.WriteAllLines(output, lines);
        return failed == 0 ? 0 : 1;
    }

    public static List<(string Name, bool Ok)> Checks()
    {
        var results = new List<(string Name, bool Ok)>();
        void Check(string name, bool ok) => results.Add((name, ok));

        DateTime t0 = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        Remaining r = Remaining.Compute(t0.AddDays(1), t0);
        Check("exactly one day left shows 1 / 00 / 00 / 00", r.Days == 1 && r.Hours == 0 && r.Minutes == 0 && r.Seconds == 0 && r.Labels()[0] == "DAY");
        r = Remaining.Compute(t0.AddMilliseconds(500), t0);
        Check("half a second left shows 1 second, not zero", !r.Done && r.Seconds == 1);
        Check("past the moment is done", Remaining.Compute(t0, t0.AddSeconds(1)).Done);

        // The shipped defaults hold nobody's personal data: no city, no countdowns, any player, Spark off.
        Settings defaults = Settings.Parse(File.ReadAllText(Paths.DefaultSettingsFile));
        Check("public defaults: no city, no countdowns, any player, Spark calendar off, Todoist offered",
            defaults.Weather.Enabled && !defaults.Weather.HasLocation && defaults.Countdowns.Count == 0 && defaults.NowPlaying.Apps.Count == 0 &&
            !defaults.Agenda.Calendar && defaults.Agenda.Todoist);
        Check("public defaults: next up 15 min before, held 5 min after", defaults.Agenda.NextUpMinutes == 15 && defaults.Agenda.NextUpHoldMinutes == 5);

        // The behaviour checks use a typical set-up: a city, Cider as the player, Spark on, one pinned midnight launch.
        Settings s = Settings.Parse("{ \"weather\": { \"place\": \"Kansas City\", \"latitude\": 39.10, \"longitude\": -94.58 }, " +
            "\"nowPlaying\": { \"apps\": [ \"Cider\" ] }, \"agenda\": { \"calendar\": true }, " +
            "\"countdowns\": [ { \"name\": \"Grand Theft Auto VI\", \"at\": \"2026-11-19T00:00\", \"pinned\": true, \"doneText\": \"OUT NOW\", \"showDoneHours\": 24 } ] }");
        CountdownSetting gta = s.Countdowns[0];
        Check("a countdown reads as local midnight", gta.Valid && gta.Pinned && gta.AtLocal == new DateTime(2026, 11, 19, 0, 0, 0));
        Check("weather needs a valid city-level location", s.Weather.HasLocation && !Settings.Parse("{ \"weather\": { \"latitude\": 120, \"longitude\": 5 } }").Weather.HasLocation);

        var en = CultureInfo.GetCultureInfo("en-US");
        DateTime pm = new(2026, 11, 16, 14, 5, 9);
        ViewModel clock = Views.Clock(s, pm, null);
        Check("12-hour clock: 2 / 05 / 09 PM", clock.Values.SequenceEqual(new[] { "2", "05", "09" }) && clock.Suffix == "PM" && clock.Footer == null);
        s.Use24HourClock = true;
        clock = Views.Clock(s, pm, null);
        Check("24-hour clock: 14 / 05 / 09, no suffix", clock.Values.SequenceEqual(new[] { "14", "05", "09" }) && clock.Suffix == null);
        s.Use24HourClock = false;
        Check("clock title is the date", clock.Title == "MONDAY, NOVEMBER 16");

        // smart rules
        List<ViewModel> v = Views.Build(s, new DateTime(2026, 11, 16, 2, 0, 0), null, null);
        Check("pinned GTA VI countdown is picked days ahead", v.Count == 2 && Smart.Pick(v) == 1);
        gta.Pinned = false;
        v = Views.Build(s, new DateTime(2026, 11, 16, 2, 0, 0), null, null);
        Check("unpinned countdown 3 days out: clock is picked", Smart.Pick(v) == 0);
        v = Views.Build(s, new DateTime(2026, 11, 18, 1, 0, 0), null, null);
        Check("unpinned countdown in its last 24 h is picked", Smart.Pick(v) == 1);
        v = Views.Build(s, new DateTime(2026, 11, 19, 10, 0, 0), null, null);
        Check("10 h after launch: shows OUT NOW and is picked", v.Count == 2 && v[1].Big == "OUT NOW" && Smart.Pick(v) == 1);
        v = Views.Build(s, new DateTime(2026, 11, 20, 0, 0, 1), null, null);
        Check("24 h after launch: countdown gone, clock only", v.Count == 1 && Smart.Pick(v) == 0);
        gta.Pinned = true;

        ViewModel cd = Views.Countdown(gta, new DateTime(2026, 11, 16, 2, 0, 0));
        Check("countdown footer: date and time", cd.Footer == "THURSDAY, NOVEMBER 19  ·  12:00 AM");

        Check("weather code 2 = partly cloudy", WeatherService.Describe(2) == "PARTLY CLOUDY");
        Check("weather code 95 = thunderstorms", WeatherService.Describe(95) == "THUNDERSTORMS");
        string line = WeatherService.Line(new WeatherNow { Temp = 41.4, Code = 3, High = 52.6, Low = 33.2, RainPct = 20 });
        Check("weather line", line == "41°  CLOUDY   ·   H 53°  L 33°   ·   20% RAIN");

        // Now Playing
        Check("Cider's media ID matches, other players (Zen) don't",
            MediaService.Matches("CiderCollective.Cider_a6qxe093bx5xj!App", s.NowPlaying.Apps) && !MediaService.Matches("F0DC299D809B9700", s.NowPlaying.Apps));
        Check("no apps listed: any player counts", MediaService.Matches("Spotify.exe", new List<string>()) && !MediaService.Matches("", new List<string>()));
        DateTime n0 = new(2026, 11, 16, 2, 0, 0);
        DateTime n0u = n0.ToUniversalTime();
        var song = new NowPlayingInfo
        {
            AppId = "CiderCollective.Cider_a6qxe093bx5xj!App", Title = "Not Wrong Now", Artist = "YoungBoy Never Broke Again",
            Playing = true, CanPlayPause = true, Duration = TimeSpan.FromSeconds(226), Position = TimeSpan.FromSeconds(34), PositionUtc = n0u,
        };
        v = Views.Build(s, n0, null, song);
        Check("a playing song comes before the pinned countdown", v.Count == 3 && v[Smart.Pick(v)].Kind == ViewKind.NowPlaying && v[Smart.Pick(v)].Title == "NOW PLAYING");
        v = Views.Build(s, new DateTime(2026, 11, 18, 23, 30, 0), null, song);
        Check("final hour: the countdown beats a playing song", v[Smart.Pick(v)].Kind == ViewKind.Countdown);
        gta.Pinned = false;
        v = Views.Build(s, new DateTime(2026, 11, 18, 23, 30, 0), null, song);
        Check("final hour: an unpinned countdown beats it too", v[Smart.Pick(v)].Kind == ViewKind.Countdown);
        gta.Pinned = true;
        v = Views.Build(s, new DateTime(2026, 11, 18, 22, 30, 0), null, song);
        Check("90 minutes out: the song still wins", v[Smart.Pick(v)].Kind == ViewKind.NowPlaying);
        v = Views.Build(s, new DateTime(2026, 11, 19, 0, 9, 50), null, song);
        Check("OUT NOW holds over the song for 10 minutes", v[Smart.Pick(v)].Kind == ViewKind.Countdown && v[Smart.Pick(v)].Big == "OUT NOW");
        v = Views.Build(s, new DateTime(2026, 11, 19, 0, 10, 10), null, song);
        Check("after 10 minutes the song wins again", v[Smart.Pick(v)].Kind == ViewKind.NowPlaying);
        NowPlayingInfo paused = song.Clone();
        paused.Playing = false;
        paused.PausedSinceUtc = n0u.AddMinutes(-2);
        v = Views.Build(s, n0, null, paused);
        Check("paused 2 minutes ago: stays on Now Playing, titled PAUSED", v[Smart.Pick(v)].Kind == ViewKind.NowPlaying && v[Smart.Pick(v)].Title == "PAUSED");
        paused.PausedSinceUtc = n0u.AddMinutes(-10);
        v = Views.Build(s, n0, null, paused);
        Check("paused 10 minutes ago: back to the countdown, song still a click away", v.Count == 3 && v[Smart.Pick(v)].Kind == ViewKind.Countdown);
        paused.PausedSinceUtc = DateTime.MinValue;
        v = Views.Build(s, n0, null, paused);
        Check("already paused when the widget started: doesn't take over", v[Smart.Pick(v)].Kind == ViewKind.Countdown);
        NowPlayingInfo blank = song.Clone();
        blank.Title = "";
        Check("no song title: no Now Playing view", Views.Build(s, n0, null, blank).Count == 2);
        s.NowPlaying.Enabled = false;
        Check("Now Playing turned off in settings: no view", Views.Build(s, n0, null, song).Count == 2);
        s.NowPlaying.Enabled = true;
        float prog = Views.MediaTimes(song, n0u.AddSeconds(10.6), out string el, out string lf);
        Check("position runs on while playing: 0:44 and -3:02", el == "0:44" && lf == "-3:02" && Math.Abs(prog - 44.6 / 226) < 0.001);
        Views.MediaTimes(song, n0u.AddMinutes(10), out el, out lf);
        Check("position stops at the end of the song", el == "3:46" && lf == "-0:00");
        paused.Playing = false;
        Views.MediaTimes(paused, n0u.AddMinutes(1), out el, out _);
        Check("paused position stays put", el == "0:34");
        Check("hour-long times read 1:02:03", NowPlayingInfo.Format(3723) == "1:02:03");
        Check("grey cover leaves the glass untinted", AlbumArt.MainColour(Enumerable.Repeat(unchecked((int)0xFF808080), 64 * 64).ToArray(), 64) == null);
        Color? red = AlbumArt.MainColour(Enumerable.Repeat(unchecked((int)0xFFC01010), 64 * 64).ToArray(), 64);
        Check("red cover tints red", red is Color rc && rc.R > rc.G * 2 && rc.R > rc.B * 2);
        NowPlayingInfo later = song.Clone();
        later.Position = TimeSpan.FromSeconds(90);
        NowPlayingInfo next = song.Clone();
        next.Title = "Poison";
        ViewModel a0 = Views.NowPlaying(s, song, n0u), a1 = Views.NowPlaying(s, later, n0u), a2 = Views.NowPlaying(s, next, n0u);
        Check("progress alone redraws only the bar; a new song crossfades", a0.FadeKey == a1.FadeKey && a0.StaticKey == a1.StaticKey && a0.FadeKey != a2.FadeKey);

        foreach (float scale in new[] { 0.6f, 1f, 1.5f, 2f }) // 864p laptop to 4K TV and up
        {
            using var pa = new PanelArt(scale, (int)Math.Round(8 * scale));
            List<string> problems = pa.LayoutProblems();
            Check($"Now Playing layout at scale {scale}: everything inside its redraw area" + (problems.Count > 0 ? " (" + string.Join("; ", problems) + ")" : ""), problems.Count == 0);
        }

        // agenda + next up
        DateTime d17 = new(2026, 11, 17, 10, 0, 0, DateTimeKind.Local);
        string spark = "{\"events\":[" +
            "{\"id\":\"a\",\"title\":\"Dentist\",\"start\":\"2026-11-17T14:30:00-06:00\",\"end\":\"2026-11-17T15:30:00-06:00\",\"all_day\":false,\"location\":\"Main St Dental\",\"calendar\":\"Home\"}," +
            "{\"id\":\"b\",\"title\":\"Mom's birthday\",\"start\":\"2026-11-17\",\"end\":\"2026-11-18\",\"all_day\":true}," +
            "{\"id\":\"c\",\"title\":\"Haircut\",\"start\":\"2026-11-18T09:00:00-06:00\",\"end\":\"2026-11-18T09:45:00-06:00\",\"all_day\":false}]}";
        List<AgendaItem> evs = Agenda.ParseSpark(spark);
        DateTime dentistAt = DateTimeOffset.Parse("2026-11-17T14:30:00-06:00", CultureInfo.InvariantCulture).LocalDateTime;
        Check("calendar: Spark events read (timed with its offset, all-day as all-day)",
            evs.Count == 3 && evs[0].HasTime && evs[0].Start == dentistAt && evs[0].Where == "Main St Dental" && evs[1].AllDay && !evs[1].HasTime);
        bool threw = false;
        try { Agenda.ParseSpark("{\"error\":{\"message\":\"Spark CLI can't access your Spark Desktop application.\"}}"); } catch (InvalidOperationException) { threw = true; }
        Check("calendar: Spark closed is reported, not read as an empty day", threw);
        string todo = "{\"results\":[" +
            "{\"id\":\"1\",\"content\":\"Pay **phone** bill\",\"priority\":4,\"project_id\":\"p\",\"checked\":false,\"due\":{\"date\":\"2026-11-16\",\"is_recurring\":false,\"string\":\"yesterday\"}}," +
            "{\"id\":\"2\",\"content\":\"Dentist\",\"priority\":4,\"project_id\":\"i\",\"due\":{\"date\":\"2026-11-17T14:30:00\",\"string\":\"today 2:30pm\"}}," +
            "{\"id\":\"3\",\"content\":\"[Call the bank](https://example.com)\",\"priority\":2,\"project_id\":\"p\",\"due\":{\"date\":\"2026-11-17\"}}," +
            "{\"id\":\"4\",\"content\":\"Already done\",\"checked\":true,\"due\":{\"date\":\"2026-11-17\"}}],\"next_cursor\":null}";
        var projects = new Dictionary<string, string> { ["p"] = "Errands", ["i"] = "Inbox" };
        List<AgendaItem> tks = Agenda.ParseTodoist(todo, projects, out string cursor);
        Check("todoist: tasks read (markdown removed, done ones skipped, times kept)",
            tks.Count == 3 && tks[0].Title == "Pay phone bill" && tks[0].Priority == 4 && !tks[0].HasTime && tks[0].Group == "Errands" &&
            tks[1].HasTime && tks[1].Start == new DateTime(2026, 11, 17, 14, 30, 0) && tks[2].Title == "Call the bank" && cursor == null);
        List<AgendaItem> merged = Agenda.Merge(evs, tks);
        Check("an appointment in both Apple Calendar and Todoist shows once", merged.Count(i => i.Title == "Dentist") == 1 && merged.Count == 5 && evs[0].AlsoInTodoist);
        List<AgendaItem> todayList = Agenda.Today(merged, d17);
        Check("agenda order: coming up by time, then overdue, then all-day, then today's tasks",
            todayList.Select(i => i.Title).SequenceEqual(new[] { "Dentist", "Pay phone bill", "Mom's birthday", "Call the bank" }));
        List<AgendaRow> rows = todayList.Select(i => Views.Row(i, d17)).ToList();
        Check("agenda rows: time, overdue date in red, all day, untimed task",
            rows[0].Time == Agenda.Time(dentistAt) && rows[0].Marker == RowMarker.Bar && rows[1].Time == "NOV 16" && rows[1].Alert &&
            rows[2].Time == "ALL DAY" && rows[3].Time == "" && rows[3].Marker == RowMarker.Ring && rows[3].Right == "Errands");
        DateTime before = dentistAt.AddMinutes(-10);
        AgendaItem nu = Agenda.NextUp(merged, before, 15);
        ViewModel nuv = nu == null ? null : Views.NextUp(nu, before);
        Check("next up: 10 minutes before the dentist, counting 10:00", nuv != null && nuv.Detail == "Dentist" && nuv.Values.SequenceEqual(new[] { "10", "00" }));
        Check("next up: nothing 30 minutes out, nothing once it starts",
            Agenda.NextUp(merged, dentistAt.AddMinutes(-30), 15) == null && Agenda.NextUp(merged, dentistAt.AddSeconds(1), 15) == null);
        AgendaItem started = Agenda.NextUp(merged, dentistAt.AddMinutes(2), 15, 5);
        ViewModel sv = started == null ? null : Views.NextUp(started, dentistAt.AddMinutes(2));
        Check("next up holds 5 minutes after the start as NOW · STARTED", sv != null && sv.Title == "NOW" && sv.Big == "STARTED" && sv.Values == null);
        Check("…and is gone after those 5 minutes", Agenda.NextUp(merged, dentistAt.AddMinutes(5).AddSeconds(1), 15, 5) == null);
        Check("reaching the start rises into place (new fade key)", nuv != null && sv != null && nuv.FadeKey != sv.FadeKey && nuv.Id == sv.Id);
        Check("default settings: next up 15 min before, held 5 min after", s.Agenda.NextUpMinutes == 15 && s.Agenda.NextUpHoldMinutes == 5);
        var agendaData = new AgendaData { Events = evs, Tasks = tks, CalendarUtc = DateTime.UtcNow, TodoistConnected = true };
        v = Views.Build(s, before, null, song, agendaData);
        Check("next up beats a playing song and the pinned countdown", v[Smart.Pick(v)].Kind == ViewKind.NextUp);
        v = Views.Build(s, new DateTime(2026, 11, 18, 23, 50, 0), null, song, new AgendaData
        {
            Events = new List<AgendaItem> { new() { Source = AgendaSource.Calendar, Title = "Late call", Start = new DateTime(2026, 11, 18, 23, 59, 0), End = new DateTime(2026, 11, 19, 0, 30, 0), HasTime = true } },
        });
        Check("GTA's final hour still beats next up", v[Smart.Pick(v)].Kind == ViewKind.Countdown);
        var many = Enumerable.Range(0, 6).Select(n => new AgendaItem { Source = AgendaSource.Todoist, Title = "Task " + n, Start = d17.Date }).ToList();
        ViewModel big = Views.AgendaView(s, new AgendaData { Tasks = many, TodoistConnected = true }, many, d17);
        Check("a long day shows three lines and \"+ 3 more · 1 of 2\"", big.Rows.Count == 4 && big.Rows[3].More && big.Rows[3].Title.StartsWith("+ 3 more") && big.Rows[3].Title.EndsWith("1 of 2"));
        var aui = new AgendaUi { Page = 1 };
        ViewModel page2 = Views.AgendaView(s, new AgendaData { Tasks = many, TodoistConnected = true }, many, d17, aui);
        Check("…\"+ N more\" pages on, then back to the top", page2.Rows.Count == 4 && page2.Rows[0].Title == "Task 3" && page2.Rows[3].Title.StartsWith("Back to the top"));
        aui = new AgendaUi();
        aui.Completing["Task 0"] = DateTime.UtcNow.AddSeconds(5);
        many.ForEach(t => t.Id = t.Title);
        ViewModel ticking = Views.AgendaView(s, new AgendaData { Tasks = many, TodoistConnected = true }, many, d17, aui);
        Check("a ticked task stays in place, struck through, while Undo is offered", ticking.Rows[0].Completing && ticking.Rows[0].Title == "Task 0" && !ticking.Rows[1].Completing);
        aui.Completing.Clear();
        aui.Done.Add("Task 0");
        ViewModel afterDone = Views.AgendaView(s, new AgendaData { Tasks = many, TodoistConnected = true }, many, d17, aui);
        Check("once sent to Todoist it's hidden right away", afterDone.Rows[0].Title == "Task 1" && afterDone.Rows.Count(r => !r.More) == 3);
        Check("quick add shows only with Todoist connected", afterDone.CanAdd && !Views.AgendaView(s, new AgendaData { Tasks = many }, many, d17).CanAdd);
        var dismiss = new AgendaUi();
        dismiss.Dismissed.Add(nuv?.Id ?? "");
        Check("dismissing Next up hides it", Views.Build(s, before, null, null, agendaData, dismiss).All(x => x.Kind != ViewKind.NextUp));
        Check("meeting links: Zoom in the notes wins over another link",
            Agenda.MeetingLink("https://example.com/menu", "Join: https://us02web.zoom.us/j/123?pwd=x.") == "https://us02web.zoom.us/j/123?pwd=x");
        Check("directions only for a real place, never for a link", Agenda.Directions(Agenda.Place(evs[0])).Contains("Main%20St%20Dental") &&
            Agenda.Place(new AgendaItem { Where = "https://meet.google.com/abc" }) == null);
        AgendaItem card = evs[0];
        var cardUi = new AgendaUi { CardId = card.Id };
        ViewModel withCard = Views.AgendaView(s, new AgendaData { Events = evs, CalendarUtc = DateTime.UtcNow }, evs, d17, cardUi);
        Check("clicking an event opens its card with directions", withCard.Card != null && withCard.Card.Title == "Dentist" && withCard.Card.MapsUrl != null && withCard.Card.JoinUrl == null);
        Check("per-app volume finds the player by its media ID", AppVolume.Key("CiderCollective.Cider_a6qxe093bx5xj!App") == "CiderCollective.Cider" && AppVolume.Key("Spotify.exe") == "Spotify");
        // Home and the view order (1.0)
        ViewModel home = Views.Build(s, before, new WeatherNow { Temp = 41, Code = 2, High = 52, Low = 33 }, song, agendaData)[0];
        Check("Home leads, with weather, next event, tasks, music and the countdown",
            home.Kind == ViewKind.Home && home.Values.Length == 2 && home.Lines.Select(l => l.Label).SequenceEqual(new[] { "WEATHER", "NEXT", "TASKS", "PLAYING", "COUNTDOWN" }) &&
            home.Lines[0].Text == "41°  Partly cloudy  ·  52° / 33°" && home.Lines[1].Text.EndsWith("Dentist") && home.Lines[2].Alert && home.Lines[4].Target == "countdown:" + gta.Name);
        s.FirstView = "clock";
        Check("first view can be the big clock instead", Views.Build(s, before, null, null)[0].Kind == ViewKind.Clock);
        s.FirstView = "home";
        s.ViewOrder = new List<string> { "nowplaying", "agenda", "nextup", "countdowns", "clock" };
        List<ViewKind> kinds = Views.Build(s, before, null, song, agendaData).Select(x => x.Kind).ToList();
        Check("views follow the order chosen in Settings", kinds.SequenceEqual(new[] { ViewKind.Home, ViewKind.NowPlaying, ViewKind.Agenda, ViewKind.NextUp, ViewKind.Countdown }));
        s.ViewOrder = new List<string>(Settings.DefaultOrder);
        s.ShowClockView = true;
        Check("with Home first, the big clock can stay as its own view", Views.Build(s, before, null, null).Any(x => x.Kind == ViewKind.Clock));
        s.ShowClockView = false;
        Settings odd = Settings.Parse("{ \"appearance\": { \"size\": 500, \"glass\": -3, \"position\": \"middle\", \"accent\": \"nope\" }, \"viewOrder\": [ \"agenda\", \"bogus\" ], \"firstView\": \"x\" }");
        Check("odd settings are made safe (size, glass, position, accent, order, first view)",
            odd.Appearance.Size == 160 && odd.Appearance.Glass == 0 && odd.Appearance.Position == "center" && odd.Appearance.AccentColor.R == 61 && odd.Appearance.AccentColor.B == 255 &&
            odd.ViewOrder.SequenceEqual(new[] { "agenda", "nextup", "nowplaying", "countdowns", "clock" }) && odd.FirstView == "home");
        Check("countdown distance reads simply", Views.Until(new DateTime(2026, 11, 20, 12, 0, 0), new DateTime(2026, 11, 18, 12, 0, 0)) == "2 days" &&
            Views.Until(new DateTime(2026, 11, 18, 17, 20, 0), new DateTime(2026, 11, 18, 12, 0, 0)) == "5 h 20 min");
        Settings round = Settings.Parse(s.ToJson());
        Check("settings save and read back the same", round.Weather.Place == "Kansas City" && round.Countdowns.Count == 1 && round.Countdowns[0].Pinned == gta.Pinned &&
            round.Agenda.Calendar && round.NowPlaying.Apps.SequenceEqual(new[] { "Cider" }) && round.Countdowns[0].Valid);
        using (var hp = new PanelArt(1, 8))
        {
            var hf = new FrameState { View = ticking, DotCount = 3, DotIndex = 1 };
            List<Hit> hits = hp.Hits(hf);
            Check("agenda: a task's ring completes it, its title opens it, a ticked one offers Undo",
                hits.Any(h => h.Kind == HitKind.Undo && h.Index == 0) && hits.Any(h => h.Kind == HitKind.TaskDone && h.Index == 1) &&
                hits.Any(h => h.Kind == HitKind.TaskOpen && h.Index == 1) && hits.Any(h => h.Kind == HitKind.More) && hits.Any(h => h.Kind == HitKind.AddTask));
            Check("dots and both edges switch views", hits.Count(h => h.Kind == HitKind.Dot) == 3 && hits.Any(h => h.Kind == HitKind.EdgePrev) && hits.Any(h => h.Kind == HitKind.EdgeNext));
            hf.View = withCard;
            hits = hp.Hits(hf);
            Check("with a card open only its buttons act", hits.Any(h => h.Kind == HitKind.CardClose) && hits.Any(h => h.Kind == HitKind.CardDirections) &&
                !hits.Any(h => h.Kind is HitKind.EventOpen or HitKind.TaskDone));
            hf.View = Views.Clock(s, pm, new WeatherNow { Temp = 41, Code = 2, High = 52, Low = 33 });
            Check("clock: the date and the weather act, the digits between them don't",
                hp.Hits(hf).Any(h => h.Kind == HitKind.Date) && hp.Hits(hf).Any(h => h.Kind == HitKind.Weather) && !hp.HitTest(hf, hp.W / 2, hp.H / 2).Any);
        }
        List<AgendaItem> timedOnly = evs.Where(i => !i.AllDay).ToList(); // an all-day event rightly stays "today" until midnight
        ViewModel empty = Views.AgendaView(s, new AgendaData { Events = timedOnly, CalendarUtc = DateTime.UtcNow, TodoistConnected = true }, timedOnly, new DateTime(2026, 11, 17, 23, 0, 0));
        Check("an empty evening says so and previews tomorrow", empty.Rows.Count == 0 && empty.Detail == "Nothing else today" && empty.Footer != null && empty.Footer.Contains("Haircut"));
        bool credOk = Credentials.Write("selftest-value", "CarsGlance:SelfTest") && Credentials.Read("CarsGlance:SelfTest") == "selftest-value";
        credOk &= Credentials.Delete("CarsGlance:SelfTest") && Credentials.Read("CarsGlance:SelfTest") == null;
        Check("Credential Manager: save, read back, delete (test entry)", credOk);

        // covers: invisible overlays don't count (NVIDIA's full-screen overlay stopped the main monitor's animations)
        Check("NVIDIA's invisible overlay (layered, click-through, alpha 0) isn't a cover", Native.IsSeeThrough(0x080800A0, 0));
        Check("a click-through layered overlay isn't a cover", Native.IsSeeThrough(0x00080020, null));
        Check("an ordinary app window is a cover", !Native.IsSeeThrough(0x00000100, null) && !Native.IsSeeThrough(0x00080000, 255));

        // motion
        var mf = new FrameState();
        Motion.Transition(mf, 0, 1, false, 1);
        Check("switch starts with the new view off to the right and invisible", Math.Abs(mf.NewOffset.X - 64) < 0.01 && mf.NewAlpha == 0 && mf.OldAlpha == 1);
        Motion.Transition(mf, 0.3f, 1, false, 1);
        Check("mid-switch the two views barely overlap (no double exposure)", Math.Min(mf.OldAlpha, mf.NewAlpha) < 0.2f);
        Motion.Transition(mf, 0, -1, false, 1);
        Check("going back, it comes from the left", mf.NewOffset.X < 0 && mf.OldOffset.X >= 0);
        Motion.Transition(mf, 1, 1, false, 1);
        Check("switch ends exactly in place", mf.NewOffset == PointF.Empty && mf.NewAlpha == 1 && mf.OldAlpha == 0);
        Motion.Transition(mf, 0, 1, true, 1);
        Check("a new song rises from below", mf.NewOffset.X == 0 && mf.NewOffset.Y > 0);
        (float wl, float wr) = Motion.Worm(0, 2, 0, 0.4f);
        (float ml, float mr) = Motion.Worm(0, 2, 0.4f, 0.4f);
        (float el2, float er2) = Motion.Worm(0, 2, 1, 0.4f);
        Check("page pill starts on the old dot, stretches while moving, lands on the new one",
            Math.Abs(wl + 0.4f) < 0.001 && Math.Abs(wr - 0.4f) < 0.001 && mr - ml > 0.8f + 0.3f && Math.Abs(el2 - 1.6f) < 0.001 && Math.Abs(er2 - 2.4f) < 0.001);
        using (GraphicsPath play = Motion.PlayPause(PointF.Empty, 1, 0), pause = Motion.PlayPause(PointF.Empty, 1, 1))
        {
            RectangleF pb0 = play.GetBounds(), pb1 = pause.GetBounds();
            Check("play/pause morph: a triangle at 0, two bars at 1", Math.Abs(pb0.Left + 10) < 0.01 && Math.Abs(pb0.Right - 16) < 0.01 && Math.Abs(pb1.Left + 13.5f) < 0.01 && Math.Abs(pb1.Right - 13.5f) < 0.01);
        }
        using (var ba = new Bitmap(2, 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (var bb = new Bitmap(2, 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using (var bd = new Bitmap(2, 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        {
            ba.SetPixel(0, 0, Color.White); bb.SetPixel(0, 0, Color.White);       // same in both
            ba.SetPixel(1, 0, Color.Black); bb.SetPixel(1, 0, Color.FromArgb(255, 200, 200, 200));
            Motion.Blend(ba, bb, 0.5f, bd);
            Color same = bd.GetPixel(0, 0), mid = bd.GetPixel(1, 0);
            Check("crossfade keeps unchanged text fully solid, blends what changed", same.A == 255 && same.R == 255 && Math.Abs(mid.R - 100) <= 1);
        }
        Check("hover easing moves by time, not frames", Math.Abs(Motion.Toward(0, 1, 85, 170) - 0.5f) < 0.001 && Motion.Toward(0.9f, 1, 85, 170) == 1);

        return results;
    }
}

// ------------------------------------------------------------------ live data check (read-only, no windows)

// Fetches Todoist (with the saved token) and the Spark calendar once and writes what the widget would
// see: the response shape, then the parsed lines. The token is never written.
static class AgendaProbe
{
    public static int Run(string output)
    {
        var o = new List<string>();
        string token = Credentials.Read();
        o.Add("todoist token saved: " + (token != null));
        if (token != null)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.todoist.com/api/v1/tasks/filter?query=" + Uri.EscapeDataString("today | overdue") + "&limit=200");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage res = http.Send(req);
                string body = res.Content.ReadAsStringAsync().Result;
                o.Add("filter endpoint: HTTP " + (int)res.StatusCode);
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        o.Add("top-level keys: " + string.Join(", ", root.EnumerateObject().Select(p => p.Name + ":" + p.Value.ValueKind)));
                        if (root.TryGetProperty("results", out var results) && results.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            o.Add("raw tasks: " + results.GetArrayLength());
                            var first = results.EnumerateArray().FirstOrDefault();
                            if (first.ValueKind == System.Text.Json.JsonValueKind.Object)
                                o.Add("task keys: " + string.Join(", ", first.EnumerateObject().Select(p => p.Name)));
                            foreach (var t in results.EnumerateArray())
                            {
                                string due = t.TryGetProperty("due", out var d) ? d.GetRawText() : "(none)";
                                string pri = t.TryGetProperty("priority", out var p) ? p.GetRawText() : "?";
                                string dur = t.TryGetProperty("duration", out var du) ? du.GetRawText() : "(none)";
                                o.Add("  due " + due + "  priority " + pri + "  duration " + dur);
                            }
                        }
                    }
                    else o.Add("root is " + root.ValueKind);
                }
                List<AgendaItem> parsed = new TodoistService().FetchTasks(token, null).Result;
                o.Add("parsed tasks: " + parsed.Count);
                foreach (AgendaItem i in parsed)
                    o.Add($"  [{i.Start:ddd MM/dd HH:mm}] hasTime={i.HasTime} p{5 - i.Priority} :: {i.Title}");
            }
            catch (Exception ex) { o.Add("todoist error: " + ex.GetBaseException().Message); }
        }
        string cache = Path.Combine(Paths.DataDir, "calendar-cache.json");
        foreach (string c in new[] { cache })
        {
            if (!File.Exists(c)) continue;
            string raw = File.ReadAllText(c);
            o.Add("cache " + c + ": " + (raw.Length > 900 ? raw[..900] + "…" : raw).Replace("\n", " "));
            try
            {
                List<AgendaItem> evs = Agenda.ParseSpark(raw);
                o.Add("cache parsed events: " + evs.Count);
                foreach (AgendaItem e in evs) o.Add($"  [{e.Start:ddd MM/dd HH:mm} -> {e.End:HH:mm}] allDay={e.AllDay} hasTime={e.HasTime} :: {e.Title}");
                List<AgendaItem> tks = token != null ? new TodoistService().FetchTasks(token, null).Result : new List<AgendaItem>();
                List<AgendaItem> merged = Agenda.Merge(evs, tks);
                o.Add("today list at " + DateTime.Now.ToString("HH:mm") + ":");
                foreach (AgendaItem i in Agenda.Today(merged, DateTime.Now)) o.Add("  " + i.Source + " " + i.Start.ToString("MM/dd HH:mm") + " " + i.Title);
            }
            catch (Exception ex) { o.Add("cache parse error: " + ex.Message); }
            break;
        }
        try
        {
            string exe = CalendarService.FindSpark("");
            o.Add("spark.exe: " + (exe ?? "(not found)"));
            if (exe != null)
            {
                var psi = new ProcessStartInfo(exe, "events --json --week") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
                using Process p = Process.Start(psi);
                string json = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                o.Add("spark raw: " + (json.Length > 600 ? json[..600] + "…" : json).Replace("\n", " "));
                try { o.Add("spark parsed events: " + Agenda.ParseSpark(json).Count); }
                catch (Exception ex) { o.Add("spark: " + ex.Message); }
            }
        }
        catch (Exception ex) { o.Add("spark error: " + ex.Message); }
        File.WriteAllLines(output, o);
        return 0;
    }
}

// ------------------------------------------------------------------ offline preview (no windows)

static class Preview
{
    // Previews and README screenshots: a generic city and a made-up countdown, nothing personal.
    const string SampleSettings = "{ \"weather\": { \"place\": \"Kansas City\", \"latitude\": 39.10, \"longitude\": -94.58 }, " +
        "\"countdowns\": [ { \"name\": \"Summer trip\", \"at\": \"2027-06-01T08:00\", \"pinned\": true, \"doneText\": \"BON VOYAGE\" } ] }";

    public static int Run(string[] args)
    {
        string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        string output = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "CarsGlance-preview.png");
        string view = Arg("--view", "clock");
        DateTime at = DateTime.ParseExact(Arg("--at", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        int height = int.Parse(Arg("--height", "1440"), CultureInfo.InvariantCulture);
        string bgFile = Arg("--bg", null);
        Settings settings = Settings.Parse(SampleSettings);
        WeatherNow weather = Array.IndexOf(args, "--weather") >= 0 ? new WeatherNow { Temp = 41, Code = 2, High = 52, Low = 33, RainPct = 20, FetchedUtc = DateTime.UtcNow } : null;

        // --sample: a made-up day (relative to --at) for the agenda and next up views
        AgendaData agenda = null;
        if (Array.IndexOf(args, "--sample") >= 0)
        {
            DateTime d = at.Date;
            AgendaItem Ev(string title, int h, int m, int mins, string where) => new()
                { Source = AgendaSource.Calendar, Id = title, Title = title, Start = d.AddHours(h).AddMinutes(m), End = d.AddHours(h).AddMinutes(m + mins), HasTime = true, Where = where };
            AgendaItem Tk(string title, int priority, int daysAgo, string group) => new()
                { Source = AgendaSource.Todoist, Id = title, Title = title, Start = d.AddDays(-daysAgo), End = d.AddDays(-daysAgo), Priority = priority, Group = group };
            agenda = new AgendaData
            {
                Events = new List<AgendaItem>
                {
                    Ev("Dentist appointment", at.Hour, at.Minute + (Array.IndexOf(args, "--started") >= 0 ? -2 : 12), 60, "Main Street Dental"),
                    Ev("Team call about the spring launch", 18, 30, 30, ""),
                    new() { Source = AgendaSource.Calendar, Id = "bday", Title = "Mom's birthday", Start = d, End = d.AddDays(1), AllDay = true },
                },
                Tasks = new List<AgendaItem> { Tk("Pay phone bill", 4, 1, "Bills"), Tk("Pick up prescription", 3, 0, "Errands") },
                CalendarUtc = DateTime.UtcNow,
                TodoistConnected = true,
            };
            if (Array.IndexOf(args, "--empty") >= 0)
                agenda = new AgendaData { Events = new List<AgendaItem> { Ev("Haircut", 33, 0, 45, "") }, CalendarUtc = DateTime.UtcNow };
        }

        if (view == "clock") settings.FirstView = "clock";
        NowPlayingInfo media = null;
        if (view == "nowplaying" || Array.IndexOf(args, "--music") >= 0)
        {
            if (Array.IndexOf(args, "--live") >= 0)
            {
                media = new MediaService().WaitForSong(settings.NowPlaying, 6000);
                File.WriteAllText(output + ".txt", media == null ? "no song from: " + string.Join(", ", settings.NowPlaying.Apps) :
                    $"app {media.AppId}\ntitle {media.Title}\nartist {media.Artist}\nalbum {media.Album}\nplaying {media.Playing}\n" +
                    $"position {media.Position} at {media.PositionUtc:HH:mm:ss.fff}Z (now {DateTime.UtcNow:HH:mm:ss.fff}Z)\nduration {media.Duration}\n" +
                    $"controls prev {media.CanPrevious} playpause {media.CanPlayPause} next {media.CanNext}\n" +
                    $"art {(media.Art == null ? "none" : media.Art.Size + " px, tint " + (media.Art.Tint?.ToString() ?? "none"))}\n");
            }
            media ??= new NowPlayingInfo
            {
                AppId = "Music", Title = "Midnight Drive", Artist = "The Example Band", Album = "Night Lines",
                Playing = true, CanPrevious = true, CanPlayPause = true, CanNext = true,
                Duration = TimeSpan.FromSeconds(226), Position = TimeSpan.FromSeconds(83), PositionUtc = DateTime.UtcNow,
            };
            string artFile = Arg("--art", null);
            if (artFile != null) media.Art = ArtFromFile(artFile);
            string title = Arg("--title", null);
            if (title != null) media.Title = title;
            if (Array.IndexOf(args, "--paused") >= 0) { media.Playing = false; media.PausedSinceUtc = at.ToUniversalTime(); }
        }

        // --ticked: the first task is ticked (Undo showing); --card: the first event's card is open; --hint: the first-run pill
        var ui = new AgendaUi();
        if (agenda != null && Array.IndexOf(args, "--ticked") >= 0 && agenda.Tasks?.Count > 0) ui.Completing[agenda.Tasks[0].Id] = DateTime.UtcNow.AddSeconds(5);
        if (agenda != null && Array.IndexOf(args, "--card") >= 0 && agenda.Events?.Count > 0) ui.CardId = agenda.Events[0].Id;
        List<ViewModel> views = Views.Build(settings, at, weather, media, agenda, ui);
        int IndexOf(string name)
        {
            ViewKind k = name switch
            {
                "nowplaying" => ViewKind.NowPlaying, "countdown" or "done" => ViewKind.Countdown,
                "agenda" => ViewKind.Agenda, "nextup" => ViewKind.NextUp, "home" => ViewKind.Home, _ => ViewKind.Clock,
            };
            return Math.Max(0, views.FindIndex(v => v.Kind == k));
        }
        int index = IndexOf(view);
        ViewModel vm = views[index];
        float s = height / 1440f;

        using var art = new PanelArt(s, (int)Math.Round(8 * s));
        art.EnsureContent(vm);
        FrameState f = NewFrame(vm, views.Count, index);
        if (Array.IndexOf(args, "--hint") >= 0) f.HintText = "SCROLL OR USE  ‹ ›  TO SWITCH   ·   RIGHT-CLICK FOR MORE";
        string hoverKind = Arg("--hover-hit", null); // e.g. TaskDone:1, Weather, EdgeNext: the pointer over that spot
        if (hoverKind != null)
        {
            string[] hk = hoverKind.Split(':');
            int hi = hk.Length > 1 ? int.Parse(hk[1], CultureInfo.InvariantCulture) : -1;
            f.HoverHit = art.Hits(f).FirstOrDefault(h => h.Kind.ToString() == hk[0] && (hi < 0 || h.Index == hi));
            f.HoverT = 1;
        }
        int hoverButton = vm.Kind == ViewKind.NowPlaying ? int.Parse(Arg("--hover", "-1"), CultureInfo.InvariantCulture) : -1;

        // --transition <view>: a strip of frames switching to that view (what the live widget animates)
        // --buttons: a strip of the play/pause morph, hover ring growing in, and a click dip
        string to = Arg("--transition", null);
        bool buttons = Array.IndexOf(args, "--buttons") >= 0;
        var frames = new List<Bitmap>();
        try
        {
            if (to != null)
            {
                int toIndex = IndexOf(to);
                foreach (float t in new[] { 0f, 0.12f, 0.25f, 0.4f, 0.6f, 1f })
                {
                    FrameState old = NewFrame(vm, views.Count, index);
                    art.EnsureContent(vm);
                    using Bitmap snap = art.Snapshot(old);
                    FrameState nf = NewFrame(views[toIndex], views.Count, toIndex);
                    art.EnsureContent(views[toIndex]);
                    nf.FadeFrom = snap;
                    Motion.Transition(nf, t, toIndex >= index ? 1 : -1, false, s);
                    nf.DotFrom = index; nf.DotTo = toIndex; nf.DotT = t;
                    frames.Add(Framed(art, nf, bgFile, s));
                    nf.FadeFrom = null;
                }
            }
            else if (Array.IndexOf(args, "--roll") >= 0 && vm.Values != null)
            {
                // the digits ticking over from one second before --at to --at
                List<ViewModel> before = Views.Build(settings, at.AddSeconds(-1), weather, media, agenda);
                ViewModel pv = before.FirstOrDefault(v => v.Kind == vm.Kind) ?? vm;
                foreach (float t in new[] { 0f, 0.15f, 0.3f, 0.45f, 0.6f, 0.8f, 1f })
                {
                    FrameState rf = NewFrame(vm, views.Count, index);
                    for (int i = 0; i < vm.Values.Length; i++)
                    {
                        rf.Prev[i] = pv.Values != null && i < pv.Values.Length ? pv.Values[i] : null;
                        rf.ColT[i] = rf.Prev[i] == rf.Cur[i] ? 1 : t;
                    }
                    frames.Add(Framed(art, rf, bgFile, s));
                }
            }
            else if (buttons && vm.Kind == ViewKind.NowPlaying)
            {
                foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                {
                    f.PlayMorph = t;                 // play -> pause
                    f.Ring[1] = t;                   // ring growing in on hover
                    f.Press[2] = Motion.Bump(t);     // next: click dip
                    frames.Add(Framed(art, f, bgFile, s));
                }
            }
            else
            {
                if (hoverButton >= 0) f.Ring[hoverButton] = 1;
                frames.Add(Framed(art, f, bgFile, s, hoverButton >= 0));
            }
            using var sheet = new Bitmap(frames[0].Width, frames.Sum(b => b.Height));
            using (Graphics sg = Graphics.FromImage(sheet))
            {
                int y = 0;
                foreach (Bitmap b in frames) { sg.DrawImageUnscaled(b, 0, y); y += b.Height; }
            }
            sheet.Save(output, ImageFormat.Png);
        }
        finally { foreach (Bitmap b in frames) b.Dispose(); }
        return 0;
    }

    static FrameState NewFrame(ViewModel vm, int count, int index)
    {
        var f = new FrameState { View = vm, DotCount = count, DotIndex = index, DotFrom = index, DotTo = index };
        for (int i = 0; i < 4; i++) f.Cur[i] = vm.Values != null && i < vm.Values.Length ? vm.Values[i] : null;
        if (vm.Kind == ViewKind.NowPlaying)
        {
            f.Progress = Views.MediaTimes(vm.Media, DateTime.UtcNow, out f.Elapsed, out f.Left);
            f.PlayMorph = vm.Media.Playing ? 1 : 0;
        }
        return f;
    }

    // One panel over a stand-in for the blurred wallpaper. hoverLater: draw the buttons again on their
    // own afterwards, the way the live widget redraws only that row when the pointer arrives.
    static Bitmap Framed(PanelArt art, FrameState f, string bgFile, float s, bool hoverLater = false)
    {
        int pad = (int)Math.Round(140 * s);
        using var panel = new Bitmap(art.W, art.H, PixelFormat.Format32bppPArgb);
        var canvas = new Bitmap(art.W + 2 * pad, art.H + 2 * pad);
        using Graphics g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var all = new Rectangle(0, 0, canvas.Width, canvas.Height);
        if (bgFile != null) using (Image bg = Image.FromFile(bgFile)) g.DrawImage(bg, all);
        else using (var bgb = new LinearGradientBrush(all, Color.FromArgb(12, 67, 150), Color.FromArgb(2, 9, 20), 35f)) g.FillRectangle(bgb, all);

        var pr = new Rectangle(pad, pad, art.W, art.H);
        using (Bitmap under = canvas.Clone(pr, canvas.PixelFormat))
        using (var tiny = new Bitmap(under, Math.Max(1, art.W / 18), Math.Max(1, art.H / 18)))
        using (GraphicsPath clip = Art.Rounded(pr, art.Radius))
        {
            g.SetClip(clip);
            g.DrawImage(tiny, new Rectangle(pad - 12, pad - 12, art.W + 24, art.H + 24)); // stands in for Windows' blur
            using (Graphics pg = Art.Canvas(panel))
            {
                if (hoverLater)
                {
                    float[] rings = (float[])f.Ring.Clone();
                    Array.Clear(f.Ring);
                    art.Render(pg, art.Full, f);
                    Array.Copy(rings, f.Ring, 3);
                    art.Render(pg, art.ControlsRect, f);
                }
                else art.Render(pg, art.Full, f);
            }
            g.DrawImage(panel, pad, pad);
            g.ResetClip();
        }
        return canvas;
    }

    static AlbumArt ArtFromFile(string file)
    {
        using var src = new Bitmap(file);
        int side = Math.Min(src.Width, src.Height), size = Math.Min(512, side);
        using var sq = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(sq))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, size, size), new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        }
        var d = sq.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[size * size];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
        sq.UnlockBits(d);
        return new AlbumArt(size, px, 1, "file");
    }
}
