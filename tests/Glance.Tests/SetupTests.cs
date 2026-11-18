using System.Text.Json;
using Glance.Setup;
using Xunit;

namespace Glance.Tests;

// The installer's engine end to end, into a sandbox folder (no registry, no real Start menu).
public class SetupTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "CarsGlance-setup-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    (PayloadManifest manifest, string zip) Payload(string version, string marker)
    {
        string app = Path.Combine(root, "payload-" + version);
        Directory.CreateDirectory(Path.Combine(app, "runtimes"));
        File.WriteAllText(Path.Combine(app, Product.MainExe), "exe " + marker);
        File.WriteAllText(Path.Combine(app, "settings.default.json"), "{}");
        File.WriteAllText(Path.Combine(app, "runtimes", "native.dll"), "dll " + marker);
        PayloadManifest m = PayloadManifest.Build(app, version);
        string zip = Path.Combine(root, "payload-" + version + ".zip");
        SafeZip.Create(app, zip);
        return (m, zip);
    }

    [Fact]
    public void InstallsUpdatesAndUninstallsPerUser()
    {
        var layout = new InstallLayout(Path.Combine(root, "sandbox"));
        string self = Path.Combine(root, "Setup.exe");
        Directory.CreateDirectory(root);
        File.WriteAllText(self, "setup");
        var log = new List<string>();

        (PayloadManifest m1, string z1) = Payload("0.9.0", "one");
        var ops = new SandboxOps(layout, self);
        InstallResult first = new InstallEngine(layout, m1, () => File.OpenRead(z1), ops).Install(true, (p, t) => { }, log.Add);
        Assert.True(first.Ok, first.Message + " | " + string.Join(" | ", log));
        Assert.False(first.Upgraded);
        Assert.StartsWith(Path.Combine(layout.Sandbox!, "Programs"), layout.InstallDir);
        Assert.Equal("exe one", File.ReadAllText(Path.Combine(layout.InstallDir, Product.MainExe)));
        Assert.True(File.Exists(layout.SetupExe));
        Assert.True(File.Exists(layout.StartMenuShortcut));
        Assert.Contains(Product.Aumid, File.ReadAllText(layout.StartMenuShortcut));
        Assert.Contains("--settings", File.ReadAllText(layout.SettingsShortcut));
        Assert.Contains("--connect-todoist", File.ReadAllText(layout.TodoistShortcut));
        Assert.True(File.Exists(Path.Combine(layout.Sandbox!, "run-at-sign-in.txt")));
        Assert.True(File.Exists(Path.Combine(layout.Sandbox!, "watchdog.txt")));
        using (var arp = JsonDocument.Parse(File.ReadAllText(layout.SandboxArpFile)))
        {
            Assert.Equal(Product.Name, arp.RootElement.GetProperty("DisplayName").GetString());
            Assert.Equal("0.9.0", arp.RootElement.GetProperty("DisplayVersion").GetString());
            Assert.Contains("--uninstall", arp.RootElement.GetProperty("UninstallString").GetString());
        }

        // the user's data lives outside the install folder and survives an update
        Directory.CreateDirectory(layout.UserData);
        File.WriteAllText(Path.Combine(layout.UserData, "settings.json"), "{ \"mine\": true }");

        (PayloadManifest m2, string z2) = Payload("0.9.1", "two");
        InstallResult second = new InstallEngine(layout, m2, () => File.OpenRead(z2), new SandboxOps(layout, self)).Install(false, (p, t) => { }, log.Add);
        Assert.True(second.Ok, second.Message);
        Assert.True(second.Upgraded);
        Assert.Equal("exe two", File.ReadAllText(Path.Combine(layout.InstallDir, Product.MainExe)));
        Assert.False(Directory.Exists(layout.OldDir));
        Assert.False(File.Exists(Path.Combine(layout.Sandbox!, "run-at-sign-in.txt")));
        Assert.Equal("{ \"mine\": true }", File.ReadAllText(Path.Combine(layout.UserData, "settings.json")));

        var uninstallOps = new SandboxOps(layout, self);
        InstallResult gone = new InstallEngine(layout, m2, () => File.OpenRead(z2), uninstallOps).Uninstall(true, (p, t) => { }, log.Add);
        Assert.True(gone.Ok, gone.Message);
        Assert.False(Directory.Exists(layout.InstallDir));
        Assert.False(File.Exists(layout.StartMenuShortcut));
        Assert.False(File.Exists(layout.SandboxArpFile));
        Assert.False(Directory.Exists(layout.UserData));
        Assert.Contains("forget-credentials", uninstallOps.Events);
    }

    [Fact]
    public void ADamagedPackageLeavesTheInstalledVersionAlone()
    {
        var layout = new InstallLayout(Path.Combine(root, "sandbox2"));
        string self = Path.Combine(root, "Setup.exe");
        Directory.CreateDirectory(root);
        File.WriteAllText(self, "setup");
        (PayloadManifest m1, string z1) = Payload("1.0.0", "good");
        Assert.True(new InstallEngine(layout, m1, () => File.OpenRead(z1), new SandboxOps(layout, self)).Install(true, (p, t) => { }, _ => { }).Ok);

        (PayloadManifest m2, string z2) = Payload("1.0.1", "bad");
        m2.Files[0].Sha256 = new string('0', 64); // the manifest no longer matches the files
        InstallResult r = new InstallEngine(layout, m2, () => File.OpenRead(z2), new SandboxOps(layout, self)).Install(true, (p, t) => { }, _ => { });
        Assert.False(r.Ok);
        Assert.Equal("exe good", File.ReadAllText(Path.Combine(layout.InstallDir, Product.MainExe)));
    }

    [Fact]
    public void UpdatesComeOnlyFromNewerReleasesWithBothAssets()
    {
        string json = "[" +
            "{\"tag_name\":\"v0.9.1\",\"name\":\"0.9.1\",\"body\":\"## Fixes\\n- one\",\"prerelease\":false,\"draft\":false,\"html_url\":\"u\",\"assets\":[" +
            "{\"name\":\"CarsGlance-Setup-0.9.1.exe\",\"size\":1,\"browser_download_url\":\"d1\",\"url\":\"a1\"}," +
            "{\"name\":\"SHA256SUMS.txt\",\"size\":1,\"browser_download_url\":\"d2\",\"url\":\"a2\"}]}," +
            "{\"tag_name\":\"v0.9.2\",\"name\":\"0.9.2\",\"body\":\"\",\"prerelease\":false,\"draft\":false,\"html_url\":\"u\",\"assets\":[" +
            "{\"name\":\"CarsGlance-Setup-0.9.2.exe\",\"size\":1,\"browser_download_url\":\"d3\",\"url\":\"a3\"}]}]";
        ReleaseInfo pick = ReleaseInfo.PickUpdate(json, SemVer.Parse("0.9.0"), includePreReleases: false);
        Assert.NotNull(pick);
        Assert.Equal("0.9.1", pick.Version.ToString()); // 0.9.2 has no SHA256SUMS.txt, so it's skipped
        Assert.Null(ReleaseInfo.PickUpdate(json, SemVer.Parse("0.9.1"), includePreReleases: false));
    }
}
