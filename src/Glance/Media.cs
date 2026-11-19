// Now Playing: the Windows half (the system media session API and its events).
using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using DrawColor = System.Drawing.Color;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Glance;

sealed class MediaService
{
    volatile NowPlayingInfo current;
    volatile string[] apps = { "Cider" };
    volatile bool enabled = true, starting, sessionsChanged, artWanted = true;
    volatile SessionManager manager;
    volatile Session session;  // the matched player, with its change events hooked
    string sessionApp;
    DateTime nextPollUtc, nextStartUtc;

    readonly SemaphoreSlim gate = new(1, 1);
    int waiting;
    bool loggedError;

    // refresh-thread state
    string title = "", artist = "", album = "", artTrack;
    AlbumArt art;
    int artVersion;
    DateTime nextPropsUtc;

    // A play/pause press shows at once; this keeps a slow report from flipping it back.
    volatile bool heldPlaying;
    DateTime heldUntilUtc;

    public NowPlayingInfo Current => enabled ? current : null;

    // An empty list means any player.
    public static bool Matches(string appId, IEnumerable<string> wanted)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        var names = wanted.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        return names.Count == 0 || names.Any(a => appId.Contains(a.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // UI thread, from housekeeping. Polls once a second (every 5 s while a game is full screen);
    // the player's own change events trigger extra refreshes in between.
    public void Tick(NowPlayingSetting s, bool quiet)
    {
        enabled = s.Enabled;
        apps = s.Apps.ToArray();
        if (!s.Enabled) { current = null; return; }
        DateTime now = DateTime.UtcNow;
        if (manager == null)
        {
            if (!starting && now >= nextStartUtc) { starting = true; Task.Run(StartAsync); }
            return;
        }
        if (now >= nextPollUtc)
        {
            nextPollUtc = now.AddSeconds(quiet ? 5 : 1);
            Request(0);
        }
    }

    // Waits up to a few seconds for the first snapshot (used by --preview --live).
    public NowPlayingInfo WaitForSong(NowPlayingSetting s, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Tick(s, false);
            NowPlayingInfo n = current;
            if (n != null && n.Art != null) return n;
            Thread.Sleep(100);
        }
        return current;
    }

    async Task StartAsync()
    {
        try
        {
            SessionManager m = await SessionManager.RequestAsync();
            m.SessionsChanged += (s, e) => { sessionsChanged = true; Request(0); };
            manager = m;
            Request(0);
        }
        catch (Exception ex)
        {
            LogOnce("media start: " + ex.Message);
            nextStartUtc = DateTime.UtcNow.AddMinutes(1);
        }
        finally { starting = false; }
    }

    void Request(int delayMs)
    {
        Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (Interlocked.Exchange(ref waiting, 1) == 1) return; // one is already queued
            await gate.WaitAsync();
            Interlocked.Exchange(ref waiting, 0);
            try { await RefreshAsync(); }
            catch (Exception ex) { LogOnce("media: " + ex.Message); }
            finally { gate.Release(); }
        });
    }

    async Task RefreshAsync()
    {
        SessionManager m = manager;
        if (m == null || !enabled) { current = null; return; }

        string[] wanted = apps;
        Session pick = null;
        bool pickPlaying = false;
        foreach (Session s in m.GetSessions())
        {
            if (!Matches(s.SourceAppUserModelId, wanted)) continue;
            bool playing = s.GetPlaybackInfo()?.PlaybackStatus == Status.Playing;
            if (pick == null || (playing && !pickPlaying)) { pick = s; pickPlaying = playing; }
        }
        if (pick == null)
        {
            Hook(null);
            current = null;
            return;
        }
        string app = pick.SourceAppUserModelId;
        if (app != sessionApp || sessionsChanged)
        {
            sessionsChanged = false;
            Hook(pick);
            nextPropsUtc = DateTime.MinValue;
        }

        DateTime now = DateTime.UtcNow;
        var pb = pick.GetPlaybackInfo();
        var tl = pick.GetTimelineProperties();

        if (now >= nextPropsUtc || artWanted)
        {
            nextPropsUtc = now.AddSeconds(5);
            var props = await pick.TryGetMediaPropertiesAsync();
            if (props != null)
            {
                title = props.Title ?? "";
                artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
                album = props.AlbumTitle ?? "";
                string track = title + "\u0001" + artist + "\u0001" + album;
                bool newTrack = track != artTrack;
                if (newTrack || artWanted || (art == null && props.Thumbnail != null))
                {
                    artWanted = false;
                    if (newTrack)
                    {
                        art = null;
                        artTrack = track;
                        // Players sometimes report the new title before the new cover: look again shortly.
                        _ = Task.Run(async () => { await Task.Delay(1500); artWanted = true; Request(0); });
                    }
                    try
                    {
                        AlbumArt fresh = await LoadArt(props.Thumbnail, artVersion + 1);
                        if (fresh != null && fresh.Hash != art?.Hash) { art = fresh; artVersion = fresh.Version; }
                    }
                    catch (Exception ex) { LogOnce("album art: " + ex.Message); }
                }
            }
        }

        NowPlayingInfo prev = current;
        var n = new NowPlayingInfo
        {
            AppId = app,
            Title = title,
            Artist = artist,
            Album = album,
            Art = art,
            Playing = pb?.PlaybackStatus == Status.Playing,
            CanPrevious = pb?.Controls.IsPreviousEnabled ?? false,
            CanNext = pb?.Controls.IsNextEnabled ?? false,
            CanPlayPause = pb != null && (pb.Controls.IsPlayEnabled || pb.Controls.IsPauseEnabled || pb.Controls.IsPlayPauseToggleEnabled),
            CanSeek = pb?.Controls.IsPlaybackPositionEnabled ?? false,
            CanShuffle = pb?.Controls.IsShuffleEnabled ?? false,
            CanRepeat = pb?.Controls.IsRepeatEnabled ?? false,
            Shuffle = pb?.IsShuffleActive ?? false,
            Repeat = pb?.AutoRepeatMode switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.Track => 1,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => 2,
                _ => 0,
            },
        };
        if (now < heldUntilUtc) n.Playing = heldPlaying;
        if (tl != null)
        {
            n.Duration = tl.EndTime - tl.StartTime;
            n.Position = tl.Position - tl.StartTime;
            DateTime updated = tl.LastUpdatedTime.UtcDateTime;
            n.PositionUtc = updated.Year > 2000 && updated <= now.AddSeconds(1) ? updated : now;
        }
        if (!n.Playing)
        {
            // Hold the last moving position while paused, so the bar doesn't jump back.
            if (prev != null && prev.TrackKey == n.TrackKey && prev.Playing && Math.Abs((prev.PositionAt(now) - n.Position).TotalSeconds) < 3)
            {
                n.Position = prev.PositionAt(now);
                n.PositionUtc = now;
            }
            n.PausedSinceUtc = prev == null ? DateTime.MinValue : prev.Playing ? now : prev.PausedSinceUtc;
        }
        current = n;
    }

    static async Task<AlbumArt> LoadArt(IRandomAccessStreamReference thumb, int version)
    {
        if (thumb == null) return null;
        using IRandomAccessStreamWithContentType stream = await thumb.OpenReadAsync();
        if (stream.Size == 0) return null;
        BitmapDecoder dec = await BitmapDecoder.CreateAsync(stream);
        uint w = dec.OrientedPixelWidth, h = dec.OrientedPixelHeight;
        if (w == 0 || h == 0) return null;
        uint side = Math.Min(w, h), size = Math.Min(512u, side);
        double scale = size / (double)side;
        uint sw = Math.Max(size, (uint)Math.Round(w * scale)), sh = Math.Max(size, (uint)Math.Round(h * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = sw,
            ScaledHeight = sh,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (sw - size) / 2, Y = (sh - size) / 2, Width = size, Height = size }, // centre square
        };
        PixelDataProvider data = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] bgra = data.DetachPixelData();
        if (bgra.Length < size * size * 4) return null;
        var argb = new int[size * size];
        System.Buffer.BlockCopy(bgra, 0, argb, 0, argb.Length * 4); // BGRA bytes are ARGB ints on x86
        return new AlbumArt((int)size, argb, version, Convert.ToHexString(SHA256.HashData(bgra)));
    }

    void Hook(Session s)
    {
        Session old = session;
        if (old != null)
        {
            try
            {
                old.MediaPropertiesChanged -= OnProperties;
                old.PlaybackInfoChanged -= OnPlayback;
                old.TimelinePropertiesChanged -= OnTimeline;
            }
            catch { }
        }
        session = s;
        sessionApp = s?.SourceAppUserModelId;
        if (s == null) return;
        s.MediaPropertiesChanged += OnProperties;
        s.PlaybackInfoChanged += OnPlayback;
        s.TimelinePropertiesChanged += OnTimeline;
    }

    void OnProperties(Session s, MediaPropertiesChangedEventArgs e) { artWanted = true; Request(250); }
    void OnPlayback(Session s, PlaybackInfoChangedEventArgs e) => Request(0);
    void OnTimeline(Session s, TimelinePropertiesChangedEventArgs e) => Request(0);

    // UI thread. Play/pause flips on screen at once; the player's report confirms it a moment later.
    // Jump to a point in the song (0..1), if the player allows it.
    public void Seek(float fraction)
    {
        Session s = session;
        NowPlayingInfo cur = current;
        if (s == null || cur == null || !cur.CanSeek || !cur.HasTimeline) return;
        long ticks = (long)(cur.Duration.Ticks * Math.Clamp(fraction, 0, 1));
        var tl = s.GetTimelineProperties();
        long target = (tl?.StartTime.Ticks ?? 0) + ticks;
        // show it at once; the player's report confirms it
        NowPlayingInfo n = cur.Clone();
        n.Position = TimeSpan.FromTicks(ticks);
        n.PositionUtc = DateTime.UtcNow;
        current = n;
        Task.Run(async () =>
        {
            try { await s.TryChangePlaybackPositionAsync(target); }
            catch (Exception ex) { LogOnce("media seek: " + ex.Message); }
            Request(200);
        });
    }

    public void ToggleShuffle()
    {
        Session s = session;
        NowPlayingInfo cur = current;
        if (s == null || cur == null || !cur.CanShuffle) return;
        Task.Run(async () =>
        {
            try { await s.TryChangeShuffleActiveAsync(!cur.Shuffle); } catch (Exception ex) { LogOnce("media shuffle: " + ex.Message); }
            Request(200);
        });
    }

    // Off → the list → this song → off.
    public void CycleRepeat()
    {
        Session s = session;
        NowPlayingInfo cur = current;
        if (s == null || cur == null || !cur.CanRepeat) return;
        var next = cur.Repeat switch
        {
            0 => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            2 => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
        };
        Task.Run(async () =>
        {
            try { await s.TryChangeAutoRepeatModeAsync(next); } catch (Exception ex) { LogOnce("media repeat: " + ex.Message); }
            Request(200);
        });
    }

    // Brings the player's window up: Store apps through their Start-menu identity, desktop apps by process.
    public void OpenPlayer()
    {
        string app = current?.AppId ?? sessionApp;
        if (string.IsNullOrEmpty(app)) return;
        try
        {
            if (app.Contains('!'))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app) { UseShellExecute = false })?.Dispose();
                return;
            }
            string name = Path.GetFileNameWithoutExtension(app);
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    Native.ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                    Native.SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
        }
        catch (Exception ex) { LogOnce("open player: " + ex.Message); }
    }

    public string PlayerName
    {
        get
        {
            string app = current?.AppId ?? "";
            if (app.Contains("Cider", StringComparison.OrdinalIgnoreCase)) return "Cider";
            if (app.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
            if (app.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
            if (app.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase)) return "Media Player";
            return "the player";
        }
    }

    public void Command(MediaButton button)
    {
        Session s = session;
        NowPlayingInfo cur = current;
        if (s == null || cur == null || !cur.Can(button)) return;
        bool play = !cur.Playing;
        if (button == MediaButton.PlayPause)
        {
            DateTime now = DateTime.UtcNow;
            NowPlayingInfo n = cur.Clone();
            n.Position = cur.PositionAt(now);
            n.PositionUtc = now;
            n.Playing = play;
            n.PausedSinceUtc = play ? DateTime.MinValue : now;
            heldPlaying = play;
            heldUntilUtc = now.AddSeconds(1.5);
            current = n;
        }
        Task.Run(async () =>
        {
            try
            {
                bool ok = button switch
                {
                    MediaButton.Previous => await s.TrySkipPreviousAsync(),
                    MediaButton.Next => await s.TrySkipNextAsync(),
                    _ => play ? await s.TryPlayAsync() : await s.TryPauseAsync(),
                };
                if (!ok) heldUntilUtc = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                heldUntilUtc = DateTime.MinValue;
                LogOnce("media command: " + ex.Message);
            }
            Request(150);
            Request(800);
        });
    }

    void LogOnce(string message)
    {
        if (loggedError) return;
        loggedError = true;
        Log.Write(message);
    }
}

// AlbumArt keeps plain pixels (Core); turning them into a GDI+ bitmap is Windows-only.
static class AlbumArtBitmap
{

    public static System.Drawing.Bitmap ToBitmap(this AlbumArt art)
    {
        var b = new System.Drawing.Bitmap(art.Size, art.Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var d = b.LockBits(new System.Drawing.Rectangle(0, 0, art.Size, art.Size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(art.Pixels, 0, d.Scan0, art.Pixels.Length);
        b.UnlockBits(d);
        return b;
    }
}
