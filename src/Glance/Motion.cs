// Motion: the timings, easing and shapes behind every animation. Clean movement only (no glows,
// gloss sweeps, grain or pulsing), and nothing runs while a game or video is full screen.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Glance;

static class Motion
{
    public const double SwitchMs = 560;   // view to view: slide + fade
    public const double SongMs = 600;     // new song or "OUT NOW": rise + fade
    public const double ContentMs = 260;  // same view, new words (weather, PAUSED, AM/PM): crossfade in place
    public const double HoverMs = 170;    // panel edge brightens
    public const double RingMs = 150;     // button hover ring
    public const double PressMs = 260;    // button dips when clicked
    public const double MorphMs = 240;    // play <-> pause
    public const double DotMs = 440;      // page indicator glides
    public const double RollMs = 420;     // digits roll

    public static float Clamp01(double t) => (float)Math.Clamp(t, 0, 1);
    public static float EaseOut(float t) { float u = 1 - t; return 1 - u * u * u * u; }                 // quick start, soft landing
    public static float EaseInOut(float t) => t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
    public static float Smooth(float t) => t * t * (3 - 2 * t);
    public static float Bump(float t) => (float)Math.Sin(Math.PI * Clamp01(t));                          // 0 -> 1 -> 0

    // Moves value toward target at a rate that covers 0..1 in durationMs.
    public static float Toward(float value, float target, double dtMs, double durationMs)
    {
        float step = (float)(dtMs / durationMs);
        return value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
    }

    // Where the old and new layers sit at time t (0..1) of a transition. Horizontal for a view switch
    // (dir +1: the new view comes from the right), vertical for a new song (it rises into place).
    // Staggered so the two never sit on top of each other as a double exposure: the old layer slips
    // away and is gone by 40%; the new one starts at 18% and does its whole glide after that.
    public static void Transition(FrameState f, float t, int dir, bool vertical, float s)
    {
        float d = (vertical ? 18 : 64) * s;
        float to = Clamp01(t / 0.4), tn = Clamp01((t - 0.18) / 0.82);
        float incoming = d * (1 - EaseOut(tn)), outgoing = -d * 0.45f * to * to; // old accelerates away
        if (vertical)
        {
            f.NewOffset = new PointF(0, incoming);
            f.OldOffset = new PointF(0, outgoing);
        }
        else
        {
            f.NewOffset = new PointF(dir * incoming, 0);
            f.OldOffset = new PointF(dir * outgoing, 0);
        }
        f.NewAlpha = Smooth(Clamp01(tn * 1.6));
        f.OldAlpha = 1 - Smooth(to);
    }

    public static void Rest(FrameState f)
    {
        f.NewOffset = f.OldOffset = PointF.Empty;
        f.NewAlpha = 1;
        f.OldAlpha = 0;
    }

    // The page indicator as a "worm": the leading edge races ahead, the trailing edge follows,
    // so the pill stretches mid-move and settles at the new dot. Positions are in dot indexes.
    public static (float Left, float Right) Worm(float from, float to, float t, float half)
    {
        t = Clamp01(t);
        float lead = from + (to - from) * EaseOut(t), trail = from + (to - from) * EaseInOut(t);
        return to >= from ? (trail - half, lead + half) : (lead - half, trail + half);
    }

    public static float DotCentre(float from, float to, float t) => from + (to - from) * EaseOut(Clamp01(t));

    // Play triangle (morph 0) <-> pause bars (morph 1): the triangle is cut into two quads that
    // straighten into the two bars.
    public static GraphicsPath PlayPause(PointF c, float s, float morph)
    {
        morph = Clamp01(morph);
        float x0 = c.X - 10 * s, x1 = c.X + 16 * s, xm = (x0 + x1) / 2, top = c.Y - 16 * s, bottom = c.Y + 16 * s;
        PointF[] playL = { new(x0, top), new(xm, c.Y - 8 * s), new(xm, c.Y + 8 * s), new(x0, bottom) };
        PointF[] playR = { new(xm, c.Y - 8 * s), new(x1, c.Y), new(x1, c.Y), new(xm, c.Y + 8 * s) };
        float bw = 9 * s, gap = 9 * s, pt = c.Y - 15 * s, pb = c.Y + 15 * s;
        float lx0 = c.X - gap / 2 - bw, lx1 = c.X - gap / 2, rx0 = c.X + gap / 2, rx1 = c.X + gap / 2 + bw;
        PointF[] pauseL = { new(lx0, pt), new(lx1, pt), new(lx1, pb), new(lx0, pb) };
        PointF[] pauseR = { new(rx0, pt), new(rx1, pt), new(rx1, pb), new(rx0, pb) };
        var p = new GraphicsPath(FillMode.Winding);
        p.AddPolygon(Lerp(playL, pauseL, morph));
        p.AddPolygon(Lerp(playR, pauseR, morph));
        return p;
    }

    static PointF[] Lerp(PointF[] a, PointF[] b, float t)
    {
        var r = new PointF[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = new PointF(a[i].X + (b[i].X - a[i].X) * t, a[i].Y + (b[i].Y - a[i].Y) * t);
        return r;
    }

    // dst = a*(1-t) + b*t per premultiplied channel: a true crossfade, so text that is the same in
    // both layers stays fully solid (layering two half-faded copies would dim it mid-fade).
    public static void Blend(Bitmap a, Bitmap b, float t, Bitmap dst)
    {
        var r = new Rectangle(0, 0, dst.Width, dst.Height);
        BitmapData da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        BitmapData db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        BitmapData dd = dst.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int w = r.Width, k = (int)Math.Round(Clamp01(t) * 256), ik = 256 - k;
            var ra = new int[w]; var rb = new int[w]; var rd = new int[w];
            for (int y = 0; y < r.Height; y++)
            {
                Marshal.Copy(da.Scan0 + y * da.Stride, ra, 0, w);
                Marshal.Copy(db.Scan0 + y * db.Stride, rb, 0, w);
                for (int x = 0; x < w; x++)
                {
                    int pa = ra[x], pb = rb[x];
                    if (pa == pb) { rd[x] = pa; continue; }
                    int c0 = (((pa >> 24) & 255) * ik + ((pb >> 24) & 255) * k) >> 8;
                    int c1 = (((pa >> 16) & 255) * ik + ((pb >> 16) & 255) * k) >> 8;
                    int c2 = (((pa >> 8) & 255) * ik + ((pb >> 8) & 255) * k) >> 8;
                    int c3 = ((pa & 255) * ik + (pb & 255) * k) >> 8;
                    rd[x] = (c0 << 24) | (c1 << 16) | (c2 << 8) | c3;
                }
                Marshal.Copy(rd, 0, dd.Scan0 + y * dd.Stride, w);
            }
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
            dst.UnlockBits(dd);
        }
    }
}
