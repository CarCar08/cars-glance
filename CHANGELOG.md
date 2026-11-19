# Changelog

Each section becomes the release notes on GitHub. Newest first.

## 0.9.1 — Smoother digits (2026-11-18)

**Fixes**
- The seconds no longer roll both digits every second. Like an odometer, only the digit that changed rolls (14 → 15 moves the 4, never the 1), on the clock, countdowns and Next up.
- The roll is calmer: about half a second with a soft start and stop; the old digit drifts up and fades before the new one rises in, so they never smear together.
- When a change makes a number narrower or wider (10 → 11), the digits that stay glide to their new places instead of jumping.

**Under the hood**
- The release build moved to the current GitHub Actions versions.

## 0.9.0 — Glance goes public (2026-11-18)

The desktop widget that started as a GTA VI countdown is now **C.A.R.S. Glance**: its own app, installer and updates.

**What it is**
- A frosted-glass panel in the middle of every display, on the desktop layer: under your windows, over your wallpaper, never taking focus. Windows blurs the live wallpaper behind it.
- **Clock** with the date and weather (Open-Meteo, no account).
- **Now Playing** for any player that reports to Windows (Spotify, Apple Music, Cider, browsers…): cover, title, progress and previous / play-pause / next.
- **Agenda**: today's events (Apple Calendar through Spark Desktop) and your Todoist tasks due today or overdue; an appointment kept in both shows once.
- **Next up**: 15 minutes before a timed event the panel counts down to it, then shows "NOW · STARTED" for 5 minutes.
- **Countdowns** you define, with a final-hour takeover and a "done" message.
- Smooth motion: views slide, songs rise into place, the page dot glides, the play button morphs. Nothing animates while a game or video is full screen.

**Installer and updates**
- `CarsGlance-Setup-0.9.0.exe` installs for you only: no administrator approval, no .NET to install. Uninstall from Windows Settings › Apps.
- Glance checks GitHub Releases once a day. When an update is ready (its checksum verified), a small **Update ready** pill appears on the panel; click it to update. Your settings are kept.
- If Glance ever stops unexpectedly, a small scheduled task starts it again (unless you quit it yourself).
- After sleep or a time-zone change everything refreshes at once.

**Good to know**
- Settings live in `%LOCALAPPDATA%\CARS Glance\settings.json` (Start menu › "C.A.R.S. Glance Settings" opens it). A proper Settings window arrives in 1.0.
- Todoist: Start menu › "C.A.R.S. Glance - Connect Todoist". Your token is kept in Windows Credential Manager.
- Setup isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC": choose **More info › Run anyway**. Check the file against `SHA256SUMS.txt` first if you like (see docs/install.md).
