// Now Playing: the platform-neutral half (one snapshot of the player, album art pixels and colour).
using DrawColor = System.Drawing.Color;

namespace Glance;

enum MediaButton { Previous = 0, PlayPause = 1, Next = 2 }

// One moment of the player's state. Never changed after it is published.
sealed class NowPlayingInfo
{
    public string AppId = "", Title = "", Artist = "", Album = "";
    public bool Playing, CanPrevious, CanPlayPause, CanNext;
    public TimeSpan Position, Duration;
    public DateTime PositionUtc;                         // when Position was true
    public DateTime PausedSinceUtc = DateTime.MinValue;  // MinValue = we never saw it playing
    public AlbumArt Art;

    public string TrackKey => Title + "\u0001" + Artist + "\u0001" + Album;
    public bool HasTimeline => Duration.TotalSeconds >= 1;
    public string StaticKey => string.Join("|", TrackKey, Art?.Version ?? 0, Playing, HasTimeline, Duration.TotalHours >= 1);
    public string ControlsKey => string.Join("|", Playing, CanPrevious, CanPlayPause, CanNext);

    public bool Can(MediaButton b) => b switch
    {
        MediaButton.Previous => CanPrevious,
        MediaButton.Next => CanNext,
        _ => CanPlayPause,
    };

    public TimeSpan PositionAt(DateTime utc)
    {
        TimeSpan p = Position;
        if (Playing && utc > PositionUtc) p += utc - PositionUtc;
        if (HasTimeline && p > Duration) p = Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }

    // 0:05, 3:46, 1:02:03
    public static string Format(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    public NowPlayingInfo Clone() => (NowPlayingInfo)MemberwiseClone();
}

// Album art as plain managed pixels, so any thread can hold it and nothing needs disposing.
sealed class AlbumArt
{
    public readonly int Size;
    public readonly int[] Pixels;    // ARGB, Size x Size
    public readonly DrawColor? Tint; // the cover's main colour, or null for black/white/grey covers
    public readonly int Version;
    public readonly string Hash;

    public AlbumArt(int size, int[] pixels, int version, string hash)
    {
        Size = size;
        Pixels = pixels;
        Version = version;
        Hash = hash;
        Tint = MainColour(pixels, size);
    }


    // The cover's most colourful tone, evened out so it can tint the glass. Covers without
    // a real colour (black, white, grey) give null and leave the glass as it is.
    public static DrawColor? MainColour(int[] px, int size)
    {
        double r = 0, g = 0, b = 0, w = 0;
        int n = 0, step = Math.Max(1, size / 48);
        for (int y = 0; y < size; y += step)
            for (int x = 0; x < size; x += step)
            {
                int c = px[y * size + x];
                int cr = (c >> 16) & 255, cg = (c >> 8) & 255, cb = c & 255;
                int max = Math.Max(cr, Math.Max(cg, cb)), min = Math.Min(cr, Math.Min(cg, cb));
                double sat = max == 0 ? 0 : (max - min) / (double)max;
                double weight = sat * sat * (max / 255.0); // vivid and not too dark
                r += cr * weight; g += cg * weight; b += cb * weight; w += weight; n++;
            }
        if (n == 0 || w / n < 0.02) return null;
        DrawColor avg = DrawColor.FromArgb((int)(r / w), (int)(g / w), (int)(b / w));
        return FromHsv(avg.GetHue(), 0.75, 0.62);
    }

    static DrawColor FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return DrawColor.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}

