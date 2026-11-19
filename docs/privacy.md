# Privacy

C.A.R.S. Glance has **no account, no telemetry, no analytics and no ads**. It runs on your PC and talks only to the services below, and only when you use the feature.

| Service | When | What is sent | Stored where |
|---|---|---|---|
| [Open-Meteo](https://open-meteo.com) | Every 15 minutes, if weather is on and a location is set | The latitude and longitude in your settings (rounded to two decimals, about a kilometre). Never an address. | Nothing stored; the latest forecast is kept in memory. |
| [Todoist](https://todoist.com) | Every 5 minutes, if you connected Todoist | Your API token (to Todoist only, over HTTPS) and a request for tasks due today or overdue | The token is in **Windows Credential Manager** under `CarsGlance:Todoist`, never in a file or log. |
| [Todoist](https://todoist.com), when you act | When you tick a task or add one on the panel | The task's id (to complete it) or the words you typed (to add it) | Nothing stored by Glance. |
| [Open-Meteo geocoding](https://open-meteo.com/en/docs/geocoding-api) | Only when you search for a city in Settings › Weather | The city name you typed | Glance keeps the chosen city's name and its position rounded to two decimals. |
| Spark Desktop (local) | Every 5 minutes, if Apple Calendar through Spark is on | Nothing leaves your PC: Glance asks the Spark app on your computer for today's events | The last list is cached in `%LOCALAPPDATA%\CARS Glance\calendar-cache.json` so the agenda works while Spark is closed. |
| Windows media controls (local) | While a player is open | Nothing leaves your PC | Nothing stored. |
| [GitHub Releases](https://github.com/CarCar08/cars-glance/releases) | About a minute after start, then daily | A normal request for the list of releases (no identifiers) | Downloaded updates in `%LOCALAPPDATA%\CARS Glance\updates`. |

Every request identifies itself only as `CarsGlance/1.0 (+https://github.com/CarCar08/cars-glance)`: no name, no e-mail, no machine ID.

## Your files

- Settings: `%LOCALAPPDATA%\CARS Glance\settings.json`
- Logs: `%LOCALAPPDATA%\CARS Glance\logs\` (two files at most, 512 KB each). Logs contain errors and events like "update ready", never tokens.
- Links you click on the panel (directions, meeting links, a task in Todoist, the forecast) open in your own browser; Glance itself sends nothing to those sites.
- Settings › About › Copy diagnostics puts a short report on your clipboard (nothing is sent anywhere): versions, displays, which features are on and the recent log with links removed.

Uninstalling with **Also delete my settings and saved sign-ins** removes that folder and Glance's entries in Credential Manager.
