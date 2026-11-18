// The "Connect Todoist" window (Start menu): you paste your own API token, the window checks it
// with Todoist, then saves it in Windows Credential Manager and tells the running widget to refresh.
// The token never touches a file, a log or the screen (the box hides it).
using System.Diagnostics;
using System.Drawing;

namespace Glance;

sealed class ConnectTodoistForm : Form
{
    const string DeveloperSettings = "https://app.todoist.com/app/settings/integrations/developer";
    static readonly Color Back = Color.FromArgb(22, 14, 16), Field = Color.FromArgb(38, 26, 29), Text1 = Color.FromArgb(240, 240, 240),
                          Text2 = Color.FromArgb(185, 180, 182), Red = Color.FromArgb(225, 38, 28);
    readonly TextBox token;
    readonly Button connect, disconnect, cancel;
    readonly Label status;

    public ConnectTodoistForm()
    {
        Text = "Connect Todoist";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        BackColor = Back;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 10.5f);
        ClientSize = new Size(520, 270);
        Padding = new Padding(24);

        var title = new Label { Text = "Connect Todoist", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(24, 20) };
        var help = new Label
        {
            Text = "Your tasks due today and overdue will show in the widget's agenda.\nPaste your Todoist API token below. Todoist: Settings → Integrations → Developer.",
            ForeColor = Text2, AutoSize = true, MaximumSize = new Size(472, 0), Location = new Point(24, 58),
        };
        var link = new LinkLabel
        {
            Text = "Open Todoist's Developer settings", AutoSize = true, Location = new Point(24, 104),
            LinkColor = Red, ActiveLinkColor = Text1, VisitedLinkColor = Red,
        };
        link.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo(DeveloperSettings) { UseShellExecute = true }); } catch { } };
        token = new TextBox
        {
            UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Field, ForeColor = Text1,
            Location = new Point(24, 138), Width = 472, PlaceholderText = "API token",
        };
        status = new Label { ForeColor = Text2, AutoSize = true, MaximumSize = new Size(472, 0), Location = new Point(24, 176) };
        connect = MakeButton("Connect", Red, new Point(396, 216));
        cancel = MakeButton("Cancel", Field, new Point(288, 216));
        disconnect = MakeButton("Disconnect", Field, new Point(24, 216));
        disconnect.Visible = Credentials.Read() != null;
        if (disconnect.Visible) status.Text = "Todoist is connected. Paste a new token to replace it.";

        connect.Click += async (s, e) => await ConnectAsync();
        cancel.Click += (s, e) => Close();
        disconnect.Click += (s, e) =>
        {
            Credentials.Delete();
            SignalWidget();
            disconnect.Visible = false;
            status.ForeColor = Text2;
            status.Text = "Disconnected. The token was removed from Credential Manager.";
        };
        AcceptButton = connect;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { title, help, link, token, status, connect, cancel, disconnect });
    }

    Button MakeButton(string text, Color back, Point at)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Text1,
            Size = new Size(100, 34), Location = at, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    async Task ConnectAsync()
    {
        string value = token.Text.Trim();
        if (value.Length < 20) { Say("That doesn't look like a Todoist API token.", true); return; }
        connect.Enabled = token.Enabled = false;
        Say("Checking with Todoist…", false);
        try
        {
            List<AgendaItem> tasks = await new TodoistService().FetchTasks(value, null);
            if (!Credentials.Write(value)) { Say("Couldn't save the token to Credential Manager.", true); return; }
            token.Text = "";
            SignalWidget();
            disconnect.Visible = true;
            Say($"Connected. {tasks.Count} task{(tasks.Count == 1 ? "" : "s")} due today or overdue. You can close this window.", false);
            connect.Text = "Done";
            connect.Enabled = true;
            connect.Click += (s, e) => Close();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            Say("Todoist didn't accept that token. Copy it again from Developer settings.", true);
        }
        catch (Exception)
        {
            Say("Couldn't reach Todoist. Check the connection and try again.", true);
        }
        finally
        {
            if (connect.Text != "Done") { connect.Enabled = token.Enabled = true; token.Focus(); }
        }
    }

    void Say(string text, bool error)
    {
        status.ForeColor = error ? Red : Text2;
        status.Text = text;
    }

    // Tells a running widget to fetch again now instead of within 5 minutes.
    public static void SignalWidget()
    {
        if (EventWaitHandle.TryOpenExisting(Program.RefreshEventName, out EventWaitHandle ev))
        {
            ev.Set();
            ev.Dispose();
        }
    }

    public static int Run()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.Run(new ConnectTodoistForm());
        return 0;
    }
}
