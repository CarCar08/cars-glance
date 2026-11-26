// What you can click on the panel (1.0): every view registers hit regions; a click only ever does what's
// under the pointer (empty glass does nothing). Views switch with the dots, the scroll wheel or the ‹ › edge
// arrows. Right-click opens a menu styled like the panel.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Glance;

enum HitKind
{
    None,
    Dot, EdgePrev, EdgeNext, Update, Hint,
    Media, Seek, Cover,                          // Now Playing
    Weather, Date,                               // Clock / Home
    TaskDone, Undo, TaskOpen, EventOpen, AddTask, More,  // Agenda
    CardDirections, CardJoin, CardClose,         // Agenda: an event's detail card
    NextDirections, NextJoin,                    // Next up
    Countdown,                                   // Countdown
    HomeLine,                                    // Home: a line opens its view (Index = which line)
}

readonly record struct Hit(HitKind Kind, Rectangle Rect, int Index = -1)
{
    public static readonly Hit Nothing = new(HitKind.None, Rectangle.Empty);
    public bool Any => Kind != HitKind.None;
    // Lines of text that get the soft hover plate. Buttons draw their own hover; big areas (a countdown's digits,
    // the album cover) only change the cursor: a plate that size looked like a grey slab on a dark wallpaper (1.0.1).
    public bool Plate => Kind is HitKind.Weather or HitKind.Date or HitKind.TaskOpen or HitKind.EventOpen or HitKind.More
        or HitKind.NextDirections or HitKind.HomeLine;
}

static class Open
{
    // Opens a link or a shell target the normal way (default browser / app). Never throws.
    public static void Url(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Write("open " + url + ": " + ex.Message); }
    }

}

// ------------------------------------------------------------------ the right-click menu, in the panel's colours

sealed class GlanceMenuRenderer : ToolStripProfessionalRenderer
{
    public static readonly Color Back = Color.FromArgb(14, 18, 24), Hover = Color.FromArgb(70, 61, 139, 255),
        Text = Color.FromArgb(240, 240, 240), Dim = Color.FromArgb(150, 150, 150), Line = Color.FromArgb(55, 255, 255, 255);

    public GlanceMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Dim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle r = e.ImageRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Style.Accent, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new[] { new PointF(r.Left + r.Width * 0.18f, r.Top + r.Height * 0.52f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.76f), new PointF(r.Right - r.Width * 0.14f, r.Top + r.Height * 0.26f) });
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}

// ------------------------------------------------------------------ quick add (Todoist natural language)

// A small dark box that takes focus only because you asked for it: type "Call the dentist tomorrow 3pm", Enter.
sealed class QuickAddForm : Form
{
    readonly TextBox box;
    readonly Label status;
    readonly Func<string, Task<string>> add;

    public QuickAddForm(Func<string, Task<string>> add)
    {
        this.add = add;
        Text = "Add a Todoist task";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        BackColor = GlanceMenuRenderer.Back;
        ForeColor = GlanceMenuRenderer.Text;
        Font = new Font("Segoe UI", 12f);
        ClientSize = new Size(560, 128);
        box = new TextBox
        {
            Location = new Point(18, 18), Width = 524, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(28, 33, 40), ForeColor = GlanceMenuRenderer.Text,
            PlaceholderText = "Call the dentist tomorrow at 3pm #Errands p1",
        };
        status = new Label
        {
            Location = new Point(18, 62), AutoSize = true, MaximumSize = new Size(524, 0), ForeColor = GlanceMenuRenderer.Dim,
            Text = "Enter adds it to Todoist (dates, #projects, @labels and p1–p4 work). Esc closes.",
            Font = new Font("Segoe UI", 10f),
        };
        Controls.Add(box);
        Controls.Add(status);
        KeyPreview = true;
        KeyDown += async (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; await Submit(); }
        };
        Shown += (s, e) => { Activate(); box.Focus(); };
    }

    async Task Submit()
    {
        string text = box.Text.Trim();
        if (text.Length == 0) return;
        box.Enabled = false;
        status.ForeColor = GlanceMenuRenderer.Dim;
        status.Text = "Adding…";
        string error = await add(text);
        if (error == null) { Close(); return; }
        box.Enabled = true;
        box.Focus();
        status.ForeColor = Color.FromArgb(255, 120, 110); // errors stay a soft red
        status.Text = error;
    }
}
