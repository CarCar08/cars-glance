// Settings, views and the rules that pick which view is on screen.
using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glance;

// Where Glance keeps its own files: %LOCALAPPDATA%\CARS Glance (settings, caches, logs, downloaded updates).
// Never the install folder, which an update replaces whole. GLANCE_DATA_DIR overrides it (tests, dev runs).
static class Paths
{
    public const string FolderName = "CARS Glance";
    static string dataDir;

    public static string DataDir
    {
        get
        {
            if (dataDir != null) return dataDir;
            string dir = Environment.GetEnvironmentVariable("GLANCE_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
            try { Directory.CreateDirectory(dir); } catch { }
            return dataDir = dir;
        }
        set => dataDir = value;
    }

    public static string AppDir => AppContext.BaseDirectory.TrimEnd('\\', '/');
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DefaultSettingsFile => Path.Combine(AppDir, "settings.default.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string QuitFlag => Path.Combine(DataDir, "quit.flag");
}

// A small rotating log: logs\glance.log, moved to glance.1.log at 512 KB (two files at most).
static class Log
{
    static readonly object Gate = new();

    public static string File => Path.Combine(Paths.LogsDir, "glance.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogsDir);
                var info = new FileInfo(File);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    string old = Path.Combine(Paths.LogsDir, "glance.1.log");
                    System.IO.File.Copy(File, old, true);
                    System.IO.File.WriteAllText(File, "");
                }
                System.IO.File.AppendAllText(File, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
            }
        }
        catch { }
    }
}

// ------------------------------------------------------------------ settings

sealed class WeatherSetting
{
    public bool Enabled { get; set; } = true;
    public string Place { get; set; } = "";
    public double? Latitude { get; set; }   // city level is plenty: Glance never needs a street address
    public double? Longitude { get; set; }
    public bool Fahrenheit { get; set; } = true;
    [JsonIgnore] public bool HasLocation => Latitude is double la && Longitude is double lo && Math.Abs(la) <= 90 && Math.Abs(lo) <= 180;
}

sealed class CountdownSetting
{
    public string Name { get; set; } = "";
    public string At { get; set; } = "";
    public bool Pinned { get; set; }
    public string DoneText { get; set; } = "IT'S TIME";
    public double ShowDoneHours { get; set; } = 24;
    [JsonIgnore] public DateTime AtLocal;
    [JsonIgnore] public bool Valid;
}

sealed class NowPlayingSetting
{
    public bool Enabled { get; set; } = true;
    public List<string> Apps { get; set; } = new();   // matched against the app's Windows media ID; empty = any player
    public double KeepAfterPauseMinutes { get; set; } = 5;
}

sealed class Settings
{
    public bool Use24HourClock { get; set; }
    public int ManualViewMinutes { get; set; } = 5;
    public WeatherSetting Weather { get; set; } = new();
    public NowPlayingSetting NowPlaying { get; set; } = new();
    public AgendaSetting Agenda { get; set; } = new();
    public List<CountdownSetting> Countdowns { get; set; } = new();

    public static string FilePath => Paths.SettingsFile;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Parse(string json)
    {
        Settings s = JsonSerializer.Deserialize<Settings>(json, Json) ?? new Settings();
        s.Weather ??= new WeatherSetting();
        s.NowPlaying ??= new NowPlayingSetting();
        s.NowPlaying.Apps ??= new List<string>();
        s.Agenda ??= new AgendaSetting();
        s.Agenda.NextUpMinutes = Math.Clamp(s.Agenda.NextUpMinutes, 1, 120);
        s.Agenda.NextUpHoldMinutes = Math.Clamp(s.Agenda.NextUpHoldMinutes, 0, 60);
        s.Countdowns ??= new List<CountdownSetting>();
        foreach (CountdownSetting c in s.Countdowns)
        {
            c.Valid = !string.IsNullOrWhiteSpace(c.Name) &&
                      DateTime.TryParseExact(c.At, new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" },
                          CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out c.AtLocal);
            if (c.Valid) c.AtLocal = DateTime.SpecifyKind(c.AtLocal, DateTimeKind.Local);
            if (string.IsNullOrWhiteSpace(c.DoneText)) c.DoneText = "IT'S TIME";
        }
        if (s.ManualViewMinutes < 1) s.ManualViewMinutes = 1;
        return s;
    }

    // settings.json in the data folder; created from the install folder's settings.default.json the first time.
    public static Settings Load()
    {
        try
        {
            string file = FilePath;
            if (!File.Exists(file))
            {
                string def = Paths.DefaultSettingsFile;
                if (File.Exists(def)) File.Copy(def, file);
            }
            if (File.Exists(file)) return Parse(File.ReadAllText(file));
        }
        catch (Exception ex) { Log.Write("settings: " + ex.Message); }
        return null;
    }
}

// ------------------------------------------------------------------ time left

struct Remaining
{
    public bool Done;
    public long Days;
    public int Hours, Minutes, Seconds;
    public double TotalHours;

    public string[] Values() => new[] { Days.ToString(CultureInfo.InvariantCulture), Hours.ToString("00"), Minutes.ToString("00"), Seconds.ToString("00") };

    public string[] Labels() => new[] { Days == 1 ? "DAY" : "DAYS", Hours == 1 ? "HOUR" : "HOURS", Minutes == 1 ? "MINUTE" : "MINUTES", Seconds == 1 ? "SECOND" : "SECONDS" };

    public static Remaining Compute(DateTime targetUtc, DateTime nowUtc)
    {
        Remaining r = new Remaining();
        double secondsLeft = (targetUtc - nowUtc).TotalSeconds;
        r.TotalHours = secondsLeft / 3600.0;
        if (secondsLeft <= 0) { r.Done = true; return r; }
        long total = (long)Math.Ceiling(secondsLeft); // never shows 0:00:00:00 before the moment
        r.Days = total / 86400;
        r.Hours = (int)(total % 86400 / 3600);
        r.Minutes = (int)(total % 3600 / 60);
        r.Seconds = (int)(total % 60);
        return r;
    }
}

// ------------------------------------------------------------------ views

enum ViewKind { Clock, Countdown, NowPlaying, Agenda, NextUp }

enum RowMarker { None, Bar, Ring }

// One line of the agenda: time on the left, a marker (bar = event, ring = task), the title, a faint note.
sealed record AgendaRow(string Time, bool Alert, RowMarker Marker, Color MarkerColor, string Title, string Right, bool More = false)
{
    public string Key => string.Join("\u0002", Time, Alert, Marker, MarkerColor.ToArgb(), Title, Right, More);
}

sealed class ViewModel
{
    public string Id = "";
    public ViewKind Kind;
    public string Title = "";
    public string[] Values;   // rolling digit columns, or null
    public string[] Labels;   // under the columns, or null
    public string Suffix;     // AM / PM
    public string Big;        // replaces the digits, e.g. OUT NOW
    public string Footer;     // date line or weather line
    public bool Pinned;
    public double HoursLeft = double.MaxValue;
    public NowPlayingInfo Media; // Now Playing only
    public bool Claim;           // Now Playing: playing, or paused only a few minutes ago
    public List<AgendaRow> Rows; // Agenda: up to four lines
    public string Detail;        // Next up: the event's name. Agenda: the empty-day line
    public string Note;          // Agenda: the right-hand summary ("2 EVENTS · 3 TASKS" or a status)

    // Everything except the digits and the song's progress: when this changes the static layer is redrawn.
    public string StaticKey => string.Join("|", Kind, Title, Labels == null ? "" : string.Join(",", Labels), Suffix, Big, Footer, Values?.Length ?? 0, Media?.StaticKey,
        Rows == null ? "" : string.Join("\u0003", Rows.Select(r => r.Key)), Detail, Note);

    // A change here crossfades, like switching views: a new song or its cover arriving.
    // A countdown reaching zero ("OUT NOW") also transitions, rising into place.
    public string FadeKey => Media == null ? Id + (Big != null ? "|done" : "") : Id + "|" + Media.TrackKey + "|" + (Media.Art?.Version ?? 0);
}

static class Views
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static List<ViewModel> Build(Settings s, DateTime nowLocal, WeatherNow weather, NowPlayingInfo media, AgendaData agenda = null)
    {
        var list = new List<ViewModel> { Clock(s, nowLocal, weather) };
        if (s.Agenda.Enabled && agenda != null)
        {
            List<AgendaItem> merged = Agenda.Merge(agenda.Events, agenda.Tasks);
            AgendaItem next = Agenda.NextUp(merged, nowLocal, s.Agenda.NextUpMinutes, s.Agenda.NextUpHoldMinutes);
            if (next != null) list.Add(NextUp(next, nowLocal));
            list.Add(AgendaView(s, agenda, merged, nowLocal));
        }
        ViewModel np = NowPlaying(s, media, nowLocal.ToUniversalTime());
        if (np != null) list.Add(np);
        foreach (CountdownSetting c in s.Countdowns.Where(c => c.Valid).OrderBy(c => c.AtLocal))
        {
            ViewModel vm = Countdown(c, nowLocal);
            if (vm != null) list.Add(vm);
        }
        return list;
    }

    public static ViewModel Clock(Settings s, DateTime now, WeatherNow weather)
    {
        return new ViewModel
        {
            Id = "clock",
            Kind = ViewKind.Clock,
            Title = now.ToString("dddd, MMMM d", En).ToUpperInvariant(),
            Values = new[] { s.Use24HourClock ? now.ToString("HH", En) : now.ToString("%h", En), now.ToString("mm", En), now.ToString("ss", En) },
            Suffix = s.Use24HourClock ? null : now.ToString("tt", En),
            Footer = s.Weather.Enabled && weather != null ? WeatherService.Line(weather) : null,
        };
    }

    public static ViewModel Countdown(CountdownSetting c, DateTime now)
    {
        Remaining rem = Remaining.Compute(c.AtLocal.ToUniversalTime(), now.ToUniversalTime());
        if (rem.Done && (now - c.AtLocal).TotalHours >= c.ShowDoneHours) return null; // finished and shown long enough
        var vm = new ViewModel
        {
            Id = "countdown:" + c.Name,
            Kind = ViewKind.Countdown,
            Title = c.Name.ToUpperInvariant(),
            Pinned = c.Pinned,
            HoursLeft = rem.TotalHours,
        };
        string date = c.AtLocal.ToString("dddd, MMMM d", En).ToUpperInvariant();
        if (rem.Done)
        {
            vm.Big = c.DoneText.ToUpperInvariant();
            vm.Footer = date;
        }
        else
        {
            vm.Values = rem.Values();
            vm.Labels = rem.Labels();
            vm.Footer = c.AtLocal.TimeOfDay == TimeSpan.Zero && c.At.Length <= 10
                ? date
                : date + "  ·  " + c.AtLocal.ToString("h:mm tt", En);
        }
        return vm;
    }

    // From a few minutes before a timed event (or Todoist task with a time) until a few minutes after
    // it starts: a rolling mm:ss, then "NOW · STARTED" (it rises into place, like OUT NOW).
    public static ViewModel NextUp(AgendaItem item, DateTime now)
    {
        Remaining r = Remaining.Compute(item.Start.ToUniversalTime(), now.ToUniversalTime());
        long minutes = r.Days * 1440 + r.Hours * 60 + r.Minutes;
        string footer = Agenda.Range(item);
        if (!string.IsNullOrWhiteSpace(item.Where)) footer += "  ·  " + item.Where;
        var vm = new ViewModel
        {
            Id = "nextup:" + item.Source + ":" + item.Id + ":" + item.Start.ToString("o", CultureInfo.InvariantCulture),
            Kind = ViewKind.NextUp,
            Title = "NEXT UP",
            Detail = item.Title,
            Footer = footer.ToUpperInvariant(),
        };
        if (r.Done)
        {
            vm.Title = "NOW";
            vm.Big = "STARTED";
        }
        else
        {
            vm.Values = new[] { minutes.ToString("00", CultureInfo.InvariantCulture), r.Seconds.ToString("00", CultureInfo.InvariantCulture) };
            vm.Labels = new[] { minutes == 1 ? "MINUTE" : "MINUTES", r.Seconds == 1 ? "SECOND" : "SECONDS" };
        }
        return vm;
    }

    static readonly Color[] TodoistPriority =
    {
        Color.FromArgb(150, 255, 255, 255), // p4
        Color.FromArgb(255, 70, 140, 235),  // p3 blue
        Color.FromArgb(255, 240, 150, 30),  // p2 orange
        Color.FromArgb(255, 255, 40, 28),   // p1 red
    };

    public static AgendaRow Row(AgendaItem i, DateTime now)
    {
        DateTime today = now.Date;
        if (i.Source == AgendaSource.Calendar)
        {
            bool on = i.HasTime && i.Start <= now && i.End > now;
            string time = i.AllDay ? "ALL DAY" : on ? "NOW" : Agenda.Time(i.Start);
            return new AgendaRow(time, on, RowMarker.Bar, i.Color ?? Color.FromArgb(255, 255, 40, 28), i.Title, i.Where);
        }
        bool overdue = i.Start.Date < today, late = overdue || (i.HasTime && i.Start < now);
        string when = overdue ? i.Start.ToString("MMM d", En).ToUpperInvariant() : i.HasTime ? Agenda.Time(i.Start) : "";
        string group = i.Group.Equals("Inbox", StringComparison.OrdinalIgnoreCase) ? "" : i.Group;
        return new AgendaRow(when, late, RowMarker.Ring, TodoistPriority[Math.Clamp(i.Priority, 1, 4) - 1], i.Title, group);
    }

    public static ViewModel AgendaView(Settings s, AgendaData d, List<AgendaItem> merged, DateTime now)
    {
        const int MaxRows = 4;
        List<AgendaItem> today = Agenda.Today(merged, now);
        var rows = today.Take(today.Count > MaxRows ? MaxRows - 1 : MaxRows).Select(i => Row(i, now)).ToList();
        if (today.Count > MaxRows) rows.Add(new AgendaRow("", false, RowMarker.None, Color.Empty, "+ " + (today.Count - MaxRows + 1) + " more", "", true));

        var vm = new ViewModel { Id = "agenda", Kind = ViewKind.Agenda, Title = "TODAY", Rows = rows };
        int events = today.Count(i => i.Source == AgendaSource.Calendar), tasks = today.Count - events;
        var parts = new List<string>();
        if (events > 0) parts.Add(events + (events == 1 ? " EVENT" : " EVENTS"));
        if (tasks > 0) parts.Add(tasks + (tasks == 1 ? " TASK" : " TASKS"));
        // a quiet note when the calendar is from the saved copy (Spark closed) or a source has a problem
        if (s.Agenda.Calendar && d.CalendarStatus != null && d.CalendarUtc != default && (DateTime.UtcNow - d.CalendarUtc).TotalMinutes > 10)
            parts.Add("CALENDAR AS OF " + Agenda.Time(d.CalendarUtc.ToLocalTime()).ToUpperInvariant());
        if (s.Agenda.Todoist && d.TasksStatus != null) parts.Add(d.TasksStatus.ToUpperInvariant());
        vm.Note = string.Join("  ·  ", parts);

        if (today.Count == 0)
        {
            vm.Detail = "Nothing else today";
            AgendaItem first = merged.Where(i => i.Source == AgendaSource.Calendar && i.Start.Date == now.Date.AddDays(1))
                                     .OrderBy(i => i.AllDay).ThenBy(i => i.Start).FirstOrDefault();
            if (first != null) vm.Footer = "TOMORROW  ·  " + (first.AllDay ? "ALL DAY" : Agenda.Time(first.Start).ToUpperInvariant()) + "  ·  " + first.Title;
            var hints = new List<string>();
            if (s.Agenda.Calendar && d.CalendarUtc == default) hints.Add("Open Spark to show your calendar");
            if (s.Agenda.Todoist && !d.TodoistConnected) hints.Add("Connect Todoist from the Start menu");
            if (hints.Count > 0) vm.Suffix = string.Join("   ·   ", hints);
        }
        return vm;
    }

    // Shown while the player has a song; it claims the screen while playing and for a few minutes after a pause.
    public static ViewModel NowPlaying(Settings s, NowPlayingInfo m, DateTime nowUtc)
    {
        if (!s.NowPlaying.Enabled || m == null || string.IsNullOrWhiteSpace(m.Title)) return null;
        return new ViewModel
        {
            Id = "nowplaying",
            Kind = ViewKind.NowPlaying,
            Title = m.Playing ? "NOW PLAYING" : "PAUSED",
            Media = m,
            Claim = m.Playing || (nowUtc - m.PausedSinceUtc).TotalMinutes < s.NowPlaying.KeepAfterPauseMinutes,
        };
    }

    // The progress bar (0..1, or -1 when the player gives no length) and the times either side of it.
    public static float MediaTimes(NowPlayingInfo m, DateTime utc, out string elapsed, out string left)
    {
        elapsed = left = null;
        if (m == null || !m.HasTimeline) return -1;
        TimeSpan p = m.PositionAt(utc);
        long e = (long)Math.Floor(p.TotalSeconds), d = (long)Math.Round(m.Duration.TotalSeconds);
        elapsed = NowPlayingInfo.Format(e);
        left = "-" + NowPlayingInfo.Format(Math.Max(0, d - e));
        return (float)Math.Clamp(p.TotalSeconds / m.Duration.TotalSeconds, 0, 1);
    }
}

// The smart rules: any countdown from its final hour until 10 minutes after zero (so a launch is never
// missed behind music), then Next up (an event starting within 15 minutes), then Now
// Playing while music plays (and a few minutes after a pause), then a pinned countdown, then any countdown
// in its final 24 hours (or showing its "done" text), otherwise the clock. The agenda is a click away.
static class Smart
{
    public const double FinalHours = 1, HoldDoneHours = 10 / 60.0;

    public static int Pick(List<ViewModel> views)
    {
        for (int i = 0; i < views.Count; i++)
            if (views[i].Kind == ViewKind.Countdown && views[i].HoursLeft > -HoldDoneHours && views[i].HoursLeft <= FinalHours) return i;
        for (int i = 0; i < views.Count; i++)
            if (views[i].Kind == ViewKind.NextUp) return i;
        for (int i = 0; i < views.Count; i++)
            if (views[i].Kind == ViewKind.NowPlaying && views[i].Claim) return i;
        for (int i = 0; i < views.Count; i++)
            if (views[i].Kind == ViewKind.Countdown && views[i].Pinned) return i;
        for (int i = 0; i < views.Count; i++)
            if (views[i].Kind == ViewKind.Countdown && views[i].HoursLeft <= 24) return i;
        return 0;
    }
}

// ------------------------------------------------------------------ weather (Open-Meteo, no account)

sealed class WeatherNow
{
    public double Temp, High, Low;
    public int Code, RainPct;
    public DateTime FetchedUtc;
}

sealed class WeatherService
{
    static readonly HttpClient Http = CreateClient();
    volatile WeatherNow latest;
    DateTime nextFetchUtc = DateTime.MinValue;
    volatile bool fetching;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }

    // Generic on purpose: no name, no e-mail, nothing that identifies whoever runs it.
    public const string UserAgent = "CarsGlance/1.0 (+https://github.com/CarCar08/cars-glance)";

    string lastPlace;

    // After sleep, a time-zone change or a new city: fetch on the next tick instead of waiting.
    public void Force() => nextFetchUtc = DateTime.MinValue;

    // Shown only while reasonably fresh.
    public WeatherNow Latest
    {
        get
        {
            WeatherNow w = latest;
            return w != null && (DateTime.UtcNow - w.FetchedUtc).TotalHours < 2 ? w : null;
        }
    }

    public void Tick(WeatherSetting ws)
    {
        if (!ws.Enabled || !ws.HasLocation) { latest = null; return; }
        string place = ws.Latitude + "," + ws.Longitude + "," + ws.Fahrenheit;
        if (place != lastPlace) { lastPlace = place; latest = null; nextFetchUtc = DateTime.MinValue; }
        if (fetching || DateTime.UtcNow < nextFetchUtc) return;
        fetching = true;
        nextFetchUtc = DateTime.UtcNow.AddMinutes(15);
        Task.Run(async () =>
        {
            try { latest = await Fetch(ws); }
            catch (Exception ex)
            {
                Log.Write("weather: " + ex.Message);
                nextFetchUtc = DateTime.UtcNow.AddMinutes(2);
            }
            finally { fetching = false; }
        });
    }

    static async Task<WeatherNow> Fetch(WeatherSetting ws)
    {
        string url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0:F2}&longitude={1:F2}" +
            "&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            "&temperature_unit={2}&timezone=auto&forecast_days=1",
            ws.Latitude.Value, ws.Longitude.Value, ws.Fahrenheit ? "fahrenheit" : "celsius");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        JsonElement cur = doc.RootElement.GetProperty("current");
        JsonElement daily = doc.RootElement.GetProperty("daily");
        JsonElement rain = daily.GetProperty("precipitation_probability_max")[0];
        return new WeatherNow
        {
            Temp = cur.GetProperty("temperature_2m").GetDouble(),
            Code = cur.GetProperty("weather_code").GetInt32(),
            High = daily.GetProperty("temperature_2m_max")[0].GetDouble(),
            Low = daily.GetProperty("temperature_2m_min")[0].GetDouble(),
            RainPct = rain.ValueKind == JsonValueKind.Number ? rain.GetInt32() : 0,
            FetchedUtc = DateTime.UtcNow,
        };
    }

    public static string Describe(int code) => code switch
    {
        0 => "CLEAR",
        1 => "MOSTLY CLEAR",
        2 => "PARTLY CLOUDY",
        3 => "CLOUDY",
        45 or 48 => "FOG",
        >= 51 and <= 57 => "DRIZZLE",
        61 => "LIGHT RAIN",
        63 => "RAIN",
        65 => "HEAVY RAIN",
        66 or 67 => "FREEZING RAIN",
        >= 71 and <= 77 => "SNOW",
        >= 80 and <= 82 => "SHOWERS",
        85 or 86 => "SNOW SHOWERS",
        >= 95 => "THUNDERSTORMS",
        _ => "",
    };

    public static string Line(WeatherNow w)
    {
        string desc = Describe(w.Code);
        string temp = Math.Round(w.Temp).ToString(CultureInfo.InvariantCulture) + "°";
        string now = desc.Length > 0 ? temp + "  " + desc : temp;
        return now + "   ·   H " + Math.Round(w.High).ToString(CultureInfo.InvariantCulture) + "°  L " +
               Math.Round(w.Low).ToString(CultureInfo.InvariantCulture) + "°   ·   " +
               w.RainPct.ToString(CultureInfo.InvariantCulture) + "% RAIN";
    }
}
