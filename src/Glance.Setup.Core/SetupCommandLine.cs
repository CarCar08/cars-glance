using System.Collections.Generic;

namespace Glance.Setup
{
    public enum SetupMode
    {
        /// <summary>Show the wizard (install, or update/repair/uninstall when already installed).</summary>
        Interactive,
        Install,
        Update,
        Repair,
        Uninstall,
    }

    /// <summary>
    /// Setup's command line:
    ///   CarsGlance-Setup.exe                           wizard
    ///   --install | --update | --repair | --uninstall  same, from a script or the app's self-update
    ///   --silent                                       no wizard window
    ///   --keep-data / --remove-data                    for --uninstall: what happens to %LOCALAPPDATA%\CARS Glance and saved sign-ins
    ///   --launch                                       start the app when done (default for the wizard and --update)
    ///   --no-start-at-sign-in                          don't start Glance when you sign in
    ///   --sandbox "folder"                             test mode: everything into that folder, no registry
    ///   --wait-for-pid N                               wait for that process (the app asking for its own update) to exit first
    /// </summary>
    public sealed class SetupCommandLine
    {
        public SetupMode Mode { get; private set; } = SetupMode.Interactive;
        public bool Silent { get; private set; }
        public bool? RemoveData { get; private set; }
        public bool Launch { get; private set; }
        public bool NoStartAtSignIn { get; private set; }
        public string? Sandbox { get; private set; }
        public string? WaitForPid { get; private set; }
        public List<string> Problems { get; } = new List<string>();

        public static SetupCommandLine Parse(IEnumerable<string> args)
        {
            var c = new SetupCommandLine();
            var list = new List<string>(args);
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                string? Next()
                {
                    if (i + 1 < list.Count) return list[++i];
                    c.Problems.Add("Missing value after " + a);
                    return null;
                }

                switch (a.ToLowerInvariant())
                {
                    case "--install": c.Mode = SetupMode.Install; break;
                    case "--update": c.Mode = SetupMode.Update; c.Launch = true; break;
                    case "--repair": c.Mode = SetupMode.Repair; break;
                    case "--uninstall": c.Mode = SetupMode.Uninstall; break;
                    case "--silent": c.Silent = true; break;
                    case "--launch": c.Launch = true; break;
                    case "--keep-data": c.RemoveData = false; break;
                    case "--remove-data": c.RemoveData = true; break;
                    case "--no-start-at-sign-in": c.NoStartAtSignIn = true; break;
                    case "--sandbox": c.Sandbox = Next(); break;
                    case "--wait-for-pid": c.WaitForPid = Next(); break;
                    default: c.Problems.Add("Unknown option: " + a); break;
                }
            }
            return c;
        }
    }
}
