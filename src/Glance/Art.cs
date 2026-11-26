// Drawing: the clear glass, each view's static text, rolling digits and the view dots.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Glance;

static class Style
{
    // The accent (Settings > Appearance; red by default), and how strong the secondary text and the glass are.
    // Set before the panels are built (Apply); a change rebuilds them.
    public static Color Accent = Color.FromArgb(255, 61, 139, 255);
    public static readonly Color Ink = Color.FromArgb(255, 255, 255, 255);
    public static Color Muted = Color.FromArgb(215, 255, 255, 255);
    public static Color Faint = Color.FromArgb(150, 255, 255, 255);
    public static int GlassAlpha = 100;                                      // how much the glass darkens the blurred wallpaper

    public static void Apply(AppearanceSetting look, bool highContrast)
    {
        Accent = look.AccentColor;
        GlassAlpha = highContrast ? 235 : (int)Math.Round(look.Glass * 2.5);
        Muted = Color.FromArgb(highContrast ? 255 : 215, 255, 255, 255);
        Faint = Color.FromArgb(highContrast ? 228 : 150, 255, 255, 255);
    }
    public const int TintAlpha = 34;                                         // Now Playing: the cover's colour over the glass
    public static readonly FontFamily Bold = Pick("Bahnschrift SemiBold", "Segoe UI Semibold", "Segoe UI");
    public static readonly FontFamily Text = Pick("Bahnschrift", "Segoe UI");
    // Song names in other scripts (Bahnschrift is Latin only and GDI+ paths don't fall back).
    public static readonly FontFamily WideBold = Pick("Segoe UI Semibold", "Segoe UI");
    public static readonly FontFamily WideText = Pick("Segoe UI");
    public static readonly FontFamily Cjk = Pick("Yu Gothic UI", "Microsoft YaHei UI", "Segoe UI");
    public static readonly FontFamily Hangul = Pick("Malgun Gothic", "Segoe UI");
    public static readonly FontFamily Symbol = Pick("Segoe UI Symbol", "Segoe UI");

    public static FontFamily For(string text, bool bold)
    {
        bool wide = false;
        foreach (char c in text ?? "")
        {
            if ((c >= 0x3040 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF)) return Cjk;
            if ((c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x1100 && c <= 0x11FF)) return Hangul;
            if (c >= 0x0370 && !(c >= 0x2000 && c <= 0x206F)) wide = true;
        }
        return wide ? (bold ? WideBold : WideText) : (bold ? Bold : Text);
    }

    static FontFamily Pick(params string[] names)
    {
        foreach (string n in names)
        {
            try { return new FontFamily(n); } catch (ArgumentException) { }
        }
        return FontFamily.GenericSansSerif;
    }
}

sealed class FrameState
{
    public ViewModel View;
    public string[] Cur = new string[4];
    public string[] Prev = new string[4];
    public float[] ColT = { 1, 1, 1, 1 };

    // switching views / songs: the old layer slides and fades out, the new one slides and fades in
    public Bitmap FadeFrom;                       // what was on screen (minus glass and dots)
    public float OldAlpha, NewAlpha = 1;
    public PointF OldOffset, NewOffset;
    // same view, new words (weather, PAUSED, AM/PM): the static layer crossfades in place
    public Bitmap ContentFrom;
    public float ContentT = 1;

    public int DotCount, DotIndex;
    public float DotFrom, DotTo, DotT = 1;        // the page indicator gliding between dots
    public float HoverT;                          // 0..1, the panel edge brightening under the pointer

    public float Progress = -1;    // Now Playing: 0..1, -1 = no bar
    public string Elapsed, Left;   // Now Playing: 1:23 and -2:23
    public readonly float[] Ring = new float[3];   // Now Playing: hover ring per button, 0..1
    public readonly float[] Press = new float[3];  // Now Playing: click dip per button, 0..1..0
    public float PlayMorph = 1;                    // Now Playing: 0 = play triangle, 1 = pause bars
    public string UpdateText;                      // the "Update ready" pill, or null
    public string HintText;                        // first-run hint pill (top left), or null
    public Hit HoverHit = Hit.Nothing;             // what the pointer is over
}

// One row of rolling digit columns with colons between them.
sealed class DigitRow : IDisposable
{
    public readonly Rectangle[] ColRects;
    public readonly float RowRight, DigitTop;
    readonly float s, px, originY, mid, shift;
    readonly float[] colX, sepCx;
    readonly float colW;
    readonly bool firstRight;
    readonly StringFormat fmt;
    readonly Dictionary<string, (GraphicsPath Path, float Adv)> cache = new();
    readonly GraphicsPath colon;

    public DigitRow(float s, int panelW, int columns, float colWidth, float sepWidth, float digitPx, float digitTop, float xShift, bool firstRightAligned, StringFormat fmt)
    {
        this.s = s;
        this.fmt = fmt;
        px = digitPx;
        DigitTop = digitTop;
        colW = colWidth;
        firstRight = firstRightAligned;
        RectangleF ink = Art.InkBounds("0123456789", Style.Bold, digitPx, fmt);
        originY = digitTop - ink.Top;
        mid = digitTop + ink.Height / 2;
        shift = ink.Height * 0.5f;

        float rowW = colWidth * columns + sepWidth * (columns - 1);
        float x0 = (panelW - rowW) / 2 + xShift;
        RowRight = x0 + rowW;
        int clipTop = (int)Math.Floor(digitTop - ink.Height * 0.24f);
        int clipBottom = (int)Math.Ceiling(digitTop + ink.Height * 1.24f);
        colX = new float[columns];
        sepCx = new float[Math.Max(0, columns - 1)];
        ColRects = new Rectangle[columns];
        for (int i = 0; i < columns; i++)
        {
            colX[i] = x0 + i * (colWidth + sepWidth);
            ColRects[i] = Rectangle.FromLTRB((int)Math.Floor(colX[i] - 4 * s), clipTop, (int)Math.Ceiling(colX[i] + colWidth + 4 * s), clipBottom);
            if (i < columns - 1) sepCx[i] = colX[i] + colWidth + sepWidth / 2;
        }

        colon = new GraphicsPath(FillMode.Winding);
        colon.AddString(":", Style.Bold, (int)FontStyle.Regular, digitPx * 0.8f, PointF.Empty, fmt);
        RectangleF cb = colon.GetBounds();
        using var m = new Matrix();
        m.Translate(-(cb.X + cb.Width / 2), -(cb.Y + cb.Height / 2));
        colon.Transform(m);
    }

    public void DrawColons(Graphics g)
    {
        using var b = new SolidBrush(Style.Accent);
        foreach (float cx in sepCx)
        {
            using var p = (GraphicsPath)colon.Clone();
            using var m = new Matrix();
            m.Translate(cx, mid);
            p.Transform(m);
            g.FillPath(b, p);
        }
    }

    public void DrawAll(Graphics g, string[] values, float alpha)
    {
        for (int i = 0; i < ColRects.Length; i++)
        {
            string text = values[i];
            if (string.IsNullOrEmpty(text)) continue;
            float[] x = Lefts(i, text);
            for (int k = 0; k < text.Length; k++) DrawChar(g, text[k], x[k], 0, (int)(255 * alpha));
        }
    }

    // Like an odometer: only the characters that changed roll (14 → 15 moves the 4, never the 1). Digits keep
    // their natural spacing; if a change alters the column's width (10 → 11), the digits that stay glide to their
    // new places instead of jumping. If the length changes (9 → 10) the whole column rolls.
    public void DrawColumn(Graphics g, Rectangle clip, int i, string cur, string prev, float t, float alpha)
    {
        g.SetClip(Rectangle.Intersect(clip, ColRects[i]));
        int a = (int)(255 * alpha);
        if (string.IsNullOrEmpty(cur)) { g.SetClip(clip); return; }
        float[] now = Lefts(i, cur);
        bool rolling = t < 1 && prev != null && prev != cur;
        if (!rolling)
        {
            for (int k = 0; k < cur.Length; k++) DrawChar(g, cur[k], now[k], 0, a);
        }
        else
        {
            (float move, float outY, float outA, float inY, float inA) = Roll(t);
            float[] was = Lefts(i, prev);
            if (prev.Length == cur.Length)
            {
                for (int k = 0; k < cur.Length; k++)
                {
                    if (cur[k] == prev[k]) { DrawChar(g, cur[k], was[k] + (now[k] - was[k]) * move, 0, a); continue; }
                    DrawChar(g, prev[k], was[k], outY, (int)(a * outA));
                    DrawChar(g, cur[k], now[k], inY, (int)(a * inA));
                }
            }
            else
            {
                for (int k = 0; k < prev.Length; k++) DrawChar(g, prev[k], was[k], outY, (int)(a * outA));
                for (int k = 0; k < cur.Length; k++) DrawChar(g, cur[k], now[k], inY, (int)(a * inA));
            }
        }
        g.SetClip(clip);
    }

    // The roll: the old digit drifts up and fades out early, the new one rises in a beat later and settles
    // softly (ease in-out), so the two never smear into each other.
    (float Move, float OutY, float OutA, float InY, float InA) Roll(float t)
    {
        float move = Motion.EaseInOut(Motion.Clamp01(t));
        float outA = 1 - Motion.Smooth(Motion.Clamp01(t / 0.55));
        float inA = Motion.Smooth(Motion.Clamp01((t - 0.18) / 0.6));
        return (move, -shift * move, outA, shift * (1 - move), inA);
    }

    // Left edge of each character of `text` in column `col`, with natural (proportional) spacing: centred in
    // the column, or right-aligned for the clock's hour so its colon never moves.
    float[] Lefts(int col, string text)
    {
        var lefts = new float[text.Length];
        float total = 0;
        for (int k = 0; k < text.Length; k++) total += Glyph(text[k]).Adv;
        float x = col == 0 && firstRight ? colX[0] + colW - total : colX[col] + (colW - total) / 2;
        for (int k = 0; k < text.Length; k++)
        {
            lefts[k] = x;
            x += Glyph(text[k]).Adv;
        }
        return lefts;
    }

    (GraphicsPath Path, float Adv) Glyph(char c)
    {
        string key = c.ToString();
        if (!cache.TryGetValue(key, out var entry))
        {
            float adv = Art.Advance(key, Style.Bold, px, fmt);
            var path = new GraphicsPath(FillMode.Winding);
            path.AddString(key, Style.Bold, (int)FontStyle.Regular, px, PointF.Empty, fmt);
            entry = (path, adv);
            cache[key] = entry;
        }
        return entry;
    }

    void DrawChar(Graphics g, char c, float slotLeft, float dy, int alpha)
    {
        if (alpha <= 0) return;
        var (path, _) = Glyph(c);
        float x = slotLeft;
        using var p = (GraphicsPath)path.Clone();
        using (var m = new Matrix())
        {
            m.Translate(x, originY + dy + 3 * s);
            p.Transform(m);
        }
        using (var shadow = new SolidBrush(Color.FromArgb(alpha * 70 / 255, 0, 0, 0))) g.FillPath(shadow, p);
        using (var up = new Matrix())
        {
            up.Translate(0, -3 * s);
            p.Transform(up);
        }
        using var ink = new SolidBrush(Color.FromArgb(alpha, Style.Ink));
        g.FillPath(ink, p);
    }

    public void Dispose()
    {
        foreach (var e in cache.Values) e.Path.Dispose();
        colon.Dispose();
    }
}

sealed class PanelArt : IDisposable
{
    public readonly float S;
    public readonly int W, H, Radius;
    public readonly Rectangle Full;
    public readonly Rectangle DotsRect;

    readonly StringFormat fmt = Art.Typographic();
    readonly Bitmap glass, edgeHover;
    readonly DigitRow countdownRow;
    DigitRow clockRow, nextUpRow, homeRow;
    bool clockRowHasSuffix, homeRowHasSuffix;
    Bitmap content, blend;
    string contentKey;
    float blendT = -1;
    Bitmap blendFrom;
    readonly float dotY, dotSpacing;

    // Now Playing: the cover on the left. Beside it, the label, song and artist, then one transport row
    // lined up with the text: previous / play-pause / next, then the elapsed time, the bar and the time left.
    readonly RectangleF coverRect;
    readonly RectangleF barShort, barLong;   // songs under an hour / an hour or longer (wider time slots)
    readonly float colL, colR, rowY, buttonCell, timeGap, timeCapTop;
    readonly PointF[] buttonCentres = new PointF[3];
    public readonly Rectangle ProgressRect, ControlsRect;
    public Rectangle UpdateRect => Rectangle.FromLTRB((int)(W - 262 * S), (int)(10 * S), (int)Math.Ceiling(W - 14 * S), (int)Math.Ceiling(38 * S));
    const float RingPrevNext = 28, RingPlay = 34, RowHalf = 40; // ring radii fit inside the row's redraw area

    public PanelArt(float s, int radius)
    {
        S = s;
        Radius = radius;
        W = (int)Math.Round(1120 * s);
        H = (int)Math.Round(360 * s);
        Full = new Rectangle(0, 0, W, H);
        glass = Art.Glass(W, H, radius, 72, 20);
        edgeHover = Art.Edge(W, H, radius, 67, 17); // over the glass's own edge: about 120 / 36 at full hover
        countdownRow = new DigitRow(s, W, 4, 210 * s, 56 * s, 146 * s, 92 * s, 0, false, fmt);
        dotY = 332 * s;
        dotSpacing = 22 * s;
        DotsRect = Rectangle.FromLTRB((int)(W / 2 - 160 * s), (int)(dotY - 16 * s), (int)(W / 2 + 160 * s), (int)Math.Min(H, dotY + 16 * s));

        coverRect = new RectangleF((float)Math.Round(56 * s), (float)Math.Round(52 * s), (float)Math.Round(248 * s), (float)Math.Round(248 * s));
        colL = 344 * s;
        colR = W - 56 * s;
        rowY = 252 * s;

        // buttons: the previous icon's left edge lines up with the text; each owns an 80-unit cell
        buttonCell = 80 * s;
        float first = colL + 15 * s;
        for (int i = 0; i < 3; i++) buttonCentres[i] = new PointF(first + i * buttonCell, rowY);
        ControlsRect = Rectangle.FromLTRB((int)Math.Floor(first - buttonCell / 2), (int)Math.Floor(rowY - RowHalf * s),
            (int)Math.Ceiling(first + 2.5f * buttonCell), (int)Math.Ceiling(rowY + RowHalf * s));

        // times hug the bar: elapsed right-aligned before it, time left after it, in slots wide enough for any value
        float tpx = 20 * s, track = 2 * s, h = Math.Max(3f, 6 * s);
        timeGap = 16 * s;
        timeCapTop = rowY - Art.InkBounds("0", Style.Text, tpx, fmt).Height / 2;
        float timesL = ControlsRect.Right + 22 * s;
        using (var mb = new Bitmap(1, 1))
        using (Graphics mg = Graphics.FromImage(mb))
        {
            RectangleF Bar(string elapsed, string left)
            {
                float l = timesL + Art.Measure(mg, elapsed, Style.Text, tpx, track, fmt) + timeGap;
                float r = colR - Art.Measure(mg, left, Style.Text, tpx, track, fmt) - timeGap;
                return new RectangleF(l, rowY - h / 2, r - l, h);
            }
            barShort = Bar("00:00", "-00:00");
            barLong = Bar("0:00:00", "-0:00:00");
        }
        ProgressRect = Rectangle.FromLTRB((int)Math.Floor(timesL - 6 * s), (int)Math.Floor(rowY - 18 * s), (int)Math.Ceiling(colR + 6 * s), (int)Math.Ceiling(rowY + 18 * s));
    }

    // Everything the live layer draws must sit inside the area redrawn for it, or it gets cut off
    // (the clipped play ring, 2026-11-16). Returns the problems found, or an empty list.
    public List<string> LayoutProblems()
    {
        var problems = new List<string>();
        RectangleF controls = ControlsRect, progress = ProgressRect, dots = DotsRect;
        for (int i = 0; i < 3; i++)
        {
            float r = (i == 1 ? RingPlay : RingPrevNext) * S;
            var ring = new RectangleF(buttonCentres[i].X - r, buttonCentres[i].Y - r, 2 * r, 2 * r);
            if (!controls.Contains(ring)) problems.Add("button " + i + " ring outside its redraw area");
        }
        using (var mb = new Bitmap(1, 1))
        using (Graphics mg = Graphics.FromImage(mb))
        {
            foreach (RectangleF bar in new[] { barShort, barLong })
            {
                float ew = Art.Measure(mg, bar == barLong ? "0:00:00" : "00:00", Style.Text, 20 * S, 2 * S, fmt);
                float lw = Art.Measure(mg, bar == barLong ? "-0:00:00" : "-00:00", Style.Text, 20 * S, 2 * S, fmt);
                var span = RectangleF.FromLTRB(bar.X - timeGap - ew, Math.Min(bar.Y, timeCapTop), bar.Right + timeGap + lw, Math.Max(bar.Bottom, rowY + (rowY - timeCapTop)));
                if (!progress.Contains(span)) problems.Add("bar and times outside their redraw area");
                if (bar.Width < 200 * S) problems.Add("bar too short");
            }
        }
        if (controls.IntersectsWith(progress)) problems.Add("buttons and bar overlap");
        if (controls.IntersectsWith(dots) || progress.IntersectsWith(dots)) problems.Add("transport row overlaps the dots");
        if (controls.Bottom > coverRect.Bottom) problems.Add("transport row lower than the cover");
        return problems;
    }

    RectangleF BarFor(NowPlayingInfo m) => m != null && m.Duration.TotalHours >= 1 ? barLong : barShort;

    // Which media button (0 previous, 1 play/pause, 2 next) a click at panel coordinates hits.
    // The whole row is split between the three, so a near miss never switches the view instead.
    public int ButtonAt(int x, int y)
    {
        if (!ControlsRect.Contains(x, y)) return -1;
        return Math.Clamp((int)Math.Floor((x - ControlsRect.X) / buttonCell), 0, 2);
    }

    public int FillWidth(float progress, NowPlayingInfo m) => progress < 0 ? -1 : (int)Math.Round(progress * BarFor(m).Width);

    DigitRow ClockRow(bool suffix)
    {
        if (clockRow != null && clockRowHasSuffix == suffix) return clockRow;
        clockRow?.Dispose();
        float shift = suffix ? -(Art.Advance("PM", Style.Text, 36 * S, fmt) + 16 * S) / 2 : 0;
        float colW = Art.Advance("00", Style.Bold, 146 * S, fmt) + 6 * S; // columns as wide as two digits, so colons sit evenly
        clockRow = new DigitRow(S, W, 3, colW, 62 * S, 146 * S, 104 * S, shift, true, fmt);
        clockRowHasSuffix = suffix;
        return clockRow;
    }

    // Home: hours and minutes, centred in the left part of the panel (beside the list of lines).
    const float HomeLeftCentre = 266, HomeDivider = 504, HomeLabelX = 536, HomeTextX = 664, HomeDigitPx = 128, HomeDateTop = 105, HomeDigitTop = 148;

    DigitRow HomeRow(bool suffix)
    {
        if (homeRow != null && homeRowHasSuffix == suffix) return homeRow;
        homeRow?.Dispose();
        float colW = Art.Advance("00", Style.Bold, HomeDigitPx * S, fmt) + 6 * S, sep = 46 * S;
        float rowW = colW * 2 + sep, extra = suffix ? Art.Advance("PM", Style.Text, 30 * S, fmt) + 12 * S : 0;
        float x0 = HomeLeftCentre * S - (rowW + extra) / 2;
        homeRow = new DigitRow(S, W, 2, colW, sep, HomeDigitPx * S, HomeDigitTop * S, x0 - (W - rowW) / 2, true, fmt);
        homeRowHasSuffix = suffix;
        return homeRow;
    }

    float HomeLineY(int k, int n) => (172 - (n - 1) * 24 + 48 * k) * S;

    void DrawHome(Graphics g, ViewModel vm)
    {
        float s = S;
        DigitRow row = HomeRow(vm.Suffix != null);
        float blockL = row.ColRects[0].Left + 4 * s, blockR = vm.Suffix != null ? row.RowRight + 12 * s + Art.Advance(vm.Suffix, Style.Text, 30 * s, fmt) : row.RowRight;
        Art.Text(g, vm.Title, Style.Bold, 22 * s, 6 * s, Style.Accent, (blockL + blockR) / 2, HomeDateTop * s, fmt, true);
        row.DrawColons(g);
        if (vm.Suffix != null)
            Art.TextLeft(g, vm.Suffix, Style.Text, 30 * s, 2.5f * s, Style.Muted, row.RowRight + 12 * s, row.DigitTop + 2 * s, fmt);
        using (var div = new Pen(Color.FromArgb(26, 255, 255, 255), Math.Max(1f, s)))
            g.DrawLine(div, HomeDivider * s, 70 * s, HomeDivider * s, 278 * s);

        int n = vm.Lines?.Count ?? 0;
        if (n == 0)
        {
            Art.TextLeft(g, "All quiet", Style.Text, 26 * s, 0.5f * s, Style.Faint, HomeLabelX * s, 172 * s - Cap(Style.Text, 26 * s) / 2, fmt);
            return;
        }
        float capLabel = Cap(Style.Bold, 14 * s), capText = Cap(Style.Text, 22 * s), textMax = W - 56 * s - HomeTextX * s;
        for (int k = 0; k < n; k++)
        {
            HomeLine line = vm.Lines[k];
            float cy = HomeLineY(k, n);
            Art.TextLeft(g, line.Label, Style.Bold, 14 * s, 2.5f * s, line.Alert ? Style.Accent : Style.Faint, HomeLabelX * s, cy - capLabel / 2, fmt);
            FontFamily tf = Style.For(line.Text, false);
            Art.TextLeft(g, Art.Fit(g, line.Text, tf, 22 * s, 0.4f * s, textMax, fmt), tf, 22 * s, 0.4f * s, Style.Ink, HomeTextX * s, cy - capText / 2, fmt);
        }
    }

    public DigitRow RowFor(ViewModel vm)
    {
        if (vm == null || vm.Values == null) return null;
        return vm.Kind switch
        {
            ViewKind.Home => HomeRow(vm.Suffix != null),
            ViewKind.Clock => ClockRow(vm.Suffix != null),
            ViewKind.NextUp => nextUpRow ??= new DigitRow(S, W, 2, Art.Advance("00", Style.Bold, 112 * S, fmt) + 6 * S, 64 * S, 112 * S, 134 * S, 0, false, fmt),
            _ => countdownRow,
        };
    }

    float Cap(FontFamily family, float px) => Art.InkBounds("H", family, px, fmt).Height;

    // ---- Next up: the event's name, a rolling mm:ss to its start, its time and place.
    void DrawNextUp(Graphics g, ViewModel vm)
    {
        float s = S, cx = W / 2f;
        Art.Text(g, vm.Title, Style.Bold, 26 * s, 8.5f * s, Style.Accent, cx, 40 * s, fmt, true);
        FontFamily nameFont = Style.For(vm.Detail, true);
        Art.Text(g, Art.Fit(g, vm.Detail, nameFont, 44 * s, 0.5f * s, W - 180 * s, fmt), nameFont, 44 * s, 0.5f * s, Style.Ink, cx, 80 * s, fmt, true);
        DigitRow row = RowFor(vm);
        if (row == null)
        {
            Art.Text(g, vm.Big, Style.Bold, 104 * s, 9 * s, Style.Ink, cx, 148 * s, fmt, false); // after the start: "STARTED"
        }
        else
        {
            row.DrawColons(g);
            for (int i = 0; i < vm.Labels.Length; i++)
            {
                Rectangle c = row.ColRects[i];
                Art.Text(g, vm.Labels[i], Style.Text, 20 * s, 4.5f * s, Style.Muted, c.X + c.Width / 2f, 234 * s, fmt, false);
            }
        }
        FontFamily footFont = Style.For(vm.Footer, false);
        // with a place to go, the footer reads a little brighter: it's a link to directions
        Art.Text(g, Art.Fit(g, vm.Footer, footFont, 20 * s, 4 * s, W - 200 * s, fmt), footFont, 20 * s, 4 * s, vm.MapsUrl != null ? Style.Muted : Style.Faint, cx, 280 * s, fmt, false);
        if (vm.JoinUrl != null)
        {
            Rectangle j = JoinRect(g);
            DrawPill(g, j, "JOIN MEETING", true);
        }
    }

    // ---- Agenda: "TODAY", a summary and "+", then up to four lines (time, marker, title, note). A ticked task
    // shows struck through with an UNDO pill for 5 seconds; an event can open a detail card over the list.
    float AgLeft => 72 * S;
    float AgRight => W - 72 * S;
    float AgRowY(int k) => (112 + 52 * k) * S;
    const float AgTimeRight = 204, AgMarkX = 228, AgTitleLeft = 252;

    Rectangle AddButtonRect => Rectangle.FromLTRB((int)(AgRight - 32 * S), (int)(34 * S), (int)Math.Ceiling(AgRight + 2 * S), (int)Math.Ceiling(68 * S));

    Rectangle UndoRect(int k)
    {
        float cy = AgRowY(k);
        return Rectangle.FromLTRB((int)(AgRight - 104 * S), (int)(cy - 17 * S), (int)Math.Ceiling(AgRight), (int)Math.Ceiling(cy + 17 * S));
    }

    RectangleF CardRect => RectangleF.FromLTRB(AgLeft - 8 * S, 80 * S, AgRight + 8 * S, 304 * S);

    // The card's buttons, right to left: CLOSE, then JOIN and DIRECTIONS when the event has them.
    List<(HitKind Kind, Rectangle Rect, string Label)> CardButtons(Graphics g, AgendaCard c)
    {
        var list = new List<(HitKind, Rectangle, string)>();
        float x = CardRect.Right - 24 * S, y = 252 * S, h = 36 * S;
        void Add(HitKind kind, string label)
        {
            float w = Art.Measure(g, label, Style.Bold, 17 * S, 2.5f * S, fmt) + 40 * S;
            var r = Rectangle.FromLTRB((int)(x - w), (int)y, (int)Math.Ceiling(x), (int)Math.Ceiling(y + h));
            list.Add((kind, r, label));
            x -= w + 12 * S;
        }
        Add(HitKind.CardClose, "CLOSE");
        if (c.JoinUrl != null) Add(HitKind.CardJoin, "JOIN");
        if (c.MapsUrl != null) Add(HitKind.CardDirections, "DIRECTIONS");
        return list;
    }

    void DrawPill(Graphics g, Rectangle r, string label, bool primary)
    {
        using GraphicsPath p = Art.Rounded(r, r.Height / 2f);
        using (var fill = new SolidBrush(primary ? Color.FromArgb(225, Style.Accent) : Color.FromArgb(34, 255, 255, 255))) g.FillPath(fill, p);
        if (!primary) using (var edge = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, S))) g.DrawPath(edge, p);
        Art.Text(g, label, Style.Bold, 17 * S, 2.5f * S, Style.Ink, r.X + r.Width / 2f, r.Y + (r.Height - Cap(Style.Bold, 17 * S)) / 2, fmt, false);
    }

    void DrawAgenda(Graphics g, ViewModel vm)
    {
        float s = S, cx = W / 2f, left = AgLeft, right = AgRight;
        Art.TextLeft(g, vm.Title, Style.Bold, 24 * s, 7 * s, Style.Accent, left, 44 * s, fmt, true);
        float noteRight = right;
        if (vm.CanAdd)
        {
            Rectangle a = AddButtonRect;
            float r = a.Height / 2f, acx = a.X + a.Width / 2f, acy = a.Y + a.Height / 2f;
            using (var ring = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1.4f, 1.8f * s))) g.DrawEllipse(ring, acx - r + s, acy - r + s, 2 * r - 2 * s, 2 * r - 2 * s);
            using (var plus = new Pen(Style.Ink, 2.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(plus, acx - 7 * s, acy, acx + 7 * s, acy);
                g.DrawLine(plus, acx, acy - 7 * s, acx, acy + 7 * s);
            }
            noteRight = a.X - 16 * s;
        }
        if (!string.IsNullOrEmpty(vm.Note))
        {
            string note = Art.Fit(g, vm.Note, Style.Text, 17 * s, 3 * s, noteRight - left - 220 * s, fmt);
            Art.TextRight(g, note, Style.Text, 17 * s, 3 * s, Style.Faint, noteRight, 44 * s + (Cap(Style.Bold, 24 * s) - Cap(Style.Text, 17 * s)) / 2, fmt);
        }

        // An open card replaces the list (drawing it over the rows left them showing through the tint).
        if (vm.Card != null) { DrawCard(g, vm.Card); return; }

        if (vm.Rows.Count == 0)
        {
            Art.Text(g, vm.Detail, Style.Text, 34 * s, 1 * s, Style.Muted, cx, 128 * s, fmt, false);
            if (vm.Footer != null)
            {
                FontFamily ff = Style.For(vm.Footer, false);
                Art.Text(g, Art.Fit(g, vm.Footer, ff, 20 * s, 3 * s, W - 200 * s, fmt), ff, 20 * s, 3 * s, Style.Faint, cx, 188 * s, fmt, false);
            }
            if (vm.Suffix != null) Art.Text(g, vm.Suffix, Style.Text, 18 * s, 2 * s, Style.Faint, cx, 262 * s, fmt, false);
            return;
        }

        float timeRight = AgTimeRight * s, markX = AgMarkX * s, titleLeft = AgTitleLeft * s;
        float capTime = Cap(Style.Text, 22 * s), capTitle = Cap(Style.Text, 28 * s), capNote = Cap(Style.Text, 17 * s);
        using var line = new Pen(Color.FromArgb(18, 255, 255, 255), Math.Max(1f, s));
        for (int k = 0; k < vm.Rows.Count; k++)
        {
            AgendaRow row = vm.Rows[k];
            float cy = AgRowY(k);
            if (k > 0) g.DrawLine(line, titleLeft, cy - 26 * s, right, cy - 26 * s);
            if (row.More)
            {
                Art.TextLeft(g, row.Title, Style.Text, 21 * s, 2 * s, Style.Faint, titleLeft, cy - Cap(Style.Text, 21 * s) / 2, fmt);
                continue;
            }
            if (!string.IsNullOrEmpty(row.Time))
                Art.TextRight(g, row.Time, Style.Text, 22 * s, 1.5f * s, row.Alert ? Style.Accent : Style.Muted, timeRight, cy - capTime / 2, fmt);
            if (row.Marker == RowMarker.Bar)
            {
                using GraphicsPath bar = Art.Rounded(new RectangleF(markX - 2.5f * s, cy - 13 * s, 5 * s, 26 * s), 2.5f * s);
                using var bb = new SolidBrush(row.MarkerColor);
                g.FillPath(bb, bar);
            }
            else if (row.Marker == RowMarker.Ring)
            {
                float r = 8.5f * s;
                if (row.Completing)
                {
                    // ticked: a filled ring with a check
                    using (var fill = new SolidBrush(row.MarkerColor)) g.FillEllipse(fill, markX - r - s, cy - r - s, 2 * r + 2 * s, 2 * r + 2 * s);
                    using var check = new Pen(Color.FromArgb(240, 10, 14, 20), 2.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawLines(check, new[] { new PointF(markX - 4.5f * s, cy), new PointF(markX - 1 * s, cy + 3.5f * s), new PointF(markX + 5 * s, cy - 4 * s) });
                }
                else
                {
                    using var pen = new Pen(row.MarkerColor, 2.2f * s);
                    g.DrawEllipse(pen, markX - r, cy - r, 2 * r, 2 * r);
                }
            }
            float titleRight = right;
            if (row.Completing)
            {
                Rectangle u = UndoRect(k);
                DrawPill(g, u, "UNDO", false);
                titleRight = u.X - 20 * s;
            }
            else if (!string.IsNullOrWhiteSpace(row.Right))
            {
                string note = Art.Fit(g, row.Right.ToUpperInvariant(), Style.For(row.Right, false), 17 * s, 2.5f * s, 240 * s, fmt);
                FontFamily nf = Style.For(note, false);
                Art.TextRight(g, note, nf, 17 * s, 2.5f * s, Style.Faint, right, cy - capNote / 2, fmt);
                titleRight = right - Art.Measure(g, note, nf, 17 * s, 2.5f * s, fmt) - 28 * s;
            }
            FontFamily tf = Style.For(row.Title, false);
            string title = Art.Fit(g, row.Title, tf, 28 * s, 0.3f * s, titleRight - titleLeft, fmt);
            Art.TextLeft(g, title, tf, 28 * s, 0.3f * s, row.Completing ? Style.Faint : Style.Ink, titleLeft, cy - capTitle / 2, fmt);
            if (row.Completing)
            {
                float w = Art.Measure(g, title, tf, 28 * s, 0.3f * s, fmt);
                using var strike = new Pen(Color.FromArgb(170, 255, 255, 255), Math.Max(1.4f, 2 * s));
                g.DrawLine(strike, titleLeft, cy, titleLeft + w, cy);
            }
        }
    }

    // An event's detail card, over the list: name, time, place, a line of notes, and its buttons.
    void DrawCard(Graphics g, AgendaCard c)
    {
        float s = S;
        RectangleF r = CardRect;
        using (GraphicsPath p = Art.Rounded(r, 16 * s))
        {
            using (var fill = new SolidBrush(Color.FromArgb(16, 255, 255, 255))) g.FillPath(fill, p);
            using (var edge = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(1f, s))) g.DrawPath(edge, p);
        }
        float x = r.X + 28 * s, width = r.Width - 56 * s;
        FontFamily tf = Style.For(c.Title, true);
        Art.TextLeft(g, Art.Fit(g, c.Title, tf, 32 * s, 0.4f * s, width, fmt), tf, 32 * s, 0.4f * s, Style.Ink, x, 104 * s, fmt, true);
        Art.TextLeft(g, c.When.ToUpperInvariant(), Style.Text, 20 * s, 3 * s, Style.Accent, x, 152 * s, fmt);
        if (!string.IsNullOrWhiteSpace(c.Where))
        {
            FontFamily wf = Style.For(c.Where, false);
            Art.TextLeft(g, Art.Fit(g, c.Where, wf, 22 * s, 0.5f * s, width, fmt), wf, 22 * s, 0.5f * s, Style.Muted, x, 184 * s, fmt);
        }
        if (!string.IsNullOrWhiteSpace(c.Notes))
        {
            FontFamily nf = Style.For(c.Notes, false);
            Art.TextLeft(g, Art.Fit(g, c.Notes, nf, 19 * s, 0.5f * s, width, fmt), nf, 19 * s, 0.5f * s, Style.Faint, x, 216 * s, fmt);
        }
        foreach (var (kind, rect, label) in CardButtons(g, c)) DrawPill(g, rect, label, kind == HitKind.CardJoin);
    }

    public bool EnsureContent(ViewModel vm)
    {
        bool changed = EnsureContent(vm, out Bitmap previous);
        previous?.Dispose();
        return changed;
    }

    // Redraws the view's static text (everything but the digits) when it changes. The old layer is
    // handed back (the caller owns it) so it can crossfade out.
    public bool EnsureContent(ViewModel vm, out Bitmap previous)
    {
        previous = null;
        string key = vm.StaticKey;
        if (key == contentKey && content != null) return false;
        contentKey = key;
        previous = content;
        content = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        float s = S, cx = W / 2f;
        using Graphics g = Art.Canvas(content);
        g.Clear(Color.Transparent);

        if (vm.Kind == ViewKind.NowPlaying)
        {
            DrawNowPlaying(g, vm);
        }
        else if (vm.Kind == ViewKind.Agenda)
        {
            DrawAgenda(g, vm);
        }
        else if (vm.Kind == ViewKind.NextUp)
        {
            DrawNextUp(g, vm);
        }
        else if (vm.Kind == ViewKind.Home)
        {
            DrawHome(g, vm);
        }
        else if (vm.Kind == ViewKind.Clock)
        {
            DigitRow row = RowFor(vm);
            Art.Text(g, vm.Title, Style.Bold, 26 * s, 8.5f * s, Style.Accent, cx, 52 * s, fmt, true);
            row.DrawColons(g);
            if (vm.Suffix != null)
                Art.TextLeft(g, vm.Suffix, Style.Text, 36 * s, 3 * s, Style.Muted, row.RowRight + 16 * s, row.DigitTop + 2 * s, fmt);
            if (vm.Footer != null)
                Art.Text(g, vm.Footer, Style.Text, 22 * s, 3.5f * s, Style.Muted, cx, 250 * s, fmt, false);
        }
        else
        {
            Art.Text(g, vm.Title, Style.Bold, 26 * s, 8.5f * s, Style.Accent, cx, 40 * s, fmt, true);
            if (vm.Big != null)
            {
                Art.Text(g, vm.Big, Style.Bold, 136 * s, 9 * s, Style.Ink, cx, 102 * s, fmt, false);
            }
            else
            {
                countdownRow.DrawColons(g);
                RectangleF[] cols = countdownRow.ColRects.Select(r => (RectangleF)r).ToArray();
                for (int i = 0; i < vm.Labels.Length; i++)
                    Art.Text(g, vm.Labels[i], Style.Text, 22 * s, 4.5f * s, Style.Muted, cols[i].X + cols[i].Width / 2, 222 * s, fmt, false);
            }
            Art.Divider(g, W, 262 * s, s);
            Art.Text(g, vm.Footer, Style.Text, 21 * s, 4.5f * s, vm.Big != null ? Style.Muted : Style.Faint, cx, 288 * s, fmt, false);
        }
        return true;
    }

    // Exactly what is on screen now minus the glass and dots (mid-transition too), so a new switch
    // can start from it without a jump.
    public Bitmap Snapshot(FrameState f)
    {
        var b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        using Graphics g = Art.Canvas(b);
        g.Clear(Color.Transparent);
        g.SetClip(Full);
        DrawLayers(g, Full, f);
        return b;
    }

    // The old layer (sliding out) and the current view (static text, digits, Now Playing's live parts) on top.
    void DrawLayers(Graphics g, Rectangle clip, FrameState f)
    {
        if (f.FadeFrom != null && f.OldAlpha > 0.004f)
        {
            GraphicsState os = g.Save();
            g.TranslateTransform(f.OldOffset.X, f.OldOffset.Y);
            Art.DrawFaded(g, f.FadeFrom, clip, f.OldAlpha);
            g.Restore(os);
        }
        float a = f.NewAlpha;
        if (a <= 0.004f) return;
        GraphicsState st = g.Save();
        if (f.NewOffset != PointF.Empty) g.TranslateTransform(f.NewOffset.X, f.NewOffset.Y);
        Art.DrawFaded(g, StaticLayer(f), clip, a);
        DigitRow row = RowFor(f.View);
        if (row != null)
            for (int i = 0; i < row.ColRects.Length; i++)
                if (row.ColRects[i].IntersectsWith(clip)) row.DrawColumn(g, clip, i, f.Cur[i], f.Prev[i], f.ColT[i], a);
        if (f.View?.Kind == ViewKind.NowPlaying && (f.NewOffset != PointF.Empty || ProgressRect.IntersectsWith(clip) || ControlsRect.IntersectsWith(clip)))
            DrawMediaLive(g, f, a);
        g.Restore(st);
    }

    // The static layer, or a true crossfade of the old and new one while words change in place.
    Bitmap StaticLayer(FrameState f)
    {
        if (f.ContentFrom == null || f.ContentT >= 1 || content == null) return content;
        if (blend == null) blend = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
        if (blendT != f.ContentT || !ReferenceEquals(blendFrom, f.ContentFrom))
        {
            Motion.Blend(f.ContentFrom, content, f.ContentT, blend);
            blendT = f.ContentT;
            blendFrom = f.ContentFrom;
        }
        return blend;
    }

    // ---- Now Playing, static layer: tint, cover, label, song, artist and the empty bar.
    void DrawNowPlaying(Graphics g, ViewModel vm)
    {
        NowPlayingInfo m = vm.Media;
        float s = S, width = colR - colL;
        if (m.Art?.Tint is Color tint)
        {
            float edge = Math.Max(1f, Radius / 8f);
            using GraphicsPath p = Art.Rounded(new RectangleF(edge, edge, W - 2 * edge, H - 2 * edge), Math.Max(1f, Radius - edge));
            using var b = new SolidBrush(Color.FromArgb(Style.TintAlpha, tint));
            g.FillPath(b, p);
        }
        DrawCover(g, m);

        Art.TextLeft(g, vm.Title, Style.Bold, 24 * s, 7 * s, Style.Accent, colL, 72 * s, fmt, true);
        FontFamily songFont = Style.For(m.Title, true);
        string song = Art.Fit(g, m.Title, songFont, 50 * s, 0.5f * s, width, fmt);
        Art.TextLeft(g, song, songFont, 50 * s, 0.5f * s, Style.Ink, colL, 106 * s, fmt, true);
        if (!string.IsNullOrWhiteSpace(m.Artist))
        {
            FontFamily artistFont = Style.For(m.Artist, false);
            Art.TextLeft(g, Art.Fit(g, m.Artist, artistFont, 28 * s, 1 * s, width, fmt), artistFont, 28 * s, 1 * s, Style.Muted, colL, 166 * s, fmt, false);
        }
        if (m.HasTimeline)
        {
            RectangleF bar = BarFor(m);
            using GraphicsPath track = Art.Rounded(bar, bar.Height / 2);
            using var tb = new SolidBrush(Color.FromArgb(46, 255, 255, 255));
            g.FillPath(tb, track);
        }
    }

    void DrawCover(Graphics g, NowPlayingInfo m)
    {
        RectangleF r = coverRect;
        float s = S, rad = 12 * s;
        for (int i = 3; i >= 1; i--) // soft shadow under the cover
        {
            float grow = i * 2.5f * s;
            using GraphicsPath sp = Art.Rounded(new RectangleF(r.X - grow, r.Y - grow + 6 * s, r.Width + 2 * grow, r.Height + 2 * grow), rad + grow);
            using var sb = new SolidBrush(Color.FromArgb(18, 0, 0, 0));
            g.FillPath(sb, sp);
        }
        using GraphicsPath shape = Art.Rounded(r, rad);
        if (m.Art != null)
        {
            int size = (int)r.Width;
            using Bitmap src = m.Art.ToBitmap();
            using var sized = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics sg = Graphics.FromImage(sized))
            using (var ia = new ImageAttributes())
            {
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                ia.SetWrapMode(WrapMode.TileFlipXY);
                if (!m.Playing) ia.SetColorMatrix(new ColorMatrix { Matrix33 = 0.55f }); // paused: the cover dims
                sg.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            using var tex = new TextureBrush(sized, WrapMode.Clamp);
            tex.TranslateTransform(r.X, r.Y);
            g.FillPath(tex, shape); // anti-aliased rounded corners
        }
        else
        {
            using var empty = new SolidBrush(Color.FromArgb(26, 255, 255, 255));
            g.FillPath(empty, shape);
            Art.Text(g, "♪", Style.Symbol, 96 * s, 0, Style.Faint, r.X + r.Width / 2, r.Y + r.Height * 0.3f, fmt, false);
        }
        using var edge = new Pen(Color.FromArgb(40, 255, 255, 255), Math.Max(1f, s));
        g.DrawPath(edge, shape);
    }

    // ---- Now Playing, live layer: the bar's fill, the times and the buttons.
    void DrawMediaLive(Graphics g, FrameState f, float alpha)
    {
        NowPlayingInfo m = f.View.Media;
        if (m == null || alpha <= 0.004f) return;
        float s = S;
        int a255 = (int)(255 * alpha);
        if (f.Progress >= 0)
        {
            RectangleF bar = BarFor(m);
            float fill = FillWidth(f.Progress, m);
            if (fill > 0)
            {
                var fr = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, fill), bar.Height);
                using GraphicsPath fp = Art.Rounded(fr, bar.Height / 2);
                using var fb = new SolidBrush(Color.FromArgb(a255, Style.Accent));
                g.FillPath(fb, fp);
            }
            Color times = Color.FromArgb((int)(Style.Faint.A * alpha), Style.Faint);
            Art.TextRight(g, f.Elapsed, Style.Text, 20 * s, 2 * s, times, bar.X - timeGap, timeCapTop, fmt);
            Art.TextLeft(g, f.Left, Style.Text, 20 * s, 2 * s, times, bar.Right + timeGap, timeCapTop, fmt, false);
        }
        for (int i = 0; i < 3; i++)
        {
            var b = (MediaButton)i;
            bool enabled = m.Can(b);
            PointF c = buttonCentres[i];
            float ring = enabled ? f.Ring[i] : 0, press = enabled ? f.Press[i] : 0;
            float show = Math.Max(Motion.EaseOut(ring), press);
            if (show > 0.01f)
            {
                // the ring grows into place on hover and flashes a little brighter when clicked
                float hr = (i == 1 ? RingPlay : RingPrevNext) * s * (0.84f + 0.16f * show);
                int ra = (int)(Math.Max(34 * Motion.EaseOut(ring), 58 * press) * alpha);
                using var hb = new SolidBrush(Color.FromArgb(ra, 255, 255, 255));
                g.FillEllipse(hb, c.X - hr, c.Y - hr, hr * 2, hr * 2);
            }
            using GraphicsPath icon = b == MediaButton.PlayPause ? Motion.PlayPause(c, s, f.PlayMorph) : Art.MediaIcon(b, m.Playing, c, s);
            if (press > 0.01f)
            {
                float k = 1 - 0.14f * press; // the icon dips as if pressed
                using var mx = new Matrix();
                mx.Translate(c.X, c.Y);
                mx.Scale(k, k);
                mx.Translate(-c.X, -c.Y);
                icon.Transform(mx);
            }
            using var ib = new SolidBrush(Color.FromArgb(enabled ? a255 : (int)(70 * alpha), 255, 255, 255));
            g.FillPath(ib, icon);
            if (b == MediaButton.PlayPause)
            {
                using var soft = new Pen(ib.Color, 2.4f * s) { LineJoin = LineJoin.Round }; // rounds the morphing corners
                g.DrawPath(soft, icon);
            }
        }
    }

    public void Render(Graphics g, Rectangle clip, FrameState f)
    {
        g.SetClip(clip);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(glass, clip, clip, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        if (f.HoverT > 0.004f) Art.DrawFaded(g, edgeHover, clip, Motion.Smooth(f.HoverT));
        DrawLayers(g, clip, f);
        DrawInteractive(g, f);
        if (DotsRect.IntersectsWith(clip)) DrawDots(g, f);
        if (f.UpdateText != null && UpdateRect.IntersectsWith(clip)) DrawUpdatePill(g, f.UpdateText);
        g.ResetClip();
    }

    // A small pill in the top-right corner while a verified update waits: a red dot and "UPDATE READY · x.y.z".
    void DrawUpdatePill(Graphics g, string text)
    {
        RectangleF r = UpdateRect;
        float s = S, h = r.Height;
        float textW = Art.Measure(g, text, Style.Text, 15 * s, 2.5f * s, fmt);
        var pill = RectangleF.FromLTRB(r.Right - textW - 40 * s, r.Top, r.Right, r.Bottom);
        using (GraphicsPath p = Art.Rounded(pill, h / 2))
        using (var fill = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
        using (var edge = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, s)))
        {
            g.FillPath(fill, p);
            g.DrawPath(edge, p);
        }
        float dot = 7 * s;
        using (var dotBrush = new SolidBrush(Style.Accent)) g.FillEllipse(dotBrush, pill.X + 12 * s, pill.Y + (h - dot) / 2, dot, dot);
        Art.TextLeft(g, text, Style.Text, 15 * s, 2.5f * s, Style.Muted, pill.X + 26 * s, pill.Y + (h - Cap(Style.Text, 15 * s)) / 2, fmt);
    }

    // Faint dots for every view, and the current one as a pill that glides (and stretches) between them.
    void DrawDots(Graphics g, FrameState f)
    {
        int count = f.DotCount;
        if (count <= 1) return;
        float x0 = W / 2f - (count - 1) * dotSpacing / 2, r = 3f * S;
        using (var faint = new SolidBrush(Color.FromArgb(85, 255, 255, 255)))
            for (int i = 0; i < count; i++)
                g.FillEllipse(faint, x0 + i * dotSpacing - r, dotY - r, r * 2, r * 2);
        float half = 9 * S / dotSpacing; // pill half-width, in dot units
        (float left, float right) = Motion.Worm(f.DotFrom, f.DotTo, f.DotT, half);
        float h = 7 * S;
        var pill = RectangleF.FromLTRB(x0 + left * dotSpacing, dotY - h / 2, x0 + right * dotSpacing, dotY + h / 2);
        using GraphicsPath pp = Art.Rounded(pill, h / 2);
        using var pb = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        g.FillPath(pb, pp);
    }

    // ---- What's under the pointer (1.0): everything clickable on the current view, most specific first.

    public Rectangle HintRect => Rectangle.FromLTRB((int)(14 * S), (int)(10 * S), (int)Math.Ceiling(W * 0.62f), (int)Math.Ceiling(38 * S));

    Rectangle JoinRect(Graphics g) =>
        Rectangle.FromLTRB((int)(14 * S), (int)(10 * S), (int)Math.Ceiling(14 * S + Art.Measure(g, "JOIN MEETING", Style.Bold, 15 * S, 2.5f * S, fmt) + 44 * S), (int)Math.Ceiling(40 * S));

    Rectangle TextBounds(Graphics g, string text, FontFamily family, float px, float tracking, float centreX, float capTop, float maxW = float.MaxValue)
    {
        float w = Math.Min(maxW, Art.Measure(g, text, family, px, tracking, fmt));
        float cap = Cap(family, px);
        return Rectangle.FromLTRB((int)(centreX - w / 2 - 14 * S), (int)(capTop - 10 * S), (int)Math.Ceiling(centreX + w / 2 + 14 * S), (int)Math.Ceiling(capTop + cap + 10 * S));
    }

    public Hit HitTest(FrameState f, int x, int y)
    {
        foreach (Hit h in Hits(f))
            if (h.Rect.Contains(x, y)) return h;
        return Hit.Nothing;
    }

    public List<Hit> Hits(FrameState f)
    {
        var list = new List<Hit>();
        float s = S;
        using var mb = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(mb);
        ViewModel vm = f.View;
        if (f.UpdateText != null) list.Add(new Hit(HitKind.Update, UpdateRect));
        if (vm?.Kind == ViewKind.NextUp && vm.JoinUrl != null) list.Add(new Hit(HitKind.NextJoin, JoinRect(g)));
        else if (f.HintText != null) list.Add(new Hit(HitKind.Hint, HintRect));
        if (f.DotCount > 1)
        {
            float x0 = W / 2f - (f.DotCount - 1) * dotSpacing / 2;
            for (int i = 0; i < f.DotCount; i++)
                list.Add(new Hit(HitKind.Dot, Rectangle.FromLTRB((int)(x0 + i * dotSpacing - dotSpacing / 2), (int)(dotY - 16 * s), (int)Math.Ceiling(x0 + i * dotSpacing + dotSpacing / 2), (int)Math.Ceiling(dotY + 16 * s)), i));
        }
        list.Add(new Hit(HitKind.EdgePrev, Rectangle.FromLTRB(0, (int)(44 * s), (int)(50 * s), (int)(H - 44 * s))));
        list.Add(new Hit(HitKind.EdgeNext, Rectangle.FromLTRB((int)(W - 50 * s), (int)(44 * s), W, (int)(H - 44 * s))));
        if (vm == null) return list;

        switch (vm.Kind)
        {
            case ViewKind.NowPlaying:
                for (int i = 0; i < 3; i++)
                    list.Add(new Hit(HitKind.Media, Rectangle.FromLTRB((int)(ControlsRect.X + i * buttonCell), ControlsRect.Y, (int)Math.Ceiling(ControlsRect.X + (i + 1) * buttonCell), ControlsRect.Bottom), i));
                if (vm.Media != null && vm.Media.HasTimeline && vm.Media.CanSeek)
                {
                    RectangleF bar = BarFor(vm.Media);
                    list.Add(new Hit(HitKind.Seek, Rectangle.FromLTRB((int)(bar.X - 4 * s), (int)(bar.Y - 14 * s), (int)Math.Ceiling(bar.Right + 4 * s), (int)Math.Ceiling(bar.Bottom + 14 * s))));
                }
                list.Add(new Hit(HitKind.Cover, Rectangle.Round(coverRect)));
                break;
            case ViewKind.Clock:
                list.Add(new Hit(HitKind.Date, TextBounds(g, vm.Title, Style.Bold, 26 * s, 8.5f * s, W / 2f, 52 * s)));
                if (vm.Footer != null) list.Add(new Hit(HitKind.Weather, TextBounds(g, vm.Footer, Style.Text, 22 * s, 3.5f * s, W / 2f, 250 * s)));
                break;
            case ViewKind.Agenda:
                if (vm.CanAdd) list.Add(new Hit(HitKind.AddTask, AddButtonRect));
                if (vm.Card != null)
                {
                    foreach (var (kind, rect, _) in CardButtons(g, vm.Card)) list.Add(new Hit(kind, rect));
                    break; // the list under the card isn't clickable while it's open
                }
                for (int k = 0; vm.Rows != null && k < vm.Rows.Count; k++)
                {
                    AgendaRow row = vm.Rows[k];
                    float cy = AgRowY(k);
                    var line = Rectangle.FromLTRB((int)(AgTitleLeft * s - 10 * s), (int)(cy - 24 * s), (int)Math.Ceiling(AgRight + 6 * s), (int)Math.Ceiling(cy + 24 * s));
                    if (row.More) { list.Add(new Hit(HitKind.More, line, k)); continue; }
                    if (row.Source == AgendaSource.Todoist)
                    {
                        if (row.Completing) { list.Add(new Hit(HitKind.Undo, UndoRect(k), k)); continue; }
                        list.Add(new Hit(HitKind.TaskDone, Rectangle.FromLTRB((int)(AgMarkX * s - 20 * s), (int)(cy - 22 * s), (int)Math.Ceiling(AgMarkX * s + 18 * s), (int)Math.Ceiling(cy + 22 * s)), k));
                        list.Add(new Hit(HitKind.TaskOpen, line, k));
                    }
                    else list.Add(new Hit(HitKind.EventOpen, line, k));
                }
                break;
            case ViewKind.NextUp:
                if (vm.MapsUrl != null && vm.Footer != null) list.Add(new Hit(HitKind.NextDirections, TextBounds(g, vm.Footer, Style.Text, 20 * s, 4 * s, W / 2f, 280 * s, W - 200 * s)));
                break;
            case ViewKind.Countdown:
                list.Add(new Hit(HitKind.Countdown, Rectangle.FromLTRB((int)(60 * s), (int)(80 * s), (int)(W - 60 * s), (int)(250 * s))));
                break;
            case ViewKind.Home:
                for (int k = 0; vm.Lines != null && k < vm.Lines.Count; k++)
                {
                    float cy = HomeLineY(k, vm.Lines.Count);
                    list.Add(new Hit(HitKind.HomeLine, Rectangle.FromLTRB((int)((HomeLabelX - 14) * s), (int)(cy - 22 * s), (int)Math.Ceiling(W - 48 * s), (int)Math.Ceiling(cy + 22 * s)), k));
                }
                break;
        }
        return list;
    }

    // Where along the progress bar (0..1) a click at x lands.
    public float SeekFraction(NowPlayingInfo m, int x)
    {
        RectangleF bar = BarFor(m);
        return Motion.Clamp01((x - bar.X) / Math.Max(1f, bar.Width));
    }

    // The hover layer: a soft plate behind hovered text, a tick preview on a task's ring, the ‹ › edge arrows,
    // and the first-run hint. Drawn over the view (and faded with it).
    void DrawInteractive(Graphics g, FrameState f)
    {
        float s = S;
        Hit h = f.HoverHit;
        if (h.Plate && f.HoverT > 0.01f)
        {
            // Plain (not gamma-correct) blending: with the panel's high-quality compositing a faint white over a
            // black wallpaper came out about four times brighter than its alpha.
            using GraphicsPath p = Art.Rounded(h.Rect, 8 * s);
            using var plate = new SolidBrush(Color.FromArgb((int)(16 * Motion.Smooth(f.HoverT)), 255, 255, 255));
            CompositingQuality q = g.CompositingQuality;
            g.CompositingQuality = CompositingQuality.AssumeLinear;
            g.FillPath(plate, p);
            g.CompositingQuality = q;
        }
        if (h.Kind == HitKind.TaskDone && f.View?.Rows != null && h.Index >= 0 && h.Index < f.View.Rows.Count)
        {
            float cy = AgRowY(h.Index), r = 8.5f * s, mx = AgMarkX * s;
            using var preview = new SolidBrush(Color.FromArgb(110, f.View.Rows[h.Index].MarkerColor));
            g.FillEllipse(preview, mx - r, cy - r, 2 * r, 2 * r);
        }
        if (f.HoverT > 0.01f)
        {
            float cy = H / 2f, half = 13 * s, depth = 7 * s;
            for (int side = 0; side < 2; side++)
            {
                bool over = (side == 0 && h.Kind == HitKind.EdgePrev) || (side == 1 && h.Kind == HitKind.EdgeNext);
                int a = (int)((over ? 235 : 95) * Motion.Smooth(f.HoverT));
                float cx = side == 0 ? 26 * s : W - 26 * s, dir = side == 0 ? 1 : -1;
                using var pen = new Pen(Color.FromArgb(a, 255, 255, 255), 3.2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, new[] { new PointF(cx + dir * depth / 2, cy - half), new PointF(cx - dir * depth / 2, cy), new PointF(cx + dir * depth / 2, cy + half) });
            }
        }
        bool join = f.View?.Kind == ViewKind.NextUp && f.View.JoinUrl != null;
        if (f.HintText != null && !join)
        {
            Rectangle r = HintRect;
            float textW = Art.Measure(g, f.HintText, Style.Text, 15 * s, 1.5f * s, fmt);
            var pill = RectangleF.FromLTRB(r.X, r.Y, Math.Min(r.Right, r.X + textW + 30 * s), r.Bottom);
            using (GraphicsPath p = Art.Rounded(pill, r.Height / 2f))
            using (var fill = new SolidBrush(Color.FromArgb(34, 255, 255, 255)))
                g.FillPath(fill, p);
            Art.TextLeft(g, f.HintText, Style.Text, 15 * s, 1.5f * s, Style.Muted, r.X + 15 * s, r.Y + (r.Height - Cap(Style.Text, 15 * s)) / 2, fmt);
        }
    }

    // Which dot (if any) a click at panel coordinates hits.
    public int DotAt(int x, int y, int count)
    {
        if (count <= 1 || Math.Abs(y - dotY) > 16 * S) return -1;
        float x0 = W / 2f - (count - 1) * dotSpacing / 2;
        int i = (int)Math.Round((x - x0) / dotSpacing);
        return i >= 0 && i < count && Math.Abs(x - (x0 + i * dotSpacing)) <= dotSpacing / 2 ? i : -1;
    }

    public void Dispose()
    {
        glass.Dispose();
        edgeHover.Dispose();
        content?.Dispose();
        blend?.Dispose();
        countdownRow.Dispose();
        clockRow?.Dispose();
        nextUpRow?.Dispose();
        homeRow?.Dispose();
        fmt.Dispose();
    }
}

static class Art
{
    public static Graphics Canvas(Bitmap b)
    {
        Graphics g = System.Drawing.Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        return g;
    }

    // Clear glass: one even tint over the live blur, and a fine light edge (brighter at the top)
    // that follows Windows' rounded corners.
    public static Bitmap Glass(int w, int h, int radius, int edgeTop, int edgeBottom)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using Graphics g = Canvas(b);
        g.Clear(Color.Transparent);
        using (var tint = new SolidBrush(Color.FromArgb(Style.GlassAlpha, 0, 3, 8))) g.FillRectangle(tint, 0, 0, w, h);
        DrawEdge(g, w, h, radius, edgeTop, edgeBottom);
        return b;
    }

    // Just the light edge, for brightening it on hover over the glass's own.
    public static Bitmap Edge(int w, int h, int radius, int edgeTop, int edgeBottom)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using Graphics g = Canvas(b);
        g.Clear(Color.Transparent);
        DrawEdge(g, w, h, radius, edgeTop, edgeBottom);
        return b;
    }

    static void DrawEdge(Graphics g, int w, int h, int radius, int edgeTop, int edgeBottom)
    {
        float edge = Math.Max(1f, radius / 8f);
        var er = new RectangleF(edge / 2, edge / 2, w - edge, h - edge);
        using GraphicsPath ep = Rounded(er, Math.Max(1f, radius - edge / 2));
        // gradient rect is bigger than the area: GDI+ wraps the last row otherwise
        using var eb = new LinearGradientBrush(new RectangleF(0, -2, w, h + 4), Color.FromArgb(edgeTop, 255, 255, 255), Color.FromArgb(edgeBottom, 255, 255, 255), 90f);
        using var pen = new Pen(eb, edge);
        g.DrawPath(pen, ep);
    }

    public static void DrawFaded(Graphics g, Bitmap b, Rectangle clip, float alpha)
    {
        if (b == null || alpha <= 0.004f) return;
        if (alpha >= 0.996f) { g.DrawImage(b, clip, clip, GraphicsUnit.Pixel); return; }
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
        g.DrawImage(b, clip, clip.X, clip.Y, clip.Width, clip.Height, GraphicsUnit.Pixel, ia);
    }

    public static void Divider(Graphics g, int w, float y, float s)
    {
        var r = new RectangleF(w * 0.3f, y, w * 0.4f, Math.Max(1f, 1.4f * s));
        using var b = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Color.Transparent, Color.Transparent, 0f);
        b.InterpolationColors = new ColorBlend
        {
            Colors = new[] { Color.FromArgb(0, Style.Accent), Color.FromArgb(170, Style.Accent), Color.FromArgb(0, Style.Accent) },
            Positions = new[] { 0f, 0.5f, 1f },
        };
        g.FillRectangle(b, r);
    }

    public static StringFormat Typographic()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    public static float Advance(string text, FontFamily family, float px, StringFormat fmt)
    {
        using var font = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel);
        using var b = new Bitmap(1, 1);
        using var g = System.Drawing.Graphics.FromImage(b);
        return g.MeasureString(text, font, PointF.Empty, fmt).Width;
    }

    public static RectangleF InkBounds(string text, FontFamily family, float px, StringFormat fmt)
    {
        using var p = new GraphicsPath(FillMode.Winding);
        p.AddString(text, family, (int)FontStyle.Regular, px, PointF.Empty, fmt);
        return p.GetBounds();
    }

    static GraphicsPath Spaced(Graphics g, string text, FontFamily family, float px, float tracking, StringFormat fmt, out float width)
    {
        var path = new GraphicsPath(FillMode.Winding);
        using var font = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel);
        float x = 0;
        foreach (char ch in text)
        {
            string one = ch.ToString();
            path.AddString(one, family, (int)FontStyle.Regular, px, new PointF(x, 0), fmt);
            x += g.MeasureString(one, font, PointF.Empty, fmt).Width + tracking;
        }
        width = Math.Max(0, x - tracking);
        return path;
    }

    // Letter-spaced text centred on centerX; capTop is where the top of the capitals lands.
    public static void Text(Graphics g, string text, FontFamily family, float px, float tracking, Color color, float centerX, float capTop, StringFormat fmt, bool halo)
    {
        if (string.IsNullOrEmpty(text)) return;
        using GraphicsPath path = Spaced(g, text, family, px, tracking, fmt, out float width);
        Place(g, path, centerX - width / 2, capTop, family, px, color, fmt, halo);
    }

    public static void TextLeft(Graphics g, string text, FontFamily family, float px, float tracking, Color color, float left, float capTop, StringFormat fmt, bool halo = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        using GraphicsPath path = Spaced(g, text, family, px, tracking, fmt, out _);
        Place(g, path, left, capTop, family, px, color, fmt, halo);
    }

    public static void TextRight(Graphics g, string text, FontFamily family, float px, float tracking, Color color, float right, float capTop, StringFormat fmt)
    {
        if (string.IsNullOrEmpty(text)) return;
        using GraphicsPath path = Spaced(g, text, family, px, tracking, fmt, out float width);
        Place(g, path, right - width, capTop, family, px, color, fmt, false);
    }

    public static float Measure(Graphics g, string text, FontFamily family, float px, float tracking, StringFormat fmt)
    {
        using var font = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel);
        float x = 0;
        foreach (char ch in text) x += g.MeasureString(ch.ToString(), font, PointF.Empty, fmt).Width + tracking;
        return Math.Max(0, x - tracking);
    }

    // The text, cut short with an ellipsis if it is wider than maxWidth.
    public static string Fit(Graphics g, string text, FontFamily family, float px, float tracking, float maxWidth, StringFormat fmt)
    {
        text = (text ?? "").Trim();
        if (Measure(g, text, family, px, tracking, fmt) <= maxWidth) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int n = (lo + hi + 1) / 2;
            if (Measure(g, text[..n].TrimEnd() + "…", family, px, tracking, fmt) <= maxWidth) lo = n; else hi = n - 1;
        }
        return text[..lo].TrimEnd() + "…";
    }

    // Previous / play-pause / next, drawn as filled shapes centred on c.
    public static GraphicsPath MediaIcon(MediaButton b, bool playing, PointF c, float s)
    {
        var p = new GraphicsPath(FillMode.Winding);
        if (b == MediaButton.PlayPause && playing)
        {
            float bw = 9 * s, bh = 30 * s, gap = 9 * s;
            p.AddPath(Rounded(new RectangleF(c.X - gap / 2 - bw, c.Y - bh / 2, bw, bh), 2.5f * s), false);
            p.AddPath(Rounded(new RectangleF(c.X + gap / 2, c.Y - bh / 2, bw, bh), 2.5f * s), false);
        }
        else if (b == MediaButton.PlayPause)
        {
            p.AddPolygon(new[] { new PointF(c.X - 10 * s, c.Y - 16 * s), new PointF(c.X + 16 * s, c.Y), new PointF(c.X - 10 * s, c.Y + 16 * s) });
        }
        else
        {
            float tw = 15 * s, th = 11 * s, dir = b == MediaButton.Next ? 1 : -1;
            for (int k = 0; k < 2; k++)
            {
                float x0 = c.X + dir * (k - 1) * tw; // two arrows pointing the same way, touching
                p.AddPolygon(new[] { new PointF(x0, c.Y - th), new PointF(x0 + dir * tw, c.Y), new PointF(x0, c.Y + th) });
            }
        }
        return p;
    }

    static void Place(Graphics g, GraphicsPath path, float left, float capTop, FontFamily family, float px, Color color, StringFormat fmt, bool halo)
    {
        RectangleF cap = InkBounds("H", family, px, fmt);
        using (var m = new Matrix())
        {
            m.Translate(left, capTop - cap.Top);
            path.Transform(m);
        }
        if (halo)
        {
            for (int w = 3; w >= 1; w--)
            {
                using var p = new Pen(Color.FromArgb(22, 0, 0, 0), w * 3 * px / 26f) { LineJoin = LineJoin.Round };
                g.DrawPath(p, path);
            }
        }
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
