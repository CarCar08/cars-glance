# Third-party notices

C.A.R.S. Glance is MIT-licensed (see LICENSE). It includes or uses the following.

## Shipped inside the app and installer

- **.NET runtime and Windows Desktop runtime** (Microsoft, MIT License): the app is published self-contained, so these libraries ship next to `CarsGlance.exe`. https://github.com/dotnet/runtime, https://github.com/dotnet/winforms
- **WPF-UI** (lepoco, MIT License), used by the installer's window. https://github.com/lepoco/wpfui
- **System.Text.Json** (Microsoft, MIT License). https://github.com/dotnet/runtime

## Online services the app talks to (only if you use the feature)

- **Open-Meteo** weather data, licensed under CC BY 4.0: "Weather data by Open-Meteo.com". https://open-meteo.com. Glance sends only the latitude and longitude you set (city level is enough).
- **Todoist API** (Doist), with the API token you paste yourself. https://developer.todoist.com
- **GitHub Releases API**, to check for updates. https://docs.github.com/rest/releases

## Fonts

Glance draws text with fonts that ship with Windows (Bahnschrift, Segoe UI, Yu Gothic UI, Malgun Gothic). No fonts are bundled.
