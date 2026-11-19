// The running widget: one glass panel per display on the desktop layer (above the wallpaper,
// below every app window, never focused), all showing the same view. Clicks and the scroll
// wheel switch views; Windows blurs the live wallpaper behind the glass.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Glance;

enum InputKind { Next, Previous, Dot, Hover, Media, Update, Act, Menu, Volume }

// Act: a click on a hit region (Hit says which, Index which row/button, Value a seek position, Item the row's id).
readonly record struct WidgetInput(InputKind Kind, int Index = -1, HitKind Hit = HitKind.None, float Value = 0, string Item = null);

sealed class WidgetContext : ApplicationContext
{
    readonly EventWaitHandle quit;
    readonly System.Windows.Forms.Timer timer = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly List<Overlay> overlays = new();
    readonly uint ownPid = (uint)Environment.ProcessId;
    readonly WeatherService weather = new();
    readonly MediaService media = new();
    readonly CalendarService calendar = new();
    readonly TodoistService todoist = new();
    readonly EventWaitHandle refresh; // set by the Connect Todoist window
    readonly UpdateService updates = new();
    readonly FileSystemWatcher watcher;
    Settings settings;
    volatile bool settingsDirty, woke, clockChanged;
    string monitorSignature;
    IntPtr desktopHost;
    double lastHousekeeping = double.MinValue;
    bool loggedError, ticking;

    List<ViewModel> views = new();
    int current;
    string manualId;
    DateTime manualUntilUtc;
    bool held;                                  // "Stay on this view for an hour" (from the menu)
    DateTime hiddenUntilUtc;                    // "Hide for an hour"
    readonly EventWaitHandle show;              // set when Glance is started again while running: show it at once
    readonly EventWaitHandle checkNow = new(false, EventResetMode.AutoReset, @"Local\CarsGlance.CheckUpdates"); // Settings > Updates > Check now
    readonly AgendaUi agendaUi = new();
    readonly System.Collections.Concurrent.ConcurrentQueue<Action> onUi = new(); // results from background work
    string toast;                               // a short message in the top-left pill (volume, an error)
    DateTime toastUntilUtc;
    bool hintSeen = File.Exists(Paths.HintFlag);
    ContextMenuStrip menu;
    QuickAddForm quickAdd;
    const string FirstRunHint = "SCROLL OR USE  ‹ ›  TO SWITCH   ·   RIGHT-CLICK FOR MORE";
    // which way the next view switch slides: set by a click/scroll, otherwise from the view order
    int pendingDir, slideDir = 1, lastIndex;
    string lastViewId;

    public WidgetContext(EventWaitHandle quit, EventWaitHandle refresh, EventWaitHandle show = null)
    {
        this.quit = quit;
        this.refresh = refresh;
        this.show = show;
        bool firstRun = !File.Exists(Paths.SettingsFile);
        settings = Settings.Load() ?? Settings.Parse("{}");
        if (firstRun && UpdateService.IsInstalledCopy)
        {
            // first start after installing: the Settings window opens on its Welcome page
            var once = new System.Windows.Forms.Timer { Interval = 1500 };
            once.Tick += (s, e) => { once.Stop(); once.Dispose(); Program.OpenSettings("welcome"); };
            once.Start();
        }
        try
        {
            watcher = new FileSystemWatcher(Paths.DataDir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            watcher.Changed += (s, e) => settingsDirty = true;
            watcher.Created += (s, e) => settingsDirty = true;
            watcher.Renamed += (s, e) => settingsDirty = true;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Write("watcher: " + ex.Message); }

        // Self-heal: after sleep or a clock / time-zone change, refresh everything at once instead of showing
        // stale weather, a wrong agenda or a frozen panel until the next regular refresh.
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged;

        timer.Interval = 15;
        timer.Tick += (s, e) => Tick();
        timer.Start();
        Tick();
    }

    void Tick()
    {
        if (ticking) return;
        ticking = true;
        try
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (now - lastHousekeeping >= 250)
            {
                lastHousekeeping = now;
                if (quit.WaitOne(0)) { ExitThread(); return; }
                if (settingsDirty)
                {
                    settingsDirty = false;
                    Settings fresh = Settings.Load();
                    if (fresh != null) settings = fresh;
                }
                SyncMonitors();
                IntPtr host = DesktopHost();
                bool haveFg = ForegroundRect(host, out Native.RECT fg);
                bool gameMode = haveFg && overlays.Any(o => Contains(fg, o.Monitor));
                bool calm = Calm();
                foreach (Overlay o in overlays)
                    o.Housekeep(host, ownPid, gameMode, haveFg && Contains(fg, o.Monitor), calm);
                while (onUi.TryDequeue(out Action a))
                {
                    try { a(); } catch (Exception ex) { Log.Write("ui: " + ex.Message); }
                }
                if (show != null && show.WaitOne(0)) { hiddenUntilUtc = DateTime.MinValue; foreach (Overlay o in overlays) o.MarkStale(); }
                SendDueCompletions();
                bool force = refresh != null && refresh.WaitOne(0);
                if (woke || clockChanged)
                {
                    if (clockChanged)
                    {
                        TimeZoneInfo.ClearCachedData();
                        System.Globalization.CultureInfo.CurrentCulture.ClearCachedData();
                    }
                    Log.Write(woke ? "resumed from sleep: refreshing" : "clock or time zone changed: refreshing");
                    woke = clockChanged = false;
                    force = true;
                    weather.Force();
                    foreach (Overlay o in overlays) o.MarkStale();
                }
                weather.Tick(settings.Weather);
                media.Tick(settings.NowPlaying, gameMode);
                calendar.Tick(settings.Agenda, force);
                todoist.Tick(settings.Agenda, force);
                bool checkRequested = checkNow.WaitOne(0);
                if (settings.CheckForUpdates || checkRequested) updates.Tick(gameMode, checkRequested);
            }
            if (desktopHost == IntPtr.Zero) { SetInterval(250); return; } // Explorer not ready
            if (DateTime.UtcNow < hiddenUntilUtc)
            {
                foreach (Overlay o in overlays) o.Hide();
                SetInterval(250);
                return;
            }

            DateTime nowLocal = DateTime.Now;
            var agenda = new AgendaData
            {
                Events = calendar.Items,
                Tasks = todoist.Items,
                CalendarUtc = calendar.FetchedUtc,
                TasksUtc = todoist.FetchedUtc,
                CalendarStatus = calendar.Status,
                TasksStatus = todoist.Status,
                TodoistConnected = todoist.Connected,
            };
            ForgetFinishedTasks();
            views = Views.Build(settings, nowLocal, weather.Latest, media.Current, agenda, agendaUi);
            string pickId = null;
            if (manualId != null && DateTime.UtcNow < manualUntilUtc && views.Any(v => v.Id == manualId)) pickId = manualId;
            else { manualId = null; held = false; }
            current = pickId != null ? views.FindIndex(v => v.Id == pickId) : Smart.Pick(views);
            ViewModel vm = views[current];
            if (vm.Id != lastViewId)
            {
                if (lastViewId != null) slideDir = pendingDir != 0 ? pendingDir : current >= lastIndex ? 1 : -1;
                pendingDir = 0;
                lastViewId = vm.Id;
            }
            lastIndex = current;

            UpdateService.ReadyUpdate ready = updates.Ready;
            string updateText = ready == null ? null : "UPDATE READY  ·  " + ready.Version;
            if (toast != null && DateTime.UtcNow >= toastUntilUtc) toast = null;
            string hint = toast ?? (hintSeen ? null : FirstRunHint);
            bool busy = false;
            foreach (Overlay o in overlays)
            {
                o.UpdateText = updateText;
                o.HintText = hint;
                o.Frame(vm, current, views.Count, slideDir, now, desktopHost, ownPid);
                busy |= o.Animating;
            }
            int untilNextSecond = 1000 - DateTime.UtcNow.Millisecond + 2;
            SetInterval(busy ? 15 : Math.Max(15, Math.Min(250, untilNextSecond)));
        }
        catch (Exception ex)
        {
            if (!loggedError) { Log.Write(ex.ToString()); loggedError = true; }
        }
        finally { ticking = false; }
    }

    void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) woke = true;
    }

    void OnTimeChanged(object sender, EventArgs e) => clockChanged = true;

    void OnInput(WidgetInput e)
    {
        if (views.Count == 0) return;
        switch (e.Kind)
        {
            case InputKind.Hover: break;
            case InputKind.Next: SwitchBy(1); break;
            case InputKind.Previous: SwitchBy(-1); break;
            case InputKind.Dot: SwitchTo(e.Index); break;
            case InputKind.Update: if (updates.Apply()) { ExitThread(); return; } break;
            case InputKind.Media: media.Command((MediaButton)e.Index); Hold(); break;
            case InputKind.Volume: ChangeVolume(e.Value); break;
            case InputKind.Menu: SeenHint(); ShowMenu(e.Item); return; // the menu runs its own loop: no Tick here
            case InputKind.Act: Act(e); break;
        }
        Tick();
    }

    // What a click on each part of a view does. Every action keeps the view up (as a manual choice), so it
    // doesn't switch away while you use it.
    void Act(WidgetInput e)
    {
        ViewModel vm = views[current];
        AgendaRow row = e.Index >= 0 && vm.Rows != null && e.Index < vm.Rows.Count ? vm.Rows[e.Index] : null;
        switch (e.Hit)
        {
            case HitKind.Dot: SwitchTo(e.Index); return;
            case HitKind.EdgePrev: SwitchBy(-1); return;
            case HitKind.EdgeNext: SwitchBy(1); return;
            case HitKind.Update: if (updates.Apply()) ExitThread(); return;
            case HitKind.Hint: if (toast != null) toast = null; else SeenHint(); return;

            case HitKind.Media: media.Command((MediaButton)e.Index); break;
            case HitKind.Seek: media.Seek(e.Value); break;
            case HitKind.Cover: media.OpenPlayer(); break;

            case HitKind.Weather: OpenForecast(); break;
            case HitKind.Date: ShowKind(ViewKind.Agenda); return;

            case HitKind.TaskDone: if (e.Item != null) agendaUi.Completing[e.Item] = DateTime.UtcNow.AddSeconds(5); break;
            case HitKind.Undo: if (e.Item != null) agendaUi.Completing.Remove(e.Item); break;
            case HitKind.TaskOpen: if (e.Item != null) Open.Url("https://app.todoist.com/app/task/" + Uri.EscapeDataString(e.Item)); break;
            case HitKind.EventOpen: if (row != null) agendaUi.CardId = row.ItemId; break;
            case HitKind.More: agendaUi.Page++; break;
            case HitKind.AddTask: ShowQuickAdd(); break;
            case HitKind.CardClose: agendaUi.CardId = null; break;
            case HitKind.CardDirections: Open.Url(vm.Card?.MapsUrl); break;
            case HitKind.CardJoin: Open.Url(vm.Card?.JoinUrl); break;

            case HitKind.NextDirections: Open.Url(vm.MapsUrl); break;
            case HitKind.NextJoin: Open.Url(vm.JoinUrl); break;

            case HitKind.Countdown: OpenSettings("countdowns"); break;

            case HitKind.HomeLine:
                string target = vm.Lines != null && e.Index >= 0 && e.Index < vm.Lines.Count ? vm.Lines[e.Index].Target : null;
                if (target == "weather") { OpenForecast(); break; }
                if (target == "agenda") { ShowKind(ViewKind.Agenda); return; }
                if (target == "nowplaying") { ShowKind(ViewKind.NowPlaying); return; }
                if (target != null && target.StartsWith("countdown:"))
                {
                    int ci = views.FindIndex(v => v.Id == target);
                    if (ci >= 0) SwitchTo(ci); else OpenSettings("countdowns");
                    return;
                }
                break;
        }
        Hold();
    }

    void SwitchBy(int dir)
    {
        int n = views.Count;
        SeenHint();
        if (n < 2) return;
        int next = ((current + dir) % n + n) % n;
        pendingDir = dir;
        manualId = views[next].Id;
        manualUntilUtc = DateTime.UtcNow.AddMinutes(settings.ManualViewMinutes);
        held = false;
    }

    void SwitchTo(int index)
    {
        int n = views.Count;
        SeenHint();
        if (n < 2) return;
        int next = Math.Clamp(index, 0, n - 1);
        pendingDir = next >= current ? 1 : -1;
        manualId = views[next].Id;
        manualUntilUtc = DateTime.UtcNow.AddMinutes(settings.ManualViewMinutes);
        held = false;
    }

    void ShowKind(ViewKind kind)
    {
        int i = views.FindIndex(v => v.Kind == kind);
        if (i >= 0) SwitchTo(i);
    }

    // Keeps the current view on screen for the usual few minutes (an hour while "Stay on this view" is on).
    void Hold()
    {
        manualId = views[current].Id;
        DateTime until = DateTime.UtcNow.AddMinutes(settings.ManualViewMinutes);
        if (!held || until > manualUntilUtc) manualUntilUtc = until;
    }

    void SeenHint()
    {
        if (hintSeen) return;
        hintSeen = true;
        try { File.WriteAllText(Paths.HintFlag, DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture)); } catch { }
    }

    void Toast(string text, double seconds = 2.5)
    {
        toast = text;
        toastUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
    }

    // Scrolling over the cover: the player's own volume (as in the Volume Mixer), 4% a notch.
    void ChangeVolume(float notches)
    {
        string app = media.Current?.AppId;
        float? v = AppVolume.Change(app, notches * 0.04f);
        Toast(v is float nv ? "VOLUME  ·  " + (int)Math.Round(nv * 100) + "%" : "THIS PLAYER'S VOLUME CAN'T BE CHANGED HERE", 1.6);
        Hold();
    }

    // Ticked tasks wait 5 seconds (Undo) and then go to Todoist. A failure brings the task back with a note.
    readonly Dictionary<string, DateTime> doneAtUtc = new();

    void SendDueCompletions()
    {
        if (agendaUi.Completing.Count == 0) return;
        DateTime now = DateTime.UtcNow;
        foreach (string id in agendaUi.Completing.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
        {
            agendaUi.Completing.Remove(id);
            agendaUi.Done.Add(id);
            doneAtUtc[id] = DateTime.MaxValue; // until Todoist confirms
            Task.Run(async () =>
            {
                string error = await todoist.CloseTask(id);
                onUi.Enqueue(() =>
                {
                    if (error == null) { doneAtUtc[id] = DateTime.UtcNow; return; }
                    agendaUi.Done.Remove(id);
                    doneAtUtc.Remove(id);
                    Toast("COULDN'T COMPLETE IT: " + error.ToUpperInvariant(), 5);
                });
            });
        }
    }

    // Once a fresh list from Todoist arrives after the close, the task is gone from it (or, if it repeats, has
    // moved to its next date), so it no longer needs hiding.
    void ForgetFinishedTasks()
    {
        if (doneAtUtc.Count == 0) return;
        foreach (string id in doneAtUtc.Where(kv => kv.Value != DateTime.MaxValue && todoist.FetchedUtc > kv.Value.AddSeconds(3)).Select(kv => kv.Key).ToList())
        {
            doneAtUtc.Remove(id);
            agendaUi.Done.Remove(id);
        }
    }

    void ShowQuickAdd()
    {
        if (quickAdd != null && !quickAdd.IsDisposed) { quickAdd.Activate(); return; }
        quickAdd = new QuickAddForm(async text =>
        {
            string error = await todoist.QuickAdd(text);
            if (error == null) onUi.Enqueue(() => Toast("ADDED TO TODOIST"));
            return error;
        });
        quickAdd.FormClosed += (s, e) => quickAdd = null;
        quickAdd.Show();
    }

    // The forecast for the city in Settings, in the browser (an in-panel forecast arrives with Weather+).
    void OpenForecast()
    {
        WeatherSetting w = settings.Weather;
        if (!w.HasLocation) { OpenSettings("weather"); return; }
        string la = w.Latitude.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        string lo = w.Longitude.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        bool us = w.Latitude is > 18 and < 72 && w.Longitude is > -180 and < -64;
        Open.Url(us ? $"https://forecast.weather.gov/MapClick.php?lat={la}&lon={lo}" : $"https://weather.com/weather/today/l/{la},{lo}");
    }

    static void OpenSettings(string page = null) => Program.OpenSettings(page);

    // ---- the right-click menu: what this view can do, then the panel itself

    void ShowMenu(string itemId)
    {
        menu?.Dispose();
        menu = new ContextMenuStrip { Renderer = new GlanceMenuRenderer(), Font = new Font("Segoe UI", 10.5f), ShowCheckMargin = true, ShowImageMargin = false };
        ViewModel vm = views[current];
        ToolStripItemCollection items = menu.Items;
        void Item(string text, Action a, bool enabled = true, bool check = false)
        {
            var mi = new ToolStripMenuItem(text) { Enabled = enabled, Checked = check, Padding = new Padding(4, 3, 4, 3) };
            mi.Click += (s, e) => { try { a(); } catch (Exception ex) { Log.Write("menu: " + ex.Message); } Tick(); };
            items.Add(mi);
        }

        switch (vm.Kind)
        {
            case ViewKind.NowPlaying when vm.Media != null:
                Item("Open " + media.PlayerName, media.OpenPlayer);
                if (vm.Media.CanShuffle) Item("Shuffle", () => { media.ToggleShuffle(); Hold(); }, check: vm.Media.Shuffle);
                if (vm.Media.CanRepeat)
                    Item("Repeat: " + (vm.Media.Repeat switch { 1 => "this song", 2 => "the list", _ => "off" }), () => { media.CycleRepeat(); Hold(); });
                break;
            case ViewKind.Agenda:
                AgendaItem task = itemId == null ? null : todoist.Items.FirstOrDefault(t => t.Id == itemId);
                AgendaItem ev = itemId == null || task != null ? null : calendar.Items.FirstOrDefault(c => c.Id == itemId);
                if (task != null)
                {
                    Item("Complete “" + Short(task.Title) + "”", () => { agendaUi.Completing[task.Id] = DateTime.UtcNow.AddSeconds(5); Hold(); });
                    Item("Open in Todoist", () => Open.Url("https://app.todoist.com/app/task/" + Uri.EscapeDataString(task.Id)));
                    items.Add(new ToolStripSeparator());
                }
                else if (ev != null)
                {
                    Item("Details", () => { agendaUi.CardId = ev.Id; Hold(); });
                    string maps = Agenda.Directions(Agenda.Place(ev)), join = Agenda.MeetingLink(ev.Where, ev.Notes);
                    if (join != null) Item("Join the meeting", () => Open.Url(join));
                    if (maps != null) Item("Directions", () => Open.Url(maps));
                    items.Add(new ToolStripSeparator());
                }
                if (vm.CanAdd) Item("Add a task…", ShowQuickAdd);
                if (settings.Agenda.Todoist) Item("Open Todoist", () => Open.Url("https://app.todoist.com/app/today"));
                Item("Refresh the agenda", () => { calendar.Tick(settings.Agenda, true); todoist.Tick(settings.Agenda, true); });
                break;
            case ViewKind.NextUp:
                if (vm.JoinUrl != null) Item("Join the meeting", () => Open.Url(vm.JoinUrl));
                if (vm.MapsUrl != null) Item("Directions", () => Open.Url(vm.MapsUrl));
                Item("Dismiss", () => { agendaUi.Dismissed.Add(vm.Id); if (manualId == vm.Id) manualId = null; });
                break;
            case ViewKind.Countdown:
                CountdownSetting c = settings.Countdowns.FirstOrDefault(x => "countdown:" + x.Name == vm.Id);
                if (c != null) Item(c.Pinned ? "Unpin this countdown" : "Pin this countdown", () => SetPinned(c.Name, !c.Pinned));
                Item("Edit countdowns…", () => OpenSettings("countdowns"));
                break;
            case ViewKind.Clock:
            case ViewKind.Home:
                if (settings.Weather.Enabled) Item("Forecast", OpenForecast);
                break;
        }
        if (items.Count > 0 && items[^1] is not ToolStripSeparator) items.Add(new ToolStripSeparator());

        if (held) Item("Resume switching by itself", () => { held = false; manualId = null; });
        else Item("Stay on this view for an hour", () => { manualId = views[current].Id; manualUntilUtc = DateTime.UtcNow.AddHours(1); held = true; });
        Item("Hide for an hour", () => { hiddenUntilUtc = DateTime.UtcNow.AddHours(1); Log.Write("hidden for an hour (start Glance again to show it)"); });
        Item("Refresh", () =>
        {
            weather.Force();
            calendar.Tick(settings.Agenda, true);
            todoist.Tick(settings.Agenda, true);
            foreach (Overlay o in overlays) o.MarkStale();
        });
        items.Add(new ToolStripSeparator());
        Item("Settings…", () => OpenSettings());
        UpdateService.ReadyUpdate ready = updates.Ready;
        if (ready != null) Item("Install update " + ready.Version, () => { if (updates.Apply()) ExitThread(); });
        else Item("Check for updates", () => { updates.Tick(false, true); Toast("CHECKING FOR UPDATES…"); });
        Item("Quit C.A.R.S. Glance", () => { Program.SetQuitFlag(true); ExitThread(); });

        menu.Show(Cursor.Position);
        Native.SetForegroundWindow(menu.Handle); // so a click anywhere else closes it
    }

    static string Short(string s) => s.Length <= 34 ? s : s[..32].TrimEnd() + "…";

    // Pin / unpin a countdown in settings.json (the Settings window shows the same switch).
    void SetPinned(string name, bool pinned)
    {
        CountdownSetting c = settings.Countdowns.FirstOrDefault(x => x.Name == name);
        if (c == null) return;
        c.Pinned = pinned;
        if (!Settings.Save(settings)) Toast("COULDN'T SAVE SETTINGS", 4);
    }

    void SetInterval(int ms)
    {
        if (timer.Interval != ms) timer.Interval = ms;
    }

    bool ForegroundRect(IntPtr host, out Native.RECT rect)
    {
        rect = default;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == host || fg == Native.GetShellWindow()) return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == ownPid) return false;
        return Native.GetWindowRect(fg, out rect);
    }

    static bool Contains(Native.RECT outer, Rectangle inner) =>
        outer.Left <= inner.Left && outer.Top <= inner.Top && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    // One panel per display (except the ones turned off in Settings). Rebuilt when the displays or the look change.
    void SyncMonitors()
    {
        List<Native.Display> displays = Native.Displays();
        AppearanceSetting look = settings.Appearance;
        bool hc = settings.Accessibility.HighContrast;
        var sig = new StringBuilder();
        foreach (Native.Display d in displays) sig.Append(d.Bounds).Append(d.Work).Append(d.Name).Append('|');
        sig.Append(look.Accent).Append(look.Glass).Append(look.Size).Append(look.Position).Append(string.Join(",", look.HiddenDisplays)).Append(hc);
        if (sig.ToString() == monitorSignature) return;
        monitorSignature = sig.ToString();
        foreach (Overlay o in overlays) o.Dispose();
        overlays.Clear();
        Style.Apply(look, hc);
        List<Native.Display> shown = displays.Where(d => !look.HiddenDisplays.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (Native.Display d in shown) overlays.Add(new Overlay(d, look, OnInput));
    }

    // Reduce motion (Settings, or Windows' "Animation effects" off) and battery saver: changes happen at once.
    bool Calm() =>
        settings.Accessibility.ReduceMotion == "on" ||
        (settings.Accessibility.ReduceMotion == "system" && !Native.WindowsAnimationsOn()) ||
        (settings.QuietOnBattery && Native.BatterySaverOn());

    // The top-level window that hosts the desktop icons view: Progman on Windows 11 24H2+,
    // or the WorkerW that holds SHELLDLL_DefView on older builds.
    IntPtr DesktopHost()
    {
        if (desktopHost != IntPtr.Zero && Native.IsWindow(desktopHost) &&
            Native.FindWindowEx(desktopHost, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return desktopHost;
        desktopHost = IntPtr.Zero;
        IntPtr progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero && Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return desktopHost = progman;
        IntPtr worker = IntPtr.Zero;
        while ((worker = Native.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            if (Native.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return desktopHost = worker;
        return IntPtr.Zero;
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged;
        watcher?.Dispose();
        foreach (Overlay o in overlays) o.Dispose();
        overlays.Clear();
        base.ExitThreadCore();
    }
}

// ------------------------------------------------------------------ one display

sealed class Overlay : IDisposable
{
    public readonly Rectangle Monitor;
    public bool Animating;
    public string UpdateText;      // "UPDATE READY · 0.9.1" while a verified update waits, else null
    public string HintText;        // the top-left pill: a short message, or the first-run hint

    readonly LayeredWindow panel;
    readonly PanelArt art;
    readonly Surface surface;
    readonly int px, py;
    readonly Action<WidgetInput> input;
    readonly FrameState f = new();
    readonly double[] colStart = { -1, -1, -1, -1 };
    readonly double[] pressStart = { -1, -1, -1 };
    double transStart = -1, transMs, contentStart = -1, dotStart = -1, morphStart = -1, lastNow = -1;
    int transDir = 1, pendingPress = -1;
    bool transVertical;
    float morphFrom = 1, morphTo = 1;
    string shownKey, shownViewId, shownElapsed, shownLeft, shownControls, shownUpdate;
    int shownDots = -1, shownDot = -1, shownFill = -2, hoverButton = -1;
    bool hover, stale = true, shown, covered, animate, hoverChanged;
    string shownHint;

    /// <summary>Redraw everything on the next frame (after sleep or a clock change).</summary>
    public void MarkStale() => stale = true;

    /// <summary>"Hide for an hour": the panel goes away until the next frame after that.</summary>
    public void Hide()
    {
        if (!shown) return;
        panel.Hide();
        shown = false;
        stale = true;
    }

    public Overlay(Native.Display display, AppearanceSetting look, Action<WidgetInput> input)
    {
        Rectangle monitor = display.Bounds, work = display.Work.IsEmpty ? display.Bounds : display.Work;
        Monitor = monitor;
        this.input = input;
        float baseScale = (float)Math.Min(monitor.Height / 1440.0, monitor.Width / 2000.0);
        float s = baseScale * look.Size / 100f;
        s = Math.Min(s, (work.Width - 48) / 1120f); // never wider than the display
        s = Math.Min(s, (work.Height - 48) / 360f);
        panel = new LayeredWindow(new Point(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2), "C.A.R.S. Glance", OnMouse);
        uint dpi = 96;
        try { dpi = Native.GetDpiForWindow(panel.Handle); } catch { }
        art = new PanelArt(s, (int)Math.Round(8 * dpi / 96.0)); // Windows 11 rounds the window with this radius
        // where it sits: centred, or along an edge / in a corner of the work area (clear of the taskbar)
        int margin = (int)Math.Round(56 * baseScale);
        string pos = look.Position ?? "center";
        px = pos.EndsWith("left") ? work.Left + margin : pos.EndsWith("right") ? work.Right - margin - art.W : work.Left + (work.Width - art.W) / 2;
        py = pos.StartsWith("top") ? work.Top + margin : pos.StartsWith("bottom") ? work.Bottom - margin - art.H : monitor.Y + (monitor.Height - art.H) / 2;
        surface = new Surface(art.W, art.H);
        Native.EnableBlurBehind(panel.Handle);
        Native.SetRoundedCorners(panel.Handle);
        panel.SetBounds(px, py, art.W, art.H);
    }

    // 1.0: a click only does what's under the pointer (empty glass does nothing); the dots, the ‹ › edges and
    // the scroll wheel switch views; right-click opens the menu; scrolling over a song's cover changes its volume.
    void OnMouse(int msg, IntPtr wParam, IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
            {
                Hit h = art.HitTest(f, x, y);
                panel.HandCursor = h.Any;
                bool changed = h.Kind != f.HoverHit.Kind || h.Index != f.HoverHit.Index;
                f.HoverHit = h;
                hoverButton = h.Kind == HitKind.Media ? h.Index : -1;
                if (!hover) { hover = true; panel.TrackLeave(); changed = true; }
                if (changed) { hoverChanged = true; input(new WidgetInput(InputKind.Hover)); }
                break;
            }
            case Native.WM_MOUSELEAVE:
                hover = false;
                hoverButton = -1;
                f.HoverHit = Hit.Nothing;
                hoverChanged = true;
                input(new WidgetInput(InputKind.Hover));
                break;
            case Native.WM_LBUTTONUP:
            {
                Hit h = art.HitTest(f, x, y);
                if (!h.Any) break;
                if (h.Kind == HitKind.Media)
                {
                    if (f.View?.Media == null || !f.View.Media.Can((MediaButton)h.Index)) break;
                    pendingPress = h.Index; // this screen shows the press
                }
                float value = h.Kind == HitKind.Seek && f.View?.Media != null ? art.SeekFraction(f.View.Media, x) : 0;
                input(new WidgetInput(InputKind.Act, h.Index, h.Kind, value, ItemAt(h)));
                break;
            }
            case Native.WM_RBUTTONUP:
                input(new WidgetInput(InputKind.Menu, -1, HitKind.None, 0, ItemAt(art.HitTest(f, x, y))));
                break;
            case Native.WM_MOUSEWHEEL:
            {
                short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                // the wheel's lParam is in screen coordinates
                Hit h = art.HitTest(f, x - px, y - py);
                if (h.Kind == HitKind.Cover) input(new WidgetInput(InputKind.Volume, -1, HitKind.Cover, delta / 120f));
                else input(new WidgetInput(delta > 0 ? InputKind.Previous : InputKind.Next));
                break;
            }
        }
    }

    // Which agenda item a hit is about (the row's task or event id).
    string ItemAt(Hit h) =>
        h.Index >= 0 && f.View?.Rows != null && h.Index < f.View.Rows.Count ? f.View.Rows[h.Index].ItemId : null;

    public void Housekeep(IntPtr host, uint ownPid, bool gameMode, bool coveredByFullscreen, bool calm = false)
    {
        bool wasCovered = covered;
        covered = coveredByFullscreen;
        if (wasCovered && !covered) stale = true;
        bool occluded = shown && !covered && panel.IsOccluded(ownPid);
        animate = !gameMode && !covered && !occluded && !calm;
        if (shown && host != IntPtr.Zero) panel.KeepOnDesktop(host, ownPid);
    }

    // dir: +1 when the new view comes from the right (next), -1 from the left (previous).
    public void Frame(ViewModel vm, int index, int count, int dir, double now, IntPtr host, uint ownPid)
    {
        Animating = false;
        if (covered && shown) return; // a full-screen game or video hides this display: do nothing
        double dt = lastNow < 0 ? 16 : Math.Clamp(now - lastNow, 0, 100);
        lastNow = now;

        string key = vm.FadeKey;
        bool switching = shown && !stale && key != shownKey;
        if (switching && animate)
        {
            Bitmap snap = art.Snapshot(f); // what is on screen now, mid-transition included
            f.FadeFrom?.Dispose();
            f.FadeFrom = snap;
            transVertical = vm.Id == shownViewId; // same view, new song or "OUT NOW": rise; another view: slide
            transDir = dir;
            transMs = transVertical ? Motion.SongMs : Motion.SwitchMs;
            transStart = now;
            Motion.Transition(f, 0, transDir, transVertical, art.S);
        }
        bool contentChanged = art.EnsureContent(vm, out Bitmap previous);
        if (contentChanged && shown && !stale && !switching && animate && previous != null)
        {
            f.ContentFrom?.Dispose();
            f.ContentFrom = previous;
            f.ContentT = 0;
            contentStart = now;
        }
        else previous?.Dispose();
        f.View = vm;
        Rectangle dirty = Rectangle.Empty;

        if (stale || !shown || switching)
        {
            for (int i = 0; i < 4; i++)
            {
                f.Cur[i] = vm.Values != null && i < vm.Values.Length ? vm.Values[i] : null;
                f.Prev[i] = null;
                f.ColT[i] = 1;
                colStart[i] = -1;
            }
            if (!switching || !animate)
            {
                f.FadeFrom?.Dispose();
                f.FadeFrom = null;
                transStart = -1;
                Motion.Rest(f);
            }
            f.ContentFrom?.Dispose();
            f.ContentFrom = null;
            f.ContentT = 1;
            contentStart = -1;
            if (vm.Media != null) { f.PlayMorph = morphFrom = morphTo = vm.Media.Playing ? 1 : 0; morphStart = -1; }
            shownKey = key;
            shownViewId = vm.Id;
            stale = false;
            dirty = art.Full;
        }
        else
        {
            if (contentChanged) dirty = art.Full;
            DigitRow row = art.RowFor(vm);
            if (row != null)
            {
                for (int i = 0; i < vm.Values.Length; i++)
                {
                    if (vm.Values[i] == f.Cur[i]) continue;
                    f.Prev[i] = f.Cur[i];
                    f.Cur[i] = vm.Values[i];
                    colStart[i] = animate ? now : -1;
                    f.ColT[i] = 1;
                    dirty = Union(dirty, row.ColRects[i]);
                }
            }
        }

        // page indicator: glide from where it is to the new dot (jump if the number of views changed)
        if (count != shownDots)
        {
            shownDots = count; shownDot = index;
            f.DotCount = count; f.DotIndex = index;
            f.DotFrom = f.DotTo = index; f.DotT = 1; dotStart = -1;
            dirty = Union(dirty, art.DotsRect);
        }
        else if (index != shownDot)
        {
            f.DotFrom = Motion.DotCentre(f.DotFrom, f.DotTo, f.DotT);
            f.DotTo = index;
            f.DotIndex = index;
            shownDot = index;
            if (animate) { f.DotT = 0; dotStart = now; } else { f.DotFrom = index; f.DotT = 1; dotStart = -1; }
            dirty = Union(dirty, art.DotsRect);
        }
        if (dotStart >= 0)
        {
            double t = (now - dotStart) / Motion.DotMs;
            if (t >= 1) { f.DotT = 1; f.DotFrom = f.DotTo; dotStart = -1; } else { f.DotT = (float)t; Animating = true; }
            dirty = Union(dirty, art.DotsRect);
        }

        // the "Update ready" pill appears (or goes) in its own corner
        if (UpdateText != shownUpdate)
        {
            shownUpdate = UpdateText;
            f.UpdateText = UpdateText;
            dirty = Union(dirty, art.UpdateRect);
        }

        // the top-left pill (a message or the first-run hint) and the hover layer (plates, arrows) redraw the panel
        if (HintText != shownHint) { shownHint = HintText; f.HintText = HintText; dirty = art.Full; }
        if (hoverChanged) { hoverChanged = false; dirty = art.Full; }

        // panel edge brightens under the pointer
        float hoverTarget = hover ? 1 : 0;
        if (f.HoverT != hoverTarget)
        {
            f.HoverT = animate ? Motion.Toward(f.HoverT, hoverTarget, dt, Motion.HoverMs) : hoverTarget;
            if (f.HoverT != hoverTarget) Animating = true;
            dirty = art.Full;
        }

        // Now Playing: the bar and times redraw only when a pixel or a second changes; buttons on hover, press or state.
        if (vm.Kind == ViewKind.NowPlaying)
        {
            f.Progress = Views.MediaTimes(vm.Media, DateTime.UtcNow, out f.Elapsed, out f.Left);
            int fill = art.FillWidth(f.Progress, vm.Media);
            if (fill != shownFill || f.Elapsed != shownElapsed || f.Left != shownLeft)
            {
                shownFill = fill; shownElapsed = f.Elapsed; shownLeft = f.Left;
                dirty = Union(dirty, art.ProgressRect);
            }
            string controls = vm.Media.ControlsKey;
            if (controls != shownControls) { shownControls = controls; dirty = Union(dirty, art.ControlsRect); }

            bool buttonsMoving = false, ringsSettling = false;
            for (int i = 0; i < 3; i++)
            {
                float target = hover && hoverButton == i && vm.Media.Can((MediaButton)i) ? 1 : 0;
                if (f.Ring[i] != target)
                {
                    f.Ring[i] = animate ? Motion.Toward(f.Ring[i], target, dt, Motion.RingMs) : target;
                    buttonsMoving = true;
                    ringsSettling |= f.Ring[i] != target;
                }
            }
            if (pendingPress >= 0) { if (animate) pressStart[pendingPress] = now; pendingPress = -1; }
            for (int i = 0; i < 3; i++)
            {
                if (pressStart[i] < 0) continue;
                double t = (now - pressStart[i]) / Motion.PressMs;
                if (t >= 1) { f.Press[i] = 0; pressStart[i] = -1; } else f.Press[i] = Motion.Bump((float)t);
                buttonsMoving = true;
            }
            float morphTarget = vm.Media.Playing ? 1 : 0;
            if (morphTarget != morphTo)
            {
                morphFrom = f.PlayMorph;
                morphTo = morphTarget;
                morphStart = animate ? now : -1;
                if (!animate) f.PlayMorph = morphTo;
                buttonsMoving = true;
            }
            if (morphStart >= 0)
            {
                double t = (now - morphStart) / Motion.MorphMs;
                if (t >= 1) { f.PlayMorph = morphTo; morphStart = -1; }
                else f.PlayMorph = morphFrom + (morphTo - morphFrom) * Motion.EaseInOut((float)t);
                buttonsMoving = true;
            }
            if (buttonsMoving)
            {
                dirty = Union(dirty, art.ControlsRect);
                Animating |= morphStart >= 0 || pressStart.Any(p => p >= 0) || ringsSettling;
            }
        }
        else
        {
            Array.Clear(f.Ring);
            Array.Clear(f.Press);
            Array.Fill(pressStart, -1);
            pendingPress = -1;
            shownFill = -2;
            shownControls = null;
        }

        DigitRow active = art.RowFor(vm);
        for (int i = 0; i < 4; i++)
        {
            if (colStart[i] < 0) continue;
            double t = (now - colStart[i]) / Motion.RollMs;
            if (t >= 1) { t = 1; colStart[i] = -1; } else Animating = true;
            f.ColT[i] = (float)t;
            if (active != null && i < active.ColRects.Length) dirty = Union(dirty, active.ColRects[i]);
        }
        if (contentStart >= 0)
        {
            double t = (now - contentStart) / Motion.ContentMs;
            if (t >= 1) { f.ContentT = 1; f.ContentFrom?.Dispose(); f.ContentFrom = null; contentStart = -1; }
            else { f.ContentT = Motion.Smooth((float)t); Animating = true; }
            dirty = art.Full;
        }
        if (transStart >= 0)
        {
            double t = (now - transStart) / transMs;
            if (t >= 1) { f.FadeFrom?.Dispose(); f.FadeFrom = null; transStart = -1; Motion.Rest(f); }
            else { Motion.Transition(f, (float)t, transDir, transVertical, art.S); Animating = true; }
            dirty = art.Full;
        }

        if (!dirty.IsEmpty)
        {
            art.Render(surface.G, dirty, f);
            surface.Present(panel.Handle, px, py, dirty);
        }
        if (!shown && host != IntPtr.Zero)
        {
            panel.KeepOnDesktop(host, ownPid);
            shown = true;
        }
    }

    static Rectangle Union(Rectangle a, Rectangle b) => a.IsEmpty ? b : Rectangle.Union(a, b);

    public void Dispose()
    {
        panel.Dispose();
        surface.Dispose();
        art.Dispose();
        f.FadeFrom?.Dispose();
        f.ContentFrom?.Dispose();
    }
}

// ------------------------------------------------------------------ desktop-layer window that takes clicks but never focus

sealed class LayeredWindow : NativeWindow, IDisposable
{
    readonly Action<int, IntPtr, IntPtr> mouse;
    Native.RECT bounds;
    bool shown;

    public LayeredWindow(Point centre, string caption, Action<int, IntPtr, IntPtr> mouse)
    {
        this.mouse = mouse;
        var cp = new CreateParams
        {
            Caption = caption,
            Style = unchecked((int)Native.WS_POPUP),
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
            X = centre.X,
            Y = centre.Y,
            Width = 1,
            Height = 1,
        };
        CreateHandle(cp);
    }

    public void SetBounds(int x, int y, int w, int h)
    {
        bounds.Left = x; bounds.Top = y; bounds.Right = x + w; bounds.Bottom = y + h;
    }

    public void TrackLeave() => Native.TrackLeave(Handle);

    public void Hide()
    {
        Native.ShowWindow(Handle, 0 /* SW_HIDE */);
        shown = false;
    }

    // Keep this window directly above the desktop host: under every app window, over the wallpaper.
    // When "Show desktop" raises the desktop above the apps, this follows it so it stays visible.
    public void KeepOnDesktop(IntPtr host, uint ownPid)
    {
        if (shown && SitsDirectlyAbove(host, ownPid)) return;
        IntPtr above = Native.GetWindow(host, Native.GW_HWNDPREV);
        uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
        if (!shown) flags |= Native.SWP_SHOWWINDOW;
        if (above == Handle)
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, flags | Native.SWP_NOZORDER);
        else if (above == IntPtr.Zero || Native.IsTopmost(above))
            Native.SetWindowPos(Handle, Native.HWND_TOP, 0, 0, 0, 0, flags);
        else
            Native.SetWindowPos(Handle, above, 0, 0, 0, 0, flags);
        shown = true;
    }

    bool SitsDirectlyAbove(IntPtr host, uint ownPid)
    {
        IntPtr w = Handle;
        for (int i = 0; i < 10000; i++)
        {
            w = Native.GetWindow(w, Native.GW_HWNDNEXT);
            if (w == IntPtr.Zero) return false; // we are below the desktop host
            if (w == host) return true;
            Native.GetWindowThreadProcessId(w, out uint pid);
            if (pid == ownPid) continue;
            if (Native.IsOnScreen(w) && Native.Intersects(w, bounds)) return false; // we would be covering an app
        }
        return false;
    }

    // True when a single visible window above this one hides it completely.
    public bool IsOccluded(uint ownPid)
    {
        IntPtr w = Handle;
        for (int i = 0; i < 10000; i++)
        {
            w = Native.GetWindow(w, Native.GW_HWNDPREV);
            if (w == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(w, out uint pid);
            if (pid == ownPid) continue;
            if (Native.ContainsRect(w, bounds) && Native.IsOnScreen(w)) return true;
        }
        return false;
    }

    /// <summary>The hand appears only over something you can click; plain glass shows the normal arrow.</summary>
    public bool HandCursor;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE: m.Result = (IntPtr)Native.MA_NOACTIVATE; return; // clicks never take focus
            case Native.WM_SETCURSOR: Native.SetCursor(Native.LoadCursor(IntPtr.Zero, HandCursor ? Native.IDC_HAND : Native.IDC_ARROW)); m.Result = (IntPtr)1; return;
            case Native.WM_DPICHANGED: m.Result = IntPtr.Zero; return; // size is set per monitor
            case Native.WM_MOUSEMOVE:
            case Native.WM_MOUSELEAVE:
            case Native.WM_LBUTTONUP:
            case Native.WM_RBUTTONUP:
            case Native.WM_MOUSEWHEEL:
                try { mouse(m.Msg, m.WParam, m.LParam); } catch (Exception ex) { Log.Write("input: " + ex.Message); }
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}

// A premultiplied 32-bit DIB that GDI+ draws into and UpdateLayeredWindowIndirect presents,
// so animation frames only copy the part that changed.
sealed class Surface : IDisposable
{
    public readonly int W, H;
    public readonly Graphics G;
    readonly Bitmap bmp;
    readonly IntPtr memDc, hbmp, oldBmp, info;

    public Surface(int w, int h)
    {
        W = w; H = h;
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        memDc = Native.CreateCompatibleDC(screen);
        Native.ReleaseDC(IntPtr.Zero, screen);
        hbmp = Native.CreateDIBSection(memDc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        oldBmp = Native.SelectObject(memDc, hbmp);
        bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        G = Art.Canvas(bmp);
        info = Marshal.AllocHGlobal(64);
    }

    public void Present(IntPtr hwnd, int x, int y, Rectangle dirty)
    {
        G.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        Native.GdiFlush();
        dirty.Intersect(new Rectangle(0, 0, W, H));
        // unmanaged block: dst POINT @0, SIZE @8, src POINT @16, BLENDFUNCTION @24, dirty RECT @28
        Marshal.WriteInt32(info, 0, x); Marshal.WriteInt32(info, 4, y);
        Marshal.WriteInt32(info, 8, W); Marshal.WriteInt32(info, 12, H);
        Marshal.WriteInt32(info, 16, 0); Marshal.WriteInt32(info, 20, 0);
        Marshal.WriteInt32(info, 24, 0x01FF0000); // BlendOp 0, flags 0, SourceConstantAlpha 255, AC_SRC_ALPHA
        Marshal.WriteInt32(info, 28, dirty.Left); Marshal.WriteInt32(info, 32, dirty.Top);
        Marshal.WriteInt32(info, 36, dirty.Right); Marshal.WriteInt32(info, 40, dirty.Bottom);
        var u = new Native.UPDATELAYEREDWINDOWINFO
        {
            cbSize = Marshal.SizeOf<Native.UPDATELAYEREDWINDOWINFO>(),
            pptDst = info,
            psize = info + 8,
            hdcSrc = memDc,
            pptSrc = info + 16,
            pblend = info + 24,
            dwFlags = 2, // ULW_ALPHA
            prcDirty = dirty.IsEmpty ? IntPtr.Zero : info + 28,
        };
        Native.UpdateLayeredWindowIndirect(hwnd, ref u);
    }

    public void Dispose()
    {
        G.Dispose();
        bmp.Dispose();
        Native.SelectObject(memDc, oldBmp);
        Native.DeleteObject(hbmp);
        Native.DeleteDC(memDc);
        Marshal.FreeHGlobal(info);
    }
}
