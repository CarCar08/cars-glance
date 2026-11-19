// Agenda + Next up: the Windows half (Spark CLI, Todoist over HTTPS, Windows Credential Manager).
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance;

// ------------------------------------------------------------------ Apple Calendar through Spark Desktop

sealed class CalendarService
{
    static string CacheFile => Path.Combine(Paths.DataDir, "calendar-cache.json");
    volatile List<AgendaItem> items = new();
    volatile string status;
    DateTime fetchedUtc, nextUtc;
    DateTime lastDay;
    volatile bool busy;
    bool loggedError;

    public List<AgendaItem> Items => items;
    public string Status => status;
    public DateTime FetchedUtc => fetchedUtc;

    public CalendarService() => LoadCache();

    // Every 5 minutes, at midnight, and on request. Spark only answers while Spark Desktop is open;
    // otherwise the last list (saved to disk) keeps the agenda and Next up working.
    public void Tick(AgendaSetting s, bool force)
    {
        if (!s.Enabled || !s.Calendar) { items = new List<AgendaItem>(); status = null; return; }
        DateTime now = DateTime.UtcNow;
        bool newDay = DateTime.Now.Date != lastDay;
        if (busy || (!force && !newDay && now < nextUtc)) return;
        busy = true;
        lastDay = DateTime.Now.Date;
        nextUtc = now.AddMinutes(5);
        string exe = FindSpark(s.SparkPath);
        Task.Run(() =>
        {
            try
            {
                if (exe == null) { status = "Spark isn't installed"; return; }
                DateTime today = DateTime.Now.Date;
                string json = Run(exe, "events --json --start " + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                                       " --end " + today.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                List<AgendaItem> fresh = Agenda.ParseSpark(json);
                items = fresh;
                fetchedUtc = DateTime.UtcNow;
                status = null;
                SaveCache(json);
            }
            catch (Exception ex)
            {
                bool closed = ex.Message.Contains("Spark Desktop", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase);
                status = closed ? "Spark is closed" : "Calendar unavailable";
                if (!closed && !loggedError) { loggedError = true; Log.Write("calendar: " + ex.Message); } // Spark being closed is normal
                nextUtc = DateTime.UtcNow.AddMinutes(1);
            }
            finally { busy = false; }
        });
    }

    static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,          // no console flash, no focus change
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        using Process p = Process.Start(psi);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        Task<string> output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } throw new TimeoutException("Spark took too long"); }
        return output.Result;
    }

    public static string FindSpark(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SparkDesktop");
        string known = Path.Combine(root, @"resources\app.asar.unpacked\node_modules\@readdle\sparkcore-win\bin\Release\SparkCore.bundle\spark.exe");
        if (File.Exists(known)) return known;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { string f = Path.Combine(dir.Trim(), "spark.exe"); if (File.Exists(f)) return f; } catch { }
        }
        return null;
    }

    void SaveCache(string json)
    {
        try { File.WriteAllText(CacheFile, json); } catch { }
    }

    void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            items = Agenda.ParseSpark(File.ReadAllText(CacheFile));
            fetchedUtc = File.GetLastWriteTimeUtc(CacheFile);
        }
        catch { }
    }
}

// ------------------------------------------------------------------ Todoist (his own API token)

sealed class TodoistService
{
    const string Api = "https://api.todoist.com/api/v1/";
    static readonly HttpClient Http = CreateClient();
    volatile List<AgendaItem> items = new();
    volatile string status;
    volatile bool busy, connected;
    DateTime fetchedUtc, nextUtc, projectsUtc;
    Dictionary<string, string> projects = new();
    bool loggedError, useFilterEndpoint = true;

    public List<AgendaItem> Items => items;
    public string Status => status;
    public bool Connected => connected;
    public DateTime FetchedUtc => fetchedUtc;

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(WeatherService.UserAgent);
        return c;
    }

    public void Tick(AgendaSetting s, bool force)
    {
        if (!s.Enabled || !s.Todoist) { items = new List<AgendaItem>(); status = null; connected = false; return; }
        DateTime now = DateTime.UtcNow;
        if (busy || (!force && now < nextUtc)) return;
        string token = Credentials.Read();
        connected = token != null;
        if (token == null) { items = new List<AgendaItem>(); status = null; nextUtc = now.AddMinutes(1); return; }
        busy = true;
        nextUtc = now.AddMinutes(5);
        Task.Run(async () =>
        {
            try
            {
                if ((DateTime.UtcNow - projectsUtc).TotalHours >= 1)
                {
                    try { projects = await FetchProjects(token); projectsUtc = DateTime.UtcNow; } catch { }
                }
                items = await FetchTasks(token, projects);
                fetchedUtc = DateTime.UtcNow;
                status = null;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
            {
                status = "Todoist token rejected: reconnect it";
                nextUtc = DateTime.UtcNow.AddMinutes(30);
            }
            catch (Exception ex)
            {
                status = "Todoist unavailable";
                if (!loggedError) { loggedError = true; Log.Write("todoist: " + ex.Message); }
                nextUtc = DateTime.UtcNow.AddMinutes(2);
            }
            finally { busy = false; }
        });
    }

    // Tasks due today or overdue. Falls back to all tasks (filtered here) if the filter endpoint is refused.
    public async Task<List<AgendaItem>> FetchTasks(string token, IReadOnlyDictionary<string, string> projectNames)
    {
        var all = new List<AgendaItem>();
        string cursor = null;
        for (int page = 0; page < 20; page++)
        {
            string url = useFilterEndpoint
                ? Api + "tasks/filter?query=" + Uri.EscapeDataString("today | overdue") + "&limit=200"
                : Api + "tasks?limit=200";
            if (cursor != null) url += "&cursor=" + Uri.EscapeDataString(cursor);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage res = await Http.SendAsync(req);
            if (useFilterEndpoint && (res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.BadRequest) && page == 0)
            {
                useFilterEndpoint = false;
                page = -1;
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new HttpRequestException("Todoist answered " + (int)res.StatusCode, null, res.StatusCode);
            all.AddRange(Agenda.ParseTodoist(await res.Content.ReadAsStringAsync(), projectNames, out cursor));
            if (string.IsNullOrEmpty(cursor)) break;
        }
        DateTime today = DateTime.Now.Date;
        return useFilterEndpoint ? all : all.Where(t => t.Start.Date <= today).ToList();
    }

    /// <summary>Asks for a fresh list on the next tick (after a change made here).</summary>
    public void Soon() => nextUtc = DateTime.UtcNow.AddSeconds(2);

    // Completes a task (a repeating task moves to its next date, as in Todoist). Returns an error line or null.
    public async Task<string> CloseTask(string id)
    {
        if (string.IsNullOrEmpty(id)) return "That task has no id";
        string token = Credentials.Read();
        if (token == null) return "Todoist isn't connected";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Api + "tasks/" + Uri.EscapeDataString(id) + "/close");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage res = await Http.SendAsync(req);
            if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotFound) { Soon(); return null; } // gone already counts as done
            return "Todoist answered " + (int)res.StatusCode;
        }
        catch (Exception ex) { Log.Write("todoist close: " + ex.Message); return "Todoist unavailable"; }
    }

    // Quick add: Todoist reads dates, #projects, @labels and p1-p4 from the text itself.
    public async Task<string> QuickAdd(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Type a task first";
        string token = Credentials.Read();
        if (token == null) return "Todoist isn't connected: connect it in Settings";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Api + "tasks/quick")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage res = await Http.SendAsync(req);
            if (res.IsSuccessStatusCode) { Soon(); return null; }
            if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden) return "Todoist refused the token: reconnect it in Settings";
            return "Todoist answered " + (int)res.StatusCode + ": try again";
        }
        catch (Exception ex) { Log.Write("todoist add: " + ex.Message); return "Couldn't reach Todoist: check the connection"; }
    }

    static async Task<Dictionary<string, string>> FetchProjects(string token)
    {
        var map = new Dictionary<string, string>();
        string cursor = null;
        for (int page = 0; page < 10; page++)
        {
            string url = Api + "projects?limit=200" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage res = await Http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            foreach (var kv in Agenda.ParseProjects(await res.Content.ReadAsStringAsync(), out cursor)) map[kv.Key] = kv.Value;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return map;
    }
}

// ------------------------------------------------------------------ Windows Credential Manager (generic credential)

static class Credentials
{
    public const string Target = "CarsGlance:Todoist";
    public const string LegacyTarget = "DesktopWidget:Todoist"; // before the rename (migrated once)
    const int CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);

    public static string Read(string target = Target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize <= 0) return null;
            return Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
        }
        finally { CredFree(p); }
    }

    public static bool Write(string secret, string target = Target)
    {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        IntPtr mem = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, mem, blob.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                Comment = "C.A.R.S. Glance: Todoist API token",
                CredentialBlobSize = blob.Length,
                CredentialBlob = mem,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "todoist",
            };
            return CredWrite(ref c, 0);
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, mem, blob.Length); // wipe our copy
            Marshal.FreeHGlobal(mem);
        }
    }

    public static bool Delete(string target = Target) => CredDelete(target, CRED_TYPE_GENERIC, 0);
}
