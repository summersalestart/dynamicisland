<picture>
  <source media="(prefers-color-scheme: dark)" srcset="./dark.png">
  <source media="(prefers-color-scheme: light)" srcset="./light.png">
  <img src="./light.png" alt="Your image">
</picture>

<p align="center">
  <a href="https://github.com/summersalestart/dynamicisland/releases">
    <img src="https://img.shields.io/github/downloads/summersalestart/dynamicisland/total?style=for-the-badge&label=Downloads" alt="Downloads">
  </a>
  <a href="https://github.com/summersalestart/dynamicisland/releases">
    <img src="https://img.shields.io/github/v/release/summersalestart/dynamicisland?style=for-the-badge&label=Releases" alt="Releases">
  </a>
  <img src="https://img.shields.io/badge/I%20Love-KFC-red?style=for-the-badge&logo=kfc&logoColor=white" alt="I Love KFC">
</p>

# DynIsland for Windows

An iPhone-style Dynamic Island overlay for Windows. A native, lightweight WPF app
(.NET 10, no Electron) a top-center always on top pill that shows the time and
weather at rest, expands for music, notifications, and timers, then settles back

## Download

Get the latest zip from [**Releases**](../../releases)

> Windows SmartScreen will warn about an unknown publisher (the build is unsigned).
> Click **More info → Run anyway**. Only a paid codesigning cert removes that prompt

No installer needed first launch auto-registers startup (HKCU Run), creates
`%AppData%\DynIsland\`, and puts an icon in the tray

## Features

- **Idle pill**: live local time (12/24h, seconds optional) plus weather via
  Open-Meteo: automatic location via ip-api, manual city or `lat, lon` in Settings,
  cached and refreshed every N minutes, last-known shown offline.
- **Music**": any Windows SMTC source (Spotify, Chrome, Edge, …): title/artist,
  album art (crossfaded), position/duration with a gliding progress bar, and working
  ⏮ ▶/⏸ ⏭ controls Expands when playback starts, settles to a compact pill with a
  green dot while paused.
- **Notifications**: Windows toast popups are mirrored onto the island (app name +
  title + body) for N seconds. Capture combines the `UserNotificationListener` API
  (where package identity allows) with a UI-Automation toast watcher plus a 1.2 s
  snapshot poll that catches popups whose open event was missed.
- **Timer**: native island countdown from tray presets (1–60 min) or Settings:
  live remaining time in the pill, hover detail, time's-up card + beeps, survives restarts
- **Island behavior**: global `Ctrl+Shift+D` toggle (remappable in Settings),
  hover to expand (pill dips, border glows, detail fades in), smooth size morphs
  between states with an automatic low-animation mode on weak GPUs / RDP.
- **Tray & Settings**: dark pill-matched theme throughout: tray menu (double-click
  opens Settings), borderless Inter-type Settings panel, everything persisted to
  `%AppData%\DynIsland\settings.json`.

## Build from source

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
(Windows, with WPF workload).

```powershell
# Run it
dotnet run --project app

# Framework-dependent publish (needs .NET 10 Desktop Runtime on target)
dotnet publish app/DynIsland.csproj -c Release -o app/publish

# Self-contained single file (runs anywhere, ~190 MB)
dotnet publish app/DynIsland.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

## Project layout

```
dyn.png                  # source artwork (converted to app/dyn.ico at build time)
app/
  MainWindow.xaml(.cs)   # the island: states, morphs, hover, timer, hotkey
  MediaService.cs        # SMTC playback sampling + artwork cache
  NotificationService.cs # toast capture (UNL + UIA watcher + snapshot poll)
  WeatherService.cs      # Open-Meteo + ip-api + geocoding
  Settings*.xaml(.cs)    # dark Settings panel + persistence
  StartupHelper.cs       # forced HKCU Run registration
  App.xaml(.cs)          # single instance, dark tray menu
  Fonts/                 # bundled Inter (SIL OFL) for the Settings UI
```

## Limitations (honest)

- Unpackaged exe, so the official notification-history API is unavailable: only
  toasts **while their popup is visible** can be mirrored, not silent history items
- Transparent layered-window resize animation is GPU-dependent

## Privacy

No telemetry, Settings and logs stay in `%AppData%\DynIsland\`.
Network calls only: Open-Meteo (weather), ip-api (coarse location, auto mode only),
and Open-Meteo geocoding (only when you type a manual city).

## License

MIT
