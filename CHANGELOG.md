# Changelog

All notable changes to VISOR are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **Radar no longer leaves "ghost" cars behind** — after a disconnect, a session change or lone
  qualifying, the radar cleared its list of cars but left their shapes on screen. Those stale
  blocks reappeared at their old positions when the radar faded back in, and more piled up with
  every reset.
- **"Exit VISOR" works from a Config window opened with the overlay's ⚙ button** — that window
  wasn't connected to the app, so its Exit button did nothing. All Config windows are now opened
  the same way.
- **A second launch brings VISOR forward again after the Config window is closed** — VISOR kept
  trying to surface the closed Config window instead of the overlay, so pressing a Stream Deck
  launch button a second time did nothing. A radar window closed with Alt+F4 can likewise be
  reopened from the Config window.
- **Error pop-ups can't stack up over the sim** — an unexpected error still gets logged every
  time, but its dialog now appears at most once a minute and never while one is already open.
- **One bad entry in `Data\TrackSections.json` no longer disables the whole catalog** — malformed
  entries, blank match or config keys, and sections without a name or outside the lap are now
  skipped with a warning in the log. A blank config key could previously throw when that track
  loaded.
- **Shift-point model saves can't collide** — the periodic background save and the save on car
  change, disconnect or exit could write the same temporary file at once, and an older save
  could land after a newer one. Saves now take turns, and an older one is skipped.
- **"HUD ready" state is only announced when it changes** — it was re-announced on every
  session-info update.
- **Telemetry frames are handled strictly in order** — each frame used to reach the overlay and
  radar on its own background task, and a later frame could occasionally overtake an earlier
  one. That could cost a Final Lap or FINISHED latch, count a qualifying lap twice, reset the
  track-temperature trend, or glitch a gap or position for a frame. Frames are now queued for
  the display in the order iRacing sends them. If the display ever falls more than four frames
  behind, newer frames are skipped and noted in the log as `[FrameBacklog]`.
- **No waiting on the display during shutdown** — connection and session-state changes no longer
  block on the display, which could delay exit by up to two seconds.

### Removed

- **Unused code and settings plumbing** — about 50 unused members, events and helpers, and the
  unused `System.Management` package (one less DLL in the install folder).
- **Three debug-build loggers whose investigations are finished** — the relative-gap CSV, the 1 Hz
  telemetry CSV and the session-YAML dumps (iRacing's own `.ibt` recordings capture the same
  data). Release builds never ran them. The wet-research and shift-point loggers stay.
- **Stale planning files** — the old file plan, prompt and a raw session dump. The track-identity
  list used by `tools/validate_track_catalog.py` moved to `tools/`.

## [1.2.1] - 2026-10-04

### Added

- **Shift indicator** — the Row 0 gear symbol now tells you when to upshift, the same way in
  every car: gray normally, amber as the shift point approaches (starting the same RPM before it
  as the car's own lights), flashing white/red (5 times a second) at the shift point, and solid
  red at the redline or rev limiter. The cue allows about 0.15 s of reaction time: it comes early
  by however far RPM will climb in that time, so it's noticeably earlier in the low gears, where
  RPM rises fastest, and barely changes in top gear. It stays gray in neutral, reverse and the pits, and top gear only shows the
  limiter. It can be turned off in the Configuration Window.
- **Learned optimal shift points** — VISOR starts from iRacing's shift-light RPM for the car,
  then learns the fastest upshift for each gear from your own full-throttle acceleration (where
  the next gear would pull harder than the current one), and switches a gear over once it has
  seen enough of it. It needs no outside data: what it learns is kept locally per car in
  `%LOCALAPPDATA%\VISOR\ShiftModels`, builds up across sessions, and starts again when iRacing
  updates the car. Saved files are size-capped and validated on load, and anything malformed is
  discarded and relearned. Learning pauses in the pits, on the pit limiter, off track, in replays,
  on a wet track, during wheelspin and while cornering hard (above about 0.3 g sideways, where
  tire side load eats into forward acceleration). On ovals that means learning is slow, but you
  rarely shift there, and the indicator still works from iRacing's shift point.
- **Shift cue steps past an early light** — VISOR won't recommend revs it hasn't seen, so a car
  whose light comes on early could stay stuck on that light. When the data proves a gear still
  pulls harder than the next at the highest RPM seen, the cue now moves later in 250-RPM steps,
  each confirmed by driving up to it, until the real best shift point is learned. It never moves
  the cue earlier than the car's light on that evidence alone.
- **Calibration dot** — a small dot at the gear symbol's lower left shows whether the current
  gear's shift point has stopped moving: red while it's still being worked out, green once it has
  held steady (within 100 RPM) for three minutes of driving, hidden in neutral, reverse and top
  gear, and in any gear whose next gear up you haven't driven (e.g. 5th at a track where you
  never use 6th), since there's nothing to calibrate it against.
- **Gear ratios remembered per car** — saved with the learned model and reused for gears you
  haven't driven yet this session, but only once a gear you have driven confirms the gearing is
  unchanged (within 1%). Rarely used top gears no longer hold up learning of the gear below.
- **Learns from normal driving** — no full-range pulls needed. VISOR now finds the shift point
  by working down from the redline to the last RPM where the current gear still pulls harder, so
  a torque dip low in the rev range can't cause an early shift. It only needs to have seen the
  next gear from where an upshift lands, which corner exits provide. It also starts using data
  0.25 s after a shift instead of 0.5 s. If you never run a gear as low as the best shift would
  land you, that gear keeps the car's own light rather than guessing.
- **Quieter learning log** — the progress line drops from once a minute to once every 10 minutes
  after every gear has settled.
- **Shift-point learning progress in the log** — VISOR logs the car as soon as it's detected,
  then once a minute while driving: how many frames were used or skipped (and why: cornering,
  part throttle, wheelspin…) and what each gear is still waiting on (e.g. "6/10 RPM bands seen
  (missing 7375)").
- **Wet-track research logging (debug builds only)** — on a wet track, debug builds record
  60 Hz telemetry (where on track, conditions, inputs, accelerations and wheelspin signals, at
  every throttle level) to `Diagnostics\WetResearch`, as groundwork for a future grip-aware wet
  shift model. Release builds don't collect it.
- **Unit tests** (`Tests/VISOR.Tests`) for the shift-point learner and its storage, using a
  simulated car whose true optimal shift points are known.

### Changed

- **Version bumped to 1.2.1.0**; README, Config window and user guide updated to match.
- **Larger gear symbol** — the ⚙ grows from 54 to 68 pt, closer to the gear number, since it
  now doubles as the shift light.
- **New gear symbol** — the ⚙ is now drawn from a bundled copy of the Symbola font (v9.17, from
  before its 2018 license change, cut down to the single gear glyph). VISOR's text font has no
  gear, so Windows had been substituting Segoe UI Symbol's flower-like one.

## [1.2.0] - 2026-10-03

### Changed

- **Version bumped to 1.2.0.0**; README, Config window and user guide updated to match.
- **iRacing telemetry SDK upgraded to 2.5** (from 1.2.1) — the SDK now retries a frame that
  iRacing was still writing while it was being read, instead of passing on a half-updated
  sample, and reads session info as UTF-8 so accented driver names display correctly. VISOR's
  telemetry handling was moved to the SDK's new single monitoring call.
- **Telemetry keeps running if a window throws** — under the new SDK, an error escaping one of
  VISOR's telemetry handlers would stop telemetry until VISOR was restarted. Those handlers now
  catch and log such errors, and a disconnect still clears cached session data even if a window
  fails to handle it.
- **Dependency tidy-up** — dropped an unused `System.Diagnostics.PerformanceCounter` package
  reference (VISOR now uses the copy built into .NET, which is patched with the runtime) and
  updated `System.Management` to 10.0.12.

## [1.1.0] - 2026-10-03

### Security

- **Setup verifies the .NET runtime it downloads** — before running the runtime installer it
  fetched, Setup now checks that the file carries a valid Microsoft Authenticode signature, and
  refuses to run it otherwise. The download still comes from Microsoft's "latest patch" link, so
  new installs get the current patched runtime rather than a pinned version.

### Fixed

- **Setup can install the .NET runtime again** — Setup saved the runtime download under one file
  name and then looked for it under another, so on a PC without the runtime the install step was
  silently skipped and VISOR could not start until the runtime was installed by hand. Both now use the same name, and a missing
  download is reported instead of ignored.

### Added

- **Secondary session clock for lap-limited timed sessions** — standard open qualifying
  gives you a lap allowance *and* a deadline (e.g. 2 flying laps in 8 minutes), but the
  Row 1 readout could only show one of them. A smaller countdown now appears beneath the
  lap count, Final Lap, and FINISHED in sessions that are lap-limited and on a clock, so
  both are visible at once. It stays collapsed — costing no height — in every other session type, including lone
  qualifying and lap-limited races, which have no meaningful clock to show.

### Changed

- **Version bumped to 1.1.0.0**; README, Config window and user guide updated to match.
- **Moved to .NET 10** — VISOR now runs on the .NET 10 Desktop Runtime. .NET 8 reaches end of
  support on November 10, 2026, after which it no longer receives security fixes; .NET 10 is
  supported until November 2028. VISOR stays framework-dependent, so the installer is still a
  few megabytes and the runtime is patched by Microsoft rather than frozen inside VISOR. If
  .NET 10 isn't installed, Setup offers to download it as before.

- **Debug Mode now says what it actually does** — enabling it also appends your raw track
  position (0–1) to the track-location readout, a calibration aid for tuning section
  boundaries. The Config window only advertised verbose logging, so the overlay change came
  as a surprise. The hint text now mentions both.
- **Disconnected drivers are documented** — a car whose telemetry is missing for more than a few
  seconds is removed from VISOR's running order, so cars behind it move up a place, while iRacing's
  official results keep a disconnected driver classified on the laps they completed. Your overall
  position can therefore read better than the official result when a driver ahead of you leaves
  mid-race. This is existing behaviour, now described in the user guide; holding such cars at their
  last known position is a possible future change.
- **Finish-phase diagnostics** — the log now records every change in SessionState and in the
  green/white/checkered session flags, along with the leader's track position and laps
  completed at that instant, plus a line for the case where a car completes a lap under the
  checkered but isn't frozen. The timing between the flags changing and a car reaching the
  line is what the finishing-position logic depends on, and nothing in the log could show it.
  The same line now includes the player's own track position and lap (the white and checkered
  bits are raised per car, when that car passes a fixed point shortly before the line), the
  leader is reported correctly once frozen, and Final Lap / FINISHED latches are logged with the
  flags and lap that caused them, including the case where a flag lands on the very sample of a
  crossing. Each change of overall leader is also logged with the lap and track position that
  put the car there.

### Fixed

- **Finishing positions not held when cars leave at the checkered** — the lap-completed
  baseline used to detect a car crossing the line was seeded on the first checkered frame.
  iRacing flips the session state to Checkered *because* the leader crossed, so the
  leader's crossing landed in the same telemetry sample as the state change and was
  swallowed by that seeding. The leader never latched, the gate that starts freezing never
  opened, and nothing was frozen at all — so every car that logged out after finishing
  handed a free position to everyone behind it (P15 drifting up to P2 as the field left).
  The baseline is now tracked from the start of the session. As a second guarantee, a car
  that drops out of the session under the checkered now holds the slot it left on, which
  covers offline and AI races where cars disappear the instant they finish.
- **Multiclass finishing positions could start freezing too early** — the point at which
  cars start being frozen was triggered by the first car with a class position of 1 to
  cross under the checkered, rather than the overall leader. A slower class's leader
  crossing in the few seconds between the finish being signalled and the winner reaching
  the line could therefore freeze cars that were still racing. It now waits for the
  overall leader.
- **Single-frame full-lap position flicker at the line** — right at S/F, iRacing reports
  LapDistPct marginally outside 0–1 for a frame. The predictive fallback wrapped its
  estimate back to ~0 while the car's cached lap number was still the previous lap, so the
  car read a whole lap down and briefly dropped to the tail of the running order. The laps
  the prediction wraps through are now added back.
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
- **Final Lap / FINISHED latching hardened** — a white or checkered flag now only counts
  toward the readout on a start/finish crossing that happens *after* the flag was seen, not
  one in the same telemetry sample, and starting or reconnecting VISOR mid-race under a
  flag no longer latches on the very first frame. Race logs show iRacing raises these flags
  per car, well before that car's own line, so a same-sample collision is not expected in
  practice; this is a guard rather than a fix for an observed fault.
- **Missing car-number background in single-class sessions** — online Test sessions and
  offline custom races report every driver as `CarClassID 0`, which both the relative
  display and the radar treated as "no class" and left the number swatch transparent
  (on the radar, an empty black-outlined box). Class 0 is now resolved like any other
  class, falling back to the existing light grey when iRacing reports no class colour.
- **Radar car numbers unreadable on light class colours** — radar numbers were always white with a
  drop shadow, which disappears on a white, yellow, or light-green class fill (iRacing gives
  single-class sessions a pure white class colour). The number is now black on light fills and
  white with the shadow on darker ones. The relative display is unchanged.
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

[Unreleased]: https://github.com/phitz86/VISOR/compare/v1.2.1.0...HEAD
[1.2.1]: https://github.com/phitz86/VISOR/releases/tag/v1.2.1.0
[1.2.0]: https://github.com/phitz86/VISOR/releases/tag/v1.2.0.0
[1.1.0]: https://github.com/phitz86/VISOR/releases/tag/v1.1.0.0
[1.0.0]: https://github.com/phitz86/VISOR/releases/tag/v1.0.0
