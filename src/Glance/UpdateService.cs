// Self-update: checks GitHub Releases for a newer version, downloads its Setup and checks it against the
// release's SHA256SUMS.txt. The widget then shows a small "Update ready" pill; a click runs that Setup silently,
// which waits for Glance to exit, swaps the files (rolling back on any failure) and starts the new version.
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Glance.Setup;

namespace Glance;

sealed class UpdateService
{
    public sealed record ReadyUpdate(SemVer Version, string SetupPath, string Sha256, string Notes);

    static readonly HttpClient Http = CreateClient();
    DateTime nextCheckUtc = DateTime.UtcNow.AddSeconds(75);
    volatile bool busy;
    volatile ReadyUpdate ready;

    public ReadyUpdate Ready => ready;

    public static SemVer Current { get; } = ReadVersion();

    /// <summary>Only the installed copy updates itself (a development build or a test copy never does).</summary>
    public static bool IsInstalledCopy =>
        Paths.AppDir.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product.FolderName),
                                StringComparison.OrdinalIgnoreCase);

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(WeatherService.UserAgent);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static SemVer ReadVersion()
    {
        string v = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus]; // drop the "+commit" build metadata
        return SemVer.TryParse(v, out SemVer s) ? s : new SemVer(0, 0, 0);
    }

    // Housekeeping: about a minute after start, then once a day; never while a game is full screen.
    public void Tick(bool gameMode, bool force = false)
    {
        if (!IsInstalledCopy || busy || gameMode) return;
        if (!force && DateTime.UtcNow < nextCheckUtc) return;
        busy = true;
        nextCheckUtc = DateTime.UtcNow.AddHours(24);
        Task.Run(async () =>
        {
            try { await CheckAsync(); }
            catch (Exception ex)
            {
                Log.Write("update check: " + ex.Message);
                nextCheckUtc = DateTime.UtcNow.AddHours(3);
            }
            finally { busy = false; }
        });
    }

    async Task CheckAsync()
    {
        string json = await Http.GetStringAsync("https://api.github.com/repos/" + Product.Repository + "/releases?per_page=20");
        ReleaseInfo pick = ReleaseInfo.PickUpdate(json, Current, includePreReleases: Current.IsPreRelease);
        if (pick == null) return;
        if (ready != null && ready.Version.CompareTo(pick.Version) >= 0) return;

        ReleaseAsset setup = pick.Assets.First(a => Regex.IsMatch(a.Name, Product.SetupAssetPattern));
        ReleaseAsset sums = pick.Assets.First(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        string expected = Checksums.HashFor(await Http.GetStringAsync(sums.DownloadUrl), setup.Name)
                          ?? throw new InvalidDataException("SHA256SUMS.txt doesn't list " + setup.Name);

        string dir = Path.Combine(Paths.DataDir, "updates", pick.Version.ToString());
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, setup.Name);
        if (!File.Exists(file) || !Sha256(file).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            string part = file + ".part";
            using (Stream src = await Http.GetStreamAsync(setup.DownloadUrl))
            using (FileStream dst = File.Create(part))
                await src.CopyToAsync(dst);
            if (!Sha256(part).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException("the downloaded " + setup.Name + " doesn't match its published checksum; it was deleted");
            }
            File.Move(part, file, overwrite: true);
        }
        ready = new ReadyUpdate(pick.Version, file, expected, ReleaseNotesText.Plain(pick.Notes));
        Log.Write("update ready: " + pick.Version);
    }

    static string Sha256(string file)
    {
        using FileStream fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>Starts the verified Setup (checked once more, right before it runs). The caller then exits.</summary>
    public bool Apply()
    {
        ReadyUpdate r = ready;
        if (r == null || !File.Exists(r.SetupPath)) return false;
        if (!Sha256(r.SetupPath).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write("update: the downloaded Setup changed on disk; not running it");
            ready = null;
            return false;
        }
        Process.Start(new ProcessStartInfo(r.SetupPath, "--update --silent --launch --wait-for-pid " + Environment.ProcessId)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        })?.Dispose();
        return true;
    }
}
