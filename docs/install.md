# Installing C.A.R.S. Glance

## Requirements

- Windows 10 (version 2004 or later) or Windows 11, 64-bit.
- Nothing else: the app carries its own .NET runtime, and it installs for your account only, so no administrator approval is needed.

## Install

1. Download `CarsGlance-Setup-<version>.exe` and `SHA256SUMS.txt` from the [latest release](https://github.com/CarCar08/cars-glance/releases/latest).
2. Run the setup. Choose whether Glance starts when you sign in (recommended) and whether to open it now.
3. Show your desktop (<kbd>Win</kbd>+<kbd>D</kbd>): the panel sits in the middle of every display.

Setup installs into `%LOCALAPPDATA%\Programs\CARS Glance` and adds two Start menu entries: **C.A.R.S. Glance** and **C.A.R.S. Glance Settings**. It also adds a small scheduled task, "CARS Glance keep running", that starts Glance again within five minutes if it ever stops without you quitting it.

### "Windows protected your PC"

The setup isn't code-signed yet (signing is planned for 1.6). Microsoft Defender SmartScreen shows this message for unsigned downloads it hasn't seen often. Choose **More info**, check that the app is `CarsGlance-Setup-<version>.exe`, then **Run anyway**.

### Check the download

In PowerShell, in your Downloads folder:

```powershell
(Get-FileHash .\CarsGlance-Setup-0.9.0.exe -Algorithm SHA256).Hash
```

The result must match the line for that file in `SHA256SUMS.txt` (upper or lower case doesn't matter). Glance's own updates do this check automatically.

## Updating

Glance checks for a new release about a minute after it starts and then once a day (never while a game is full screen). When one is ready, a small **Update ready** pill appears in the panel's top-right corner. Click it: the panel disappears for a few seconds and comes back on the new version, with your settings kept. If anything goes wrong, the version you had stays installed.

You can also run a newer setup over an existing install at any time.

## Uninstalling

**Settings › Apps › Installed apps › C.A.R.S. Glance › Uninstall.** Your settings stay in `%LOCALAPPDATA%\CARS Glance` and your saved sign-ins (Todoist) stay in Windows Credential Manager, unless you tick **Also delete my settings and saved sign-ins**.

## Silent install (scripts)

```
CarsGlance-Setup-0.9.0.exe --install --silent [--launch] [--no-start-at-sign-in]
CarsGlance-Setup-0.9.0.exe --uninstall --silent [--remove-data]
```

The exit code is 0 on success. A log is written to `%TEMP%\CarsGlance-Setup.log`.

## Troubleshooting

- **I don't see it.** It's on the desktop layer, under your windows: press <kbd>Win</kbd>+<kbd>D</kbd>. If it still isn't there, start **C.A.R.S. Glance** from the Start menu.
- **No weather.** Set your city's `latitude` and `longitude` in the settings file (Start menu › C.A.R.S. Glance Settings).
- **Something looks wrong.** The log is in `%LOCALAPPDATA%\CARS Glance\logs\glance.log`. Please [open an issue](https://github.com/CarCar08/cars-glance/issues) and include it.
