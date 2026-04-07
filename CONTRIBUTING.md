# Contributing to Emutastic

Thanks for your interest in contributing! Here's what you need to know.

## Getting Started

1. Fork the repository and clone it locally
2. Open `Emutastic.sln` in Visual Studio 2022 or later
3. Target framework is **.NET 8 (Windows)** — Windows 10/11 only
4. Build and run — no additional setup required beyond having the .NET 8 SDK installed

## Adding Cores

Emutastic uses libretro cores. To add support for a new system:

- Add a console handler in `Services/ConsoleHandlers/` implementing `IConsoleHandler`
- Register the core mapping in `CoreManager`
- Add the system to the sidebar in `MainWindow.xaml`
- Add artwork mappings in `ArtworkService.cs` (`LibretroSystemMap`)

## Pull Requests

- Keep PRs focused — one feature or fix per PR
- Test on a real library if touching import, database, or artwork code
- The database schema uses SQLite; migrations go in `DatabaseService.InitializeDatabase()` and must be idempotent
- Window/UI changes should respect the existing dark theme (accent color is `#E03535`)

## Reporting Issues

Open an issue on GitHub with:
- What you were doing
- What you expected to happen
- What actually happened
- Your OS version and any relevant console/core details

## Code Style

- C# with WPF / MVVM-lite (no heavy frameworks)
- Match the surrounding code style — no reformatting unrelated code
- Keep libretro callback paths allocation-free where possible (they run on the emu thread at 60fps)
