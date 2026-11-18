# Contributing

Thanks for wanting to help!

- **Bugs and ideas**: open an issue. For bugs, include your Windows version, your display setup and the log from `%LOCALAPPDATA%\CARS Glance\logs\glance.log`.
- **Pull requests**: keep them focused. Run `dotnet test` first; for anything visual, attach a `--preview` render (see [docs/building.md](docs/building.md)).

Design rules the project keeps:
- Stay on the desktop layer, never take focus, never steal a game's frames: nothing animates while something is full screen.
- Clean motion only: no glows, gloss sweeps or pulsing.
- Text must stay readable for people with low vision (large type, real contrast).
- No telemetry. Send nothing a feature doesn't need.
