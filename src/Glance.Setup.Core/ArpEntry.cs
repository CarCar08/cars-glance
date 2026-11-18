using System.Collections.Generic;

namespace Glance.Setup
{
    /// <summary>What Windows' Installed apps list shows for C.A.R.S. Glance, and how it uninstalls it.
    /// Written under HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\CarsGlance (per user, no admin).</summary>
    public static class ArpEntry
    {
        public static Dictionary<string, object> Values(InstallLayout layout, PayloadManifest manifest, long estimatedSizeBytes)
        {
            var setup = layout.SetupExe;
            return new Dictionary<string, object>
            {
                ["DisplayName"] = Product.Name,
                ["DisplayVersion"] = manifest.Version,
                ["Publisher"] = Product.Publisher,
                ["InstallLocation"] = layout.InstallDir,
                ["DisplayIcon"] = layout.MainExe(manifest) + ",0",
                ["UninstallString"] = "\"" + setup + "\" --uninstall",
                ["QuietUninstallString"] = "\"" + setup + "\" --uninstall --silent --keep-data",
                ["ModifyPath"] = "\"" + setup + "\" --repair",
                ["URLInfoAbout"] = Product.RepositoryUrl,
                ["HelpLink"] = Product.RepositoryUrl + "/issues",
                ["NoModify"] = 0,
                ["NoRepair"] = 0,
                ["EstimatedSize"] = (int)(estimatedSizeBytes / 1024),
                ["InstallDate"] = System.DateTime.Now.ToString("yyyyMMdd"),
            };
        }
    }
}
