<div align="center">

<img src="VISOR Logo.png" alt="VISOR logo" width="180" />

# VISOR

### Virtual Intelligence Simulator Overlay & Radar

**A real-time race-intelligence overlay and proximity radar for [iRacing](https://www.iracing.com/).**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.txt)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2B%20(x64)-0078D6)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Version](https://img.shields.io/badge/version-1.0.0-brightgreen)

</div>

---

## What is VISOR?

VISOR is a free, open-source Windows desktop application that overlays live race
information on top of iRacing. It connects to iRacing's telemetry feed and renders
a compact, configurable heads-up display plus an optional proximity radar, giving
you the situational awareness you need without taking your eyes off the road.

VISOR is built for sim racers who want clean, glanceable data — fuel, deltas,
relative gaps, and surrounding traffic — without a cluttered screen.

> **Note:** VISOR is an independent project. It is not affiliated with, endorsed by,
> or sponsored by iRacing.com Motorsport Simulations, LLC.

---

## Features

- **Heads-up overlay** with independently toggleable rows:
  - Current gear and class position
  - Session time / laps remaining and fuel (laps of fuel left)
  - Center-out **lap delta bar** versus your session-best lap
  - Last-lap and best-lap times
  - **Relative display** — a 7-car window (cars ahead, you, cars behind) with live gap times
  - **Warnings row** — incident counter, pace/damage warning, and a "pit now" cue
- **Proximity radar** — a real-time 2D view of nearby cars across five zones so you
  always know who's alongside you, color-coded by car class.
- **Smart fuel calculation** — rolling multi-lap average burn translated into
  "laps of fuel remaining."
- **Transponder-style gap timing** — gaps are measured from a position-history ring
  buffer (the time between two cars crossing the same point on track), not a crude
  straight-line estimate.
- **Pace & damage detection** — flags abnormally slow laps combined with reduced top
  speed, and estimates whether pitting for repairs is worth the time loss.
- **AI driver detection** and per-driver incident counts pulled from session data.
- **Configurable UI** — three size presets, per-row visibility toggles, and a
  drag-to-position config mode.

---

## Download & Install

Download the latest signed installer from the
**[Releases page](https://github.com/phitz86/visor/releases)**.

1. Download `VISOR-Setup-<version>.exe`.
2. Run the installer. If the **.NET 8 Desktop Runtime** is not already present, the
   installer will offer to download and install it for you.
3. Launch VISOR, start iRacing, and the overlay will connect automatically when you
   enter a session.

### System requirements

| Requirement | Details |
|-------------|---------|
| OS          | Windows 10 version 1809 (build 17763) or later, 64-bit |
| Runtime     | .NET 8 Desktop Runtime (installer can provide it) |
| Simulator   | iRacing installed and running |

---

## Building from source

VISOR is a WPF application targeting **.NET 8** (`net8.0-windows8.0`). It builds on
Windows with the .NET 8 SDK; the installer is produced with
[Inno Setup](https://jrsoftware.org/isinfo.php).

### Prerequisites

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- (Optional, for the installer) [Inno Setup 6](https://jrsoftware.org/isdl.php)

### Build the application

```powershell
# Restore dependencies and build a Release binary
dotnet restore VISOR.sln
dotnet build VISOR.sln --configuration Release

# Output:
#   bin\Release\net8.0-windows8.0\VISOR.exe
```

You can also open `VISOR.sln` in Visual Studio 2022 (or newer) and build the
**Release** configuration.

### Build the installer

```powershell
# After a Release build, compile the Inno Setup script:
iscc "VISOR-Setup.iss"

# Output:
#   installer\VISOR-Setup-<version>.exe
```

The version number is read automatically from the compiled `VISOR.exe`.

---

## Project structure

```
VISOR/
├── App.xaml(.cs)          WPF application entry point
├── Views/                 Overlay, radar, and configuration windows (XAML + code-behind)
├── ViewModels/            MVVM presentation logic (relative table, radar, fuel, deltas, …)
├── Telemetry/             iRacing SDK integration, session parsing, position history
├── Diagnostics/           Async file logging and debug exporters
├── Settings/              User configuration and persistence
├── Resources/             Styles, fonts, and value converters
├── VISOR.csproj           Project file (.NET 8 / WPF)
├── VISOR-Setup.iss        Inno Setup installer script
└── LICENSE.txt            GNU GPL v3
```

Telemetry access is provided by the
[SVappsLAB.iRacingTelemetrySDK](https://www.nuget.org/packages/SVappsLAB.iRacingTelemetrySDK)
NuGet package.

---

## Privacy

VISOR runs entirely on your local machine. It reads iRacing telemetry locally to
render the overlay and does **not** collect, store, or transmit any personal data or
telemetry to CephasMedia or any third party. No account, login, or network connection
is required for VISOR itself to function.

---

## Code signing

The Windows installer and executable are digitally signed.

Free code signing is provided by [SignPath.io](https://signpath.io/), using a free
code-signing certificate issued by the [SignPath Foundation](https://signpath.org/).

---

## License

VISOR is licensed under the **GNU General Public License v3.0**. See
[`LICENSE.txt`](LICENSE.txt) for the full text.

```
Copyright (C) 2025-2026 CephasMedia LLC

This program is free software: you can redistribute it and/or modify it under
the terms of the GNU General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later
version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU General Public License for more details.
```

---

## Support the project

VISOR is developed and maintained by **Pete Hitzeman** at **CephasMedia LLC**.

- 💬 Found a bug or have a feature idea? [Open an issue](https://github.com/phitz86/visor/issues).
- ☕ Want to support development? [Donate via Venmo](https://venmo.com/u/Pete-Hitzeman).
- 📧 Other inquiries: [info@cephasmedia.com](mailto:info@cephasmedia.com)

See you on track. 🏁
