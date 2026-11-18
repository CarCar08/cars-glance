// The running widget: one glass panel per display on the desktop layer (above the wallpaper,
// below every app window, never focused), all showing the same view. Clicks and the scroll
// wheel switch views; Windows blurs the live wallpaper behind the glass.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Glance;

enum InputKind { Next, Previous, Dot, Hover, Media, Update }

readonly record struct WidgetInput(InputKind Kind, int Index = -1);

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
    // which way the next view switch slides: set by a click/scroll, otherwise from the view order
    int pendingDir, slideDir = 1, lastIndex;
    string lastViewId;

    public WidgetContext(EventWaitHandle quit, EventWaitHandle refresh)
    {
        this.quit = quit;
        this.refresh = refresh;
        settings = Settings.Load() ?? Settings.Parse("{}");
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
                foreach (Overlay o in overlays)
                    o.Housekeep(host, ownPid, gameMode, haveFg && Contains(fg, o.Monitor));
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
                updates.Tick(gameMode);
            }
            if (desktopHost == IntPtr.Zero) { SetInterval(250); return; } // Explorer not ready

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
            views = Views.Build(settings, nowLocal, weather.Latest, media.Current, agenda);
            string pickId = null;
            if (manualId != null && DateTime.UtcNow < manualUntilUtc && views.Any(v => v.Id == manualId)) pickId = manualId;
            else manualId = null;
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
            bool busy = false;
            foreach (Overlay o in overlays)
            {
                o.UpdateText = updateText;
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
        int n = views.Count;
        if (e.Kind == InputKind.Update)
        {
            // The verified Setup waits for this process to exit, updates, and starts the new version.
            if (updates.Apply()) { ExitThread(); return; }
        }
        else if (e.Kind == InputKind.Media)
        {
            // A button press keeps Now Playing up (as a manual choice), so pausing doesn't switch it away.
            media.Command((MediaButton)e.Index);
            ViewModel np = views.FirstOrDefault(v => v.Kind == ViewKind.NowPlaying);
            if (np != null)
            {
                manualId = np.Id;
                manualUntilUtc = DateTime.UtcNow.AddMinutes(settings.ManualViewMinutes);
            }
        }
        else if (e.Kind != InputKind.Hover && n > 1)
        {
            int next = e.Kind switch
            {
                InputKind.Next => (current + 1) % n,
                InputKind.Previous => (current - 1 + n) % n,
                _ => Math.Clamp(e.Index, 0, n - 1),
            };
            pendingDir = e.Kind switch
            {
                InputKind.Next => 1,
                InputKind.Previous => -1,
                _ => next >= current ? 1 : -1,
            };
            manualId = views[next].Id;
            manualUntilUtc = DateTime.UtcNow.AddMinutes(settings.ManualViewMinutes);
        }
        Tick();
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

    void SyncMonitors()
    {
        List<Rectangle> monitors = Native.Monitors();
        var sig = new StringBuilder();
        foreach (Rectangle r in monitors) sig.Append(r.ToString()).Append('|');
        if (sig.ToString() == monitorSignature) return;
        monitorSignature = sig.ToString();
        foreach (Overlay o in overlays) o.Dispose();
        overlays.Clear();
        foreach (Rectangle r in monitors) overlays.Add(new Overlay(r, OnInput));
    }

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
    bool hover, stale = true, shown, covered, animate;

    /// <summary>Redraw everything on the next frame (after sleep or a clock change).</summary>
    public void MarkStale() => stale = true;

    public Overlay(Rectangle monitor, Action<WidgetInput> input)
    {
        Monitor = monitor;
        this.input = input;
        float s = (float)Math.Min(monitor.Height / 1440.0, monitor.Width / 2000.0);
        panel = new LayeredWindow(new Point(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2), "C.A.R.S. Glance", OnMouse);
        uint dpi = 96;
        try { dpi = Native.GetDpiForWindow(panel.Handle); } catch { }
        art = new PanelArt(s, (int)Math.Round(8 * dpi / 96.0)); // Windows 11 rounds the window with this radius
        px = monitor.X + (monitor.Width - art.W) / 2;
        py = monitor.Y + (monitor.Height - art.H) / 2;
        surface = new Surface(art.W, art.H);
        Native.EnableBlurBehind(panel.Handle);
        Native.SetRoundedCorners(panel.Handle);
        panel.SetBounds(px, py, art.W, art.H);
    }

    void OnMouse(int msg, IntPtr wParam, IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        bool media = f.View?.Kind == ViewKind.NowPlaying;
        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
                int over = media ? art.ButtonAt(x, y) : -1;
                bool changed = over != hoverButton;
                hoverButton = over;
                if (!hover) { hover = true; panel.TrackLeave(); changed = true; }
                if (changed) input(new WidgetInput(InputKind.Hover));
                break;
            case Native.WM_MOUSELEAVE:
                hover = false;
                hoverButton = -1;
                input(new WidgetInput(InputKind.Hover));
                break;
            case Native.WM_LBUTTONUP:
                int dot = art.DotAt(x, y, f.DotCount);
                int button = media ? art.ButtonAt(x, y) : -1;
                if (f.UpdateText != null && art.UpdateRect.Contains(x, y)) input(new WidgetInput(InputKind.Update));
                else if (dot >= 0) input(new WidgetInput(InputKind.Dot, dot));
                else if (button >= 0)
                {
                    if (f.View.Media.Can((MediaButton)button))
                    {
                        pendingPress = button; // this screen shows the press
                        input(new WidgetInput(InputKind.Media, button));
                    }
                }
                else input(new WidgetInput(InputKind.Next));
                break;
            case Native.WM_MOUSEWHEEL:
                short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                input(new WidgetInput(delta > 0 ? InputKind.Previous : InputKind.Next));
                break;
        }
    }

    public void Housekeep(IntPtr host, uint ownPid, bool gameMode, bool coveredByFullscreen)
    {
        bool wasCovered = covered;
        covered = coveredByFullscreen;
        if (wasCovered && !covered) stale = true;
        bool occluded = shown && !covered && panel.IsOccluded(ownPid);
        animate = !gameMode && !covered && !occluded;
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

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE: m.Result = (IntPtr)Native.MA_NOACTIVATE; return; // clicks never take focus
            case Native.WM_SETCURSOR: Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.IDC_HAND)); m.Result = (IntPtr)1; return;
            case Native.WM_DPICHANGED: m.Result = IntPtr.Zero; return; // size is set per monitor
            case Native.WM_MOUSEMOVE:
            case Native.WM_MOUSELEAVE:
            case Native.WM_LBUTTONUP:
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
