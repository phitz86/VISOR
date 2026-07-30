# Changelog

All notable changes to VISOR are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **Pole-sitter shown at the back of the field on the grid** — qualifying results
  report the pole position as `0`, which the pre-green grid sort read as "this car has
  no grid slot" and dumped to the back of the block. Everyone else shifted up one spot
  as a result. Positions snapped back to correct at the green flag, so it only affected
  the grid, parade laps, and the countdown to the start.
- **Position flickering by a full lap at the start/finish line** — `CarIdxLap` and
  `CarIdxLapDistPct` do not always tick over in the same telemetry frame, so for a frame
  or two a car crossing S/F read as a whole lap down and dropped to the tail of the
  running order before snapping back. The two variables are now realigned while they
  disagree.
- **Pre-green ordering no longer mixes grid sources** — the per-class live position array
  and the field-wide qualifying order were being used interchangeably per car, which
  could interleave a 1..n class number with a 1..N field number.
- **"FINISHED" appearing a lap early** — the checkered flag is now only allowed to end
  the race readout on a start/finish crossing that happens *after* the flag was seen, not
  one in the same telemetry sample. In multiclass this matters: the overall leader is
  usually lapping traffic when it finishes, so its checkered can land in the same frame as
  a slower-class car crossing the line with a lap still to run. Starting or reconnecting
  VISOR mid-race under the white or checkered flag no longer latches on the first frame
  either.
- **Very large relative gaps no longer crowd the gap column** — gaps beyond 99.9s (real
  measurements, but no longer proximity information) now read `99+` instead of a widening
  three-digit number.

## [1.0.0] - 2026-07-21

VISOR's first stable release, graduating the app out of beta. This release reworks
the Row 5 info area into two genuinely useful readouts, steadies the relative
display, and adds a substantial layer of app-stability and installer hardening.

### Added

- **Track-location readout (Row 5)** — names the corner or section you're currently
  in ("Eau Rouge," "Kemmel Straight"), driven by an editable catalog
  (`Data/TrackSections.json`) that ships beside the app. Covers 66 layouts out of the
  box, including the Nordschleife, Le Mans, and most iRacing road courses. Reads
  "Pit Lane" on pit road and hides on unknown tracks or off-world states. Section
  boundaries and many names were imported from
  [lovely-track-data](https://github.com/Lovely-Sim-Racing/lovely-track-data) by
  [Lovely Sim Racing](https://lsr.gg) (CC BY-NC-SA 4.0). The Nürburgring GP layout is
  fully calibrated to 15 measured, driver-confirmed apexes.
- **Track-temperature readout (Row 5)** — current surface temperature in °F or °C
  (user-selectable) with a red-up/blue-down heating/cooling trend arrow, smoothed so
  it reflects real condition changes rather than sensor jitter.
- **Class / Overall position toggle** for both the position row and the relative
  table, with the pace/safety car excluded from overall counts.
- **Per-element Row 5 toggles** — track location, incident counter, and track
  temperature can each be shown or hidden independently.
- **"Hide cars in the pits"** option for the relative display, with sensible
  exceptions (you always see your own row; pit-road cars reappear when you are on pit
  road).
- **In-app update check** — on startup VISOR quietly queries GitHub Releases and, if a
  newer version exists, surfaces a non-intrusive notice in the Config window. Fails
  silently when offline.
- **Single-instance enforcement** — launching VISOR again brings the existing window
  to the front instead of starting a second copy.
- **Global crash handling** and **corrupt-settings recovery** — unhandled exceptions
  are logged and, where recoverable, suppressed; a corrupt `user.config` is moved
  aside (timestamped) so the app starts fresh instead of crash-looping.
- Developer tooling: `tools/import_track_sections.py` and
  `tools/validate_track_catalog.py`.

### Changed

- **Relative gaps no longer flicker.** A hysteresis dead-band latches the ahead/behind
  side of dead-even cars, and a reject-and-hold latch kills lap-wrap flicker, so slots
  and gap signs stay steady during side-by-side battles.
- **Longer gaps on long tracks.** The position-history buffer grew from 30 seconds to
  4 minutes (10 Hz), so half-lap gaps on Le Mans and the Nordschleife resolve correctly
  instead of saturating — while staying lightweight (~2.4 MB across all 64 cars).
- Pit-road cars are shown on the relative display while you are on pit road.
- Config window modernized (layout and styling); the "Important" notice moved to the
  top with fixed text wrapping.
- Config window now centers deterministically on the primary screen at startup.
- Update availability is shown in the Config window instead of a modal dialog.
- Installer now closes a running VISOR before upgrading (Windows Restart Manager plus a
  matching app mutex) and wipes the previous version's files before installing, so
  orphaned DLLs and stale runtimes can't linger. User data under `%LOCALAPPDATA%\VISOR`
  is untouched.
- Incident-counter coloring is relative to the session's incident limit, so it means
  the same thing whether the cap is 4x or 25x.
- Version bumped to 1.0.0.0; README and user guide refreshed.

### Removed

- **Vehicle-health / pace / damage warnings.** iRacing exposes no reliable live
  "damage that matters" signal (repair time only reads once you're on pit road), so the
  inference produced nuisance warnings on inconsequential contact. The incident counter
  — direct, reliable telemetry — remains.

### Fixed

- RadarViewModel crash when parallel telemetry arrays had mismatched lengths.
- Lap-time fields now clear correctly on session transitions that keep the same
  `SessionNum`, and drop the stale latch on a new subsession.
- Parade-lap position scrambling, by gating green-flag position freezing on
  `SessionState`.
- Nullable-reference warnings (CS8602/CS8604) and an unused-variable warning; updated
  deprecated GitHub Actions.

[1.0.0]: https://github.com/phitz86/VISOR/releases/tag/v1.0.0
