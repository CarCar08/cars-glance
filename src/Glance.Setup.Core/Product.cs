namespace Glance.Setup
{
    /// <summary>Every name and place that identifies C.A.R.S. Glance, in one spot. Setup, the release tool and the
    /// app's self-update all read these, so renaming or forking the app is a single edit.</summary>
    public static class Product
    {
        public const string Name = "C.A.R.S. Glance";
        public const string Tagline = "Your day at a glance, right on the desktop";
        public const string FolderName = "CARS Glance";            // %LOCALAPPDATA%\Programs\CARS Glance and %LOCALAPPDATA%\CARS Glance
        public const string MainExe = "CarsGlance.exe";
        public const string ProcessName = "CarsGlance";
        public const string SetupFileName = "CarsGlance-Setup.exe";
        public const string SetupAssetPattern = @"^CarsGlance-Setup-.+\.exe$";
        public const string Publisher = "C.A.R.S. Glance contributors";
        public const string Repository = "CarCar08/cars-glance";
        public const string RepositoryUrl = "https://github.com/" + Repository;
        public const string Aumid = "CarCar08.CarsGlance";         // Windows notifications and taskbar identity
        public const string ArpKeyName = "CarsGlance";             // HKCU\...\Uninstall\CarsGlance
        public const string RunValue = "CARS Glance";              // HKCU\...\Run
        public const string WatchdogTask = "CARS Glance keep running";
        public const string SetupLogName = "CarsGlance-Setup.log";
        public const string CredentialPrefix = "CarsGlance:";       // Windows Credential Manager entries
    }
}
