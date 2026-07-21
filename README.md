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
  - **Warnings row** — track-location readout, incident counter, and track
    temperature with heating/cooling trend
- **Proximity radar** — a real-time 2D view of nearby cars across five zones so you
  always know who's alongside you, color-coded by car class.
- **Smart fuel calculation** — rolling multi-lap average burn translated into
  "laps of fuel remaining."
- **Transponder-style gap timing** — gaps are measured from a position-history ring
  buffer (the time between two cars crossing the same point on track), not a crude
  straight-line estimate.
- **Track-location readout** — the name of the corner or section you're in
  ("Eau Rouge", "Kemmel Straight"), like a sign hanging over the track surface.
  Driven by an editable catalog (`Data/TrackSections.json`) covering 66 layouts
  out of the box — the Nordschleife, Le Mans, and most iRacing road courses —
  tune boundaries or add tracks with a text editor. Measured turn positions and
  many names imported from [lovely-track-data](https://github.com/Lovely-Sim-Racing/lovely-track-data)
  by [Lovely Sim Racing](https://lsr.gg) (CC BY-NC-SA 4.0).
- **AI driver detection** and per-driver incident counts pulled from session data.
- **Configurable UI** — three size presets, per-row visibility toggles, and a
  drag-to-position config mode.

---

## Download & Install

Download the latest installer from the
**[Releases page](https://github.com/phitz86/visor/releases)**.

1. Download `VISOR-Setup-<version>.exe`.
2. Run the installer. Because VISOR is not yet code-signed, Windows may show a
   **SmartScreen "Windows protected your PC"** prompt — this is expected for a new
   independent app, not a sign of anything wrong. Click **More info → Run anyway**
   to continue. (See [Code signing](#code-signing) below.)
3. If the **.NET 8 Desktop Runtime** is not already present, the installer will offer
   to download and install it for you.
4. Launch VISOR, start iRacing, and the overlay will connect automatically when you
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

**VISOR is not yet code-signed.** Because the installer and executable don't carry a
publisher certificate, Windows SmartScreen will likely flag the download with a
**"Windows protected your PC"** / **"unknown publisher"** warning the first time you
run it. This is normal for a new, independent open-source app with no signing
reputation yet — it isn't evidence that the file is unsafe.

To install anyway:

1. When SmartScreen appears, click **More info**.
2. Click **Run anyway**.

If you'd rather not take our word for it, VISOR is fully open source — the complete
build is right here in this repository, so you can read it or build the installer
yourself.

We'd love to remove this friction. Code signing is expensive for a free project, so
we're actively looking for a code-signing sponsor — including free programs for
open-source software such as the [SignPath Foundation](https://signpath.org/). If you
can help, [get in touch](mailto:info@cephasmedia.com). This section will be updated the
moment signed builds are available.

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
