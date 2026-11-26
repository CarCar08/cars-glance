# Changelog

Each section becomes the release notes on GitHub. Newest first.

## 1.0.2 — Glance goes blue (2026-11-26)

**New look**
- Blue is Glance's colour now: the panel's titles, marks and page dot, the right-click menu, Quick add, Connect Todoist, the Settings window, Setup and the app icon. The glass has a faint cool tint instead of a warm one.
- If you've saved anything in the Settings window, your accent (red, back then) is kept: pick Blue in Settings › Appearance to switch. Otherwise Glance turns blue by itself. Red is still one click away there too.
- Error messages stay a soft red, and Todoist's own priority colours (red for p1) are unchanged.

## 1.0.1 — Quieter hover (2026-11-19)

**Fixes**
- Hovering over a countdown no longer puts a big grey box behind the digits. Large areas (a countdown, the album cover) now only change the pointer to a hand.
- The soft highlight behind clickable lines (Home's lines, agenda rows, the weather) is much fainter, and stays faint on dark wallpapers: it came out several times brighter than intended over black.

## 1.0.0 — Interactive (2026-11-18)

Glance is no longer just something to look at: click things on the panel to use them, and change everything in a proper Settings window.

**Use the views**
- **Agenda**: tick a task's ring to complete it in Todoist (it's struck through with **Undo** for 5 seconds first), click a task to open it in Todoist, click an event for a card with **Directions** and **Join** (meeting links are found in the location or notes), **+** adds a task in plain words ("Call the dentist tomorrow 3pm #Errands p1"), and "+ N more" pages through a long day.
- **Now Playing**: click along the bar to jump, scroll over the cover to change that player's own volume, click the cover to bring the player up. Right-click for shuffle and repeat when the player allows them.
- **Next up**: **Join meeting** and **Directions** when the event has them; right-click › Dismiss.
- **Clock**: click the date for the agenda, the weather for the forecast.
- **Countdowns**: right-click › Pin / Unpin.
- Switching views: scroll anywhere, click a dot, or use the ‹ › arrows that appear at the edges. A click inside a view only ever does what's under the pointer; the hand cursor shows what's clickable.
- **Right-click menu** (dark, in Glance's colours): what the view can do, then Stay on this view for an hour, Hide for an hour, Refresh, Settings, Check for updates and Quit.

**Home**
- A new first view: the time and date on the left, and beside it your weather, what's next, tasks due, the song playing and your nearest countdown. Click a line to go there. (Prefer the big clock? Settings › Views › First view.)

**Settings window**
- Start menu › **C.A.R.S. Glance Settings** (or right-click › Settings): General, Views (first view and order), Weather (search for your city), Calendar & tasks (connect Todoist, Spark, Next up timing), Music, Countdowns (an editor), Appearance, Accessibility, Updates and About. Every change applies within a second.
- A **Welcome** page opens the first time Glance runs after a new install.
- About › **Copy diagnostics** makes a short report for bug reports: versions, displays, which features are on and the recent log, with no tokens, places, names, titles or links.

**Appearance and comfort**
- Accent colour (red stays the default), glass darkness, size (70–160%, text grows with it), position (centre, top, bottom or a corner, clear of the taskbar) and which displays get a panel.
- **Reduce motion** follows Windows' "Animation effects" (or always / never), **High contrast** gives solid glass and full-strength text, and on **battery saver** changes happen at once instead of animating.

**Also**
- Starting Glance again while it's hidden shows it at once.
- The separate "Connect Todoist" Start menu entry is gone: it's in Settings now.


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
