// Agenda + Next up: the platform-neutral half (settings, items, parsing, merging, ordering).
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance;

sealed class AgendaSetting
{
    public bool Enabled { get; set; } = true;
    public bool Calendar { get; set; } = false;  // Apple Calendar through Spark Desktop (only if you use Spark)
    public bool Todoist { get; set; } = true;    // tasks due today and overdue (connect in Settings)
    public int NextUpMinutes { get; set; } = 15;
    public int NextUpHoldMinutes { get; set; } = 5; // stays up this long after the start
    public bool NextUp { get; set; } = true;     // the Next up view (the agenda still shows the event)
    public string SparkPath { get; set; } = "";  // blank: find spark.exe automatically
}

enum AgendaSource { Calendar, Todoist }

sealed class AgendaItem
{
    public AgendaSource Source;
    public string Id = "", Title = "", Where = "", Group = ""; // Where: location; Group: calendar or project
    public DateTime Start, End;   // local time; date-only items start at midnight
    public bool AllDay;           // calendar: all-day event
    public bool HasTime;          // false for all-day events and date-only tasks
    public int Priority = 1;      // Todoist: 4 = p1 (highest) ... 1 = p4
    public Color? Color;          // calendar colour when Spark reports one
    public bool AlsoInTodoist;    // an event that matched a Todoist task (shown once)
    public string Notes = "";     // event notes / task description (for the detail card and meeting links)
}

// What you're doing with the agenda right now, shared by every display: which page, which event's card is
// open, which tasks you just ticked (they wait 5 seconds for Undo before Todoist hears about them).
sealed class AgendaUi
{
    public int Page;
    public string CardId;
    public readonly Dictionary<string, DateTime> Completing = new(); // task id -> when it's sent to Todoist (UTC)
    public readonly HashSet<string> Done = new();                     // sent; hidden until the next fetch drops them
    public readonly HashSet<string> Dismissed = new();                // Next up ids you closed ("Dismiss")
}

// The open card for an event: everything about it plus the links its buttons open.
sealed record AgendaCard(string Id, string Title, string When, string Where, string Notes, string MapsUrl, string JoinUrl);

// What the views read: the latest items from both sources and a short status for each.
sealed class AgendaData
{
    public List<AgendaItem> Events = new(), Tasks = new();
    public DateTime CalendarUtc, TasksUtc;     // when each was last fetched successfully
    public string CalendarStatus, TasksStatus; // null when fine
    public bool TodoistConnected;
}

static class Agenda
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    // ---- parsing (tolerant: Spark promises snake_case keys and ISO 8601 dates; names are checked live)

    public static List<AgendaItem> ParseSpark(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement err))
            throw new InvalidOperationException(Str(err, "message") ?? "Spark error");
        var list = new List<AgendaItem>();
        foreach (JsonElement e in ArrayIn(root, "events"))
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string title = Str(e, "title", "summary", "name", "subject");
            if (!When(e, out DateTime start, out bool startDateOnly, "start", "start_date", "starts_at", "start_time", "begin", "from")) continue;
            When(e, out DateTime end, out _, "end", "end_date", "ends_at", "end_time", "finish", "to");
            bool allDay = Bool(e, "all_day", "is_all_day", "allday", "full_day") ?? startDateOnly;
            if (end <= start) end = allDay ? start.Date.AddDays(1) : start;
            list.Add(new AgendaItem
            {
                Source = AgendaSource.Calendar,
                Id = Str(e, "id", "event_id", "uid") ?? "",
                Title = Clean(title ?? "(no title)"),
                Where = Clean(Str(e, "location", "place") ?? ""),
                Notes = (Str(e, "notes", "description") ?? "").Trim(),
                Group = Str(e, "calendar", "calendar_name", "calendar_title") ?? "",
                Start = allDay ? start.Date : start,
                End = end,
                AllDay = allDay,
                HasTime = !allDay,
                Color = HexColor(Str(e, "color", "calendar_color")),
            });
        }
        return list;
    }

    public static List<AgendaItem> ParseTodoist(string json, IReadOnlyDictionary<string, string> projects, out string nextCursor)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        nextCursor = root.ValueKind == JsonValueKind.Object ? Str(root, "next_cursor") : null;
        var list = new List<AgendaItem>();
        foreach (JsonElement t in ArrayIn(root, "results", "items", "tasks"))
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            if (Bool(t, "checked", "is_completed", "completed") == true || Bool(t, "is_deleted") == true) continue;
            if (!t.TryGetProperty("due", out JsonElement due) || due.ValueKind != JsonValueKind.Object) continue;
            // API v1 puts a time inside "date"; the older REST API used a separate "datetime"
            string when = Str(due, "datetime") ?? Str(due, "date");
            if (!ParseWhen(when, out DateTime start, out bool dateOnly)) continue;
            DateTime end = start;
            if (!dateOnly && t.TryGetProperty("duration", out JsonElement dur) && dur.ValueKind == JsonValueKind.Object &&
                dur.TryGetProperty("amount", out JsonElement amt) && amt.TryGetInt32(out int amount))
                end = Str(dur, "unit") == "day" ? start.AddDays(amount) : start.AddMinutes(amount);
            string projectId = Str(t, "project_id") ?? "";
            list.Add(new AgendaItem
            {
                Source = AgendaSource.Todoist,
                Id = Str(t, "id") ?? "",
                Title = Clean(Str(t, "content") ?? ""),
                Notes = (Str(t, "description") ?? "").Trim(),
                Group = projects != null && projects.TryGetValue(projectId, out string name) ? name : "",
                Start = start,
                End = end,
                HasTime = !dateOnly,
                Priority = t.TryGetProperty("priority", out JsonElement p) && p.TryGetInt32(out int pr) ? Math.Clamp(pr, 1, 4) : 1,
            });
        }
        return list;
    }

    public static Dictionary<string, string> ParseProjects(string json, out string nextCursor)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        nextCursor = doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "next_cursor") : null;
        var map = new Dictionary<string, string>();
        foreach (JsonElement p in ArrayIn(doc.RootElement, "results", "projects"))
        {
            string id = Str(p, "id"), name = Str(p, "name");
            if (id != null && name != null) map[id] = name;
        }
        return map;
    }

    // "2026-11-17" (date only), "2026-11-17T15:00:00" (floating local time), or with Z / an offset.
    public static bool ParseWhen(string s, out DateTime local, out bool dateOnly)
    {
        local = default;
        dateOnly = false;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.Length == 10 && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
        {
            local = DateTime.SpecifyKind(d, DateTimeKind.Local);
            dateOnly = true;
            return true;
        }
        bool zoned = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(s, @"[+-]\d\d:?\d\d$");
        if (zoned && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dto))
        {
            local = dto.LocalDateTime;
            return true;
        }
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime f))
        {
            local = DateTime.SpecifyKind(f, DateTimeKind.Local);
            return true;
        }
        return false;
    }

    // Todoist titles can carry markdown: [text](link), **bold**, __bold__, `code`.
    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // ---- combining

    // He keeps appointments in both Apple Calendar and Todoist: a task that matches an event on the
    // same day (same title, or one title inside the other at about the same time) is shown once, as the event.
    public static List<AgendaItem> Merge(IEnumerable<AgendaItem> events, IEnumerable<AgendaItem> tasks)
    {
        var evs = events.ToList();
        var result = new List<AgendaItem>(evs);
        foreach (AgendaItem t in tasks)
        {
            string tn = Norm(t.Title);
            AgendaItem twin = evs.FirstOrDefault(e =>
            {
                if (e.Start.Date != t.Start.Date) return false;
                string en = Norm(e.Title);
                if (en.Length == 0 || tn.Length == 0) return false;
                if (en == tn) return true;
                bool inside = (en.Length >= 5 && tn.Contains(en)) || (tn.Length >= 5 && en.Contains(tn));
                return inside && (!t.HasTime || !e.HasTime || Math.Abs((e.Start - t.Start).TotalMinutes) <= 30);
            });
            if (twin != null) twin.AlsoInTodoist = true;
            else result.Add(t);
        }
        return result;
    }

    static string Norm(string s)
    {
        var b = new StringBuilder();
        foreach (char c in (s ?? "").ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) b.Append(c);
            else if (b.Length > 0 && b[^1] != ' ') b.Append(' ');
        return b.ToString().Trim();
    }

    // Today's list, in the order that matters at a glance: what's on now and coming up (by time),
    // then late or overdue tasks, then all-day events, then today's other tasks (highest priority first).
    public static List<AgendaItem> Today(List<AgendaItem> all, DateTime now)
    {
        DateTime today = now.Date, tomorrow = today.AddDays(1);
        bool Late(AgendaItem i) => i.Source == AgendaSource.Todoist && (i.Start.Date < today || (i.HasTime && i.Start < now));
        var timed = all.Where(i => i.HasTime && !Late(i) && i.Start < tomorrow && (i.Source == AgendaSource.Todoist ? i.Start >= now : i.End > now))
                       .OrderBy(i => i.Start).ThenBy(i => i.Source);
        var late = all.Where(Late).OrderBy(i => i.Start).ThenByDescending(i => i.Priority);
        var allDay = all.Where(i => i.AllDay && i.Start < tomorrow && i.End > today).OrderBy(i => i.Title);
        var tasks = all.Where(i => i.Source == AgendaSource.Todoist && !i.HasTime && i.Start.Date == today)
                       .OrderByDescending(i => i.Priority).ThenBy(i => i.Title);
        return timed.Concat(late).Concat(allDay).Concat(tasks).ToList();
    }

    // The soonest timed item starting within the next few minutes, or one that started less than
    // holdMinutes ago (events win ties).
    public static AgendaItem NextUp(List<AgendaItem> all, DateTime now, int minutes, int holdMinutes = 0)
    {
        return all.Where(i => i.HasTime && (i.Start - now).TotalMinutes <= minutes && now < i.Start.AddMinutes(holdMinutes))
                  .OrderBy(i => i.Start).ThenBy(i => i.Source).FirstOrDefault();
    }

    public static string Time(DateTime t) => t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", En);

    // Google Maps directions to a place (opens in the default browser).
    public static string Directions(string place) =>
        string.IsNullOrWhiteSpace(place) ? null : "https://www.google.com/maps/dir/?api=1&destination=" + Uri.EscapeDataString(place.Trim());

    // A meeting link in an event's location or notes: Zoom, Meet, Teams, Webex, Whereby, FaceTime first,
    // otherwise the first https link.
    public static string MeetingLink(params string[] texts)
    {
        string[] known = { "zoom.us/", "meet.google.com/", "teams.microsoft.com/", "teams.live.com/", "webex.com/", "whereby.com/", "facetime.apple.com/" };
        string any = null;
        foreach (string t in texts)
        {
            if (string.IsNullOrEmpty(t)) continue;
            foreach (Match m in Regex.Matches(t, @"https?://[^\s<>""')\]]+"))
            {
                string u = m.Value.TrimEnd('.', ',', ';');
                if (known.Any(k => u.Contains(k, StringComparison.OrdinalIgnoreCase))) return u;
                any ??= u;
            }
        }
        return any;
    }

    // A place you'd get directions to: a location that isn't just a meeting link.
    public static string Place(AgendaItem i) =>
        string.IsNullOrWhiteSpace(i.Where) || Regex.IsMatch(i.Where, @"^\s*https?://") ? null : i.Where;
    public static string Range(AgendaItem i) =>
        i.End > i.Start ? Time(i.Start) + " – " + Time(i.End) : Time(i.Start);

    // ---- JSON helpers

    static IEnumerable<JsonElement> ArrayIn(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray();
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<JsonElement>();
        foreach (string n in names)
            if (root.TryGetProperty(n, out JsonElement a) && a.ValueKind == JsonValueKind.Array) return a.EnumerateArray();
        foreach (JsonProperty p in root.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array) return p.Value.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    static string Str(JsonElement e, params string[] names)
    {
        foreach (string n in names)
            if (e.TryGetProperty(n, out JsonElement v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
                if (v.ValueKind == JsonValueKind.Object) return Str(v, "name", "title", "date_time", "datetime", "date", "value");
            }
        return null;
    }

    static bool? Bool(JsonElement e, params string[] names)
    {
        foreach (string n in names)
            if (e.TryGetProperty(n, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return v.GetBoolean();
        return null;
    }

    static bool When(JsonElement e, out DateTime local, out bool dateOnly, params string[] names)
    {
        local = default;
        dateOnly = false;
        foreach (string n in names)
        {
            if (!e.TryGetProperty(n, out JsonElement v)) continue;
            string s = v.ValueKind == JsonValueKind.String ? v.GetString()
                     : v.ValueKind == JsonValueKind.Object ? Str(v, "date_time", "datetime", "date") : null;
            if (ParseWhen(s, out local, out dateOnly)) return true;
        }
        return false;
    }

    static Color? HexColor(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('#');
        if (s.Length >= 6 && int.TryParse(s[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        return null;
    }
}

