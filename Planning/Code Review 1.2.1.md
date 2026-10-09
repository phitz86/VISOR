# VISOR 1.2.1 pre-release code review: findings

Scope: a line-by-line review of everything changed from 1.0 to 1.2.1 (`git diff 51e9f5b HEAD`, 53 files), plus a sweep of the whole codebase for file size, dead code, threading, performance and security.
Risk appetite for 1.2.1: fix bugs, delete dead code, and split files mechanically. Architecture changes go to 1.3.

## Summary

**Status (2026-10-09):** all three revision passes are done and validated on the rig (results under "Agreed revision plan"). Testing turned up three new items (B10, B11, S7). What remains is listed under "What's left" at the end.

- **Overall:** the code is in good shape. The new shift-point work (learner, store, cue) is well isolated, its untrusted input is validated, and it is the only part of the app with tests. Most problems are older code that grew, or plumbing nobody uses any more.
- **No significant CPU or RAM bottleneck.** Steady-state garbage is roughly 1 MB/s, all short-lived. Fixed buffers total about 2.4 MB. Nothing grows without bound in release builds except the radar ghost elements (B1).
- **No exploitable vulnerability.** The two items worth acting on are in the CI pipeline (S1, S2), not in the app.
- **Bugs:** five worth fixing before release (B1–B5), plus several small ones.
- **Dead code:** about 50 unused members, 8 data fields that are parsed but never read, and an unused NuGet package that ships a DLL.
- **File size:** four files are over about 500 lines. `PositionCalculator.cs` is 1,152 lines.

**Baseline**
- Debug and Release both build with **0 warnings and 0 errors**.
- The extra analyzer pass produced about 250 notes. Most are style (culture-specific formatting, missing `this.`-style rules). The useful ones are folded into the findings below.
- `dotnet list package --vulnerable`: **no known vulnerabilities**, and every package is current.
- Tests were not run, per your call.

Severity:
- **High:** likely to hit users in normal use, or can lose data.
- **Medium:** user-visible, but only under specific conditions.
- **Low:** latent, cosmetic or hygiene.

Risk class:
- **Mech:** code moves or deletion only; no behaviour change.
- **Small:** a local behaviour fix.
- **Retest:** touches timing or race logic, so it needs an on-track session.

---

## Findings at a glance

Status reflects the revision plan agreed after the review (see "Agreed revision plan" at the end).

| ID | Sev | Finding | Risk | Status |
|---|---|---|---|---|
| B1 | Med | Radar leaves "ghost" car shapes on screen after any reset | Small | **Done** (Pass 1) |
| B2 | Med | Config window opened from the overlay's ⚙: "Exit VISOR" does nothing, and a second launch can't bring VISOR forward | Small | **Done** (Pass 1) |
| B3 | Med | Telemetry frames can be processed out of order | Retest | **Done** (Pass 2) |
| B4 | Med | Installer upgrade wipes the whole install folder recursively | Small | **Done** (Pass 3) |
| B5 | Low–Med | Crash safety net shows a modal dialog for *every* error | Small | **Done** (Pass 1) |
| B6 | Low | One bad entry in TrackSections.json disables or misroutes the whole catalog | Small | **Done** (Pass 1) |
| B7 | Low | Two shift-model saves can collide on the same temp file | Small | **Done** (Pass 1) |
| B8 | Low | `PrimedStateChanged` fires on every session update, not only on change | Small | **Done** (Pass 1) |
| B9 | Low | Possible 2 s hang on exit (thread-blocking pattern) | — | **Done** (Pass 2) |
| B10 | Med | Session info the SDK can't parse leaves VISOR blank for a whole event | Small–Med | **Open:** Debug capture added; safety net before release or in 1.3 is your call |
| B11 | Med | Settings reset to defaults on every version bump | Small | **Open:** fix before the next version bump |
| D1 | Low | `System.Management` package unused but shipped | Mech | **Done** (Pass 1) |
| D2, D4 | Low | Unused members and events | Mech | **Done** (Pass 1) |
| D3 | Low | Session data parsed but never read | Mech | **Skipped** (cheap; unused reads have come in handy) |
| D5 | Low | Stale files in `Planning/` and the repo root; dead csproj entries | Mech | **Done** (Pass 1), except the stale PDF, which you regenerate before release |
| C1–C3, C7 | Low | Duplicated helpers, scale factors, magic numbers | Mech | Open (mechanical tidy-ups) |
| C5 | Low | Fastest-lap positioning rebuilt per row | Mech | **Done** (as P2) |
| C4, C6, C8 | Low | Larger consolidations | — | Deferred (architecture work) |
| F1–F4 | — | Files over 500 lines: split plans | Mech | Open: F2–F4 mechanical; F1 needs T2 |
| A1–A5 | — | Separation-of-concerns items | — | Deferred until after the CI pipeline work |
| P1–P4 | Low | Small per-frame waste (brushes, list copies, log I/O, notifications) | Small | **Done** (Pass 3) |
| P5 | — | Measure the transparent-window rendering cost on your rig | — | **Skipped** |
| S1 | Med | CI token has write access during the build job | Small | **Done** (Pass 3) |
| S2 | Med | CI actions and Inno Setup not pinned to fixed versions | Small | **Done** (Pass 3) |
| S3, S4 | Low | Log privacy, license notice | Small | **Done** (Pass 3) |
| S5 | Low | Named-object squatting | — | **Accepted** |
| S6 | Low | Installer `DelTree` scope | — | **Done** (with B4) |
| S7 | Low | Inno Setup prints "Non-commercial use only" | — | **Open:** check the licence terms (yours) |
| T1 | Med | CI never builds or runs the tests | Small | Deferred to the CI pipeline discussion |
| T2 | Med | No tests for PositionCalculator (needed before splitting it) | Mech | Deferred to the CI pipeline discussion |

---

## Bugs

### B1: Radar "ghost" cars (Medium; pre-1.0 code)
`RadarViewModel.Reset()` (`ViewModels/RadarViewModel.cs:445`) clears its dictionary of car shapes but never removes them from the `CarsContainer` canvas. Reset runs on disconnect, when VISOR stops being "primed", when session data isn't ready, and every frame in lone qualifying.

After a reset, the old rectangles and numbers stay frozen where they were. When cars come back, new shapes are drawn on top. On the next fade-in the stale blocks show up, and every reset adds more orphaned elements, so memory and render cost creep up over a long evening of session hopping.

**Fix:** remove the elements from the canvas in Reset. Structurally, the shapes belong in the window, not the view model (see A1).
**Verify on rig:** join a session, leave it, join another; no stuck car blocks on the radar.

### B2: Config window opened from the ⚙ button (Medium; pre-1.0)
The overlay's ⚙ and ✕ buttons are always visible. ⚙ creates a new ConfigWindow (`Views/MainWindow.xaml.cs:244`) without the wiring App gives the startup instance (`App.xaml.cs:277-281`). That has two effects:

- In that window, **"Exit VISOR" does nothing.** Its `ExitRequested` event has no listener.
- `App._configWindow` keeps pointing at the startup window after it closes. `BringToForeground` (`App.xaml.cs:224`) then calls `Show()` on a closed window, which throws; the error is caught and logged. A second launch (e.g. pressing a Stream Deck button again) therefore stops bringing VISOR forward once the first config window has been closed with Done, which is the normal state while racing.

**Fix:** make App the only place that opens the config window (one `App.ShowConfigWindow()` that wires events and tracks the current instance). Fall back to the main window when no config window is open.

### B3: Frames can be processed out of order (Medium; High if the log canaries below appear)
Each telemetry frame gets its own `Task.Run` (`Telemetry/SVappsLABSDKWrapper.cs:334`). That thread-pool thread then blocks in `Dispatcher.Invoke`, once for the overlay and once for the radar (`Views/MainWindow.xaml.cs:178`, `Views/RadarWindow.xaml.cs:282`).

Nothing guarantees frame N reaches the UI thread before frame N+1. If the thread carrying N is pre-empted for a moment (iRacing keeps the CPU busy), N+1 overtakes it. The comment in `ShiftCue.cs:54` shows this has already been seen: ShiftCue guards against it, but nothing else does.

Possible symptoms when it happens:
- **Final Lap missed.** `CountdownViewModel` reads an older lap number as "lap counter regressed, session restart" (`CountdownViewModel.cs:152`) and clears the pending white/checkered flag. The next frame then counts the crossing without the flag.
- **Qualifying lap counter skips.** The same regress-then-advance pair counts one flying lap twice (`CountdownViewModel.cs:187`).
- **Track-temp trend wiped.** `TrackTempViewModel` sees time go backwards and resets its 3-minute history.
- **Position or gap glitches.** The lap-desync correction and the gap ring buffer both assume each frame follows the previous one.

**Log canaries.** Search your existing logs for these lines in the middle of a session:
- `[TrackTemp] Session time went backwards`
- `[Countdown] White flag raised on the same sample`
- `Checkered flag raised on the same sample`

Seen outside a genuine session reset, they are this bug.

**Fix (small, contained):** raise `SnapshotAvailable` directly on the SDK's single stream thread, with no `Task.Run`, and have the windows queue the work with `Dispatcher.BeginInvoke`, which doesn't block. One producer posting to one dispatcher keeps frames in order. The SDK thread's cost stays at a few microseconds per frame, and no pool threads sit blocked. Add a small "frames pending" cap so a stalled UI drops frames instead of queuing them. This also removes the exit-hang pattern in B9.

It changes only *when* frames arrive, not any race logic. Even so, because the finish and lap logic depends on it, treat it as **needs an on-track retest**.

### B4: Installer upgrade wipes the install folder (Medium; added 2026-06-28, after 1.0)
`CurStepChanged` runs `DelTree({app})` recursively whenever `{app}\VISOR.exe` exists (`VISOR-Setup.iss:339-347`). That is fine for `C:\Program Files\VISOR`. But if someone ever installed into a shared folder (e.g. typed `C:\Games` on the directory page), an upgrade deletes **everything in that folder**. Every upgrade also discards edits to `Data\TrackSections.json`, which the README invites users to make.

**Fix:** delete only VISOR's own files: `[InstallDelete]` entries for `*.dll`, `*.json`, `runtimes\`, `VISOR.exe` and so on, or a list written at install time. Note in the README that catalog edits are overwritten on upgrade. A user override file is a possible 1.3 feature (A5).
**Verify:** an upgrade install over 1.2.1 on Windows.

### B5: Modal dialog for every UI error (Low–Medium)
`OnDispatcherUnhandledException` (`App.xaml.cs:121-135`) logs the error, keeps VISOR alive (good) and shows a modal MessageBox. An error that repeats every frame would stack dialogs over the sim at 60 per second.
**Fix:** show the dialog once per session (or at most once a minute) and log every occurrence.

### B6: Fragile catalog loading (Low)
In `Data/TrackSectionCatalog.cs`, a single entry with `"match": null` or `"configs": null` throws inside `Load()` (line 138), and the whole catalog is dropped. An empty match key `""` matches *every* track (line 68). The shipped file passes `tools/validate_track_catalog.py`, so this only bites people who edit it.
**Fix:** validate each entry, skip bad ones with a log warning, and reject empty keys.

### B7: Shift-model save collision (Low)
The periodic background save (`ShiftPointProvider.ScheduleSave`) and the synchronous `Flush()` (on car change, disconnect or exit) can both write `<car>.json.tmp` at once (`ShiftModelStore.cs:151`). One of them fails with an IOException, which is caught and logged. If the one that fails is the final flush on exit, up to a minute of learning is lost.
**Fix:** a save lock in `ShiftModelStore`, or a unique temp name per save.

### B8: "Changed" event that fires without a change (Low)
`CheckPrimedStateChange` (`SVappsLABSDKWrapper.cs:220-239`) only logs when the primed state changes, but raises `PrimedStateChanged` on every session-info update. That is several times a minute in races. Both windows tolerate it today (the overlay re-runs its "Fully connected!" status logic each time).
**Fix:** raise the event only on a change.

### B9: Possible exit delay (Low; fixed by B3)
On exit, `Shutdown()` waits up to 2 s on the UI thread for the SDK task. Meanwhile an SDK handler can be blocked in `Dispatcher.Invoke`, waiting for that same UI thread.
Canary: `Run task did not shut down gracefully` in the log.

### B10: Session info the SDK can't parse leaves VISOR blank (Medium; found 2026-10-09, during Pass 3 testing)
In one official race (GT4 Challenge at Road Atlanta, 9 October), every session-info update failed inside the SDK: 17 of 17 over two minutes, through the end of practice and into the next session. Each failed with `While scanning a multiline plain scalar, found invalid mapping`. VISOR never received session data, so the HUD never became ready, and nothing on screen says why.

- **Not caused by the passes.** The SDK parses session info before VISOR sees it, 1.2.1 ships the same SDK, and the same build worked in every other session that day.
- **What breaks it.** That exact error is what YamlDotNet raises for a value with a line break inside it (reproduced). The SDK's repair quotes six name fields one line at a time, so it can't fix a value split across lines; that is why both of its attempts failed.
- **Ruled out:**
  - the network (session info is read from shared memory on the PC)
  - the perf-logging tools
  - the car: a Mercedes-AMG GT4 test session parsed cleanly
  - the race drivers' names (results CSV)
- **Most likely** something in that event's practice field (drivers who left before the race, spectators) or its event details. It couldn't be reproduced.
- **Done:** Debug builds save any session info that fails to parse to `Diagnostics\SessionYaml`, with the error position and an excerpt in the log (commits ca9502c and 628f4a9).
- **Proposed fix:** when the SDK's parse fails, VISOR rejoins values split across lines, quotes values, and parses the result itself; report it to SVappsLAB as well. It can be tested with made-up samples of the confirmed failure, which makes it a good first test for the CI pipeline. **Decision pending:** before release or in 1.3.

### B11: Settings reset on every version bump (Medium; pre-1.0)
`UserSettings` uses .NET's standard settings store, which keeps `user.config` in a folder named after the assembly version. Nothing calls `Upgrade()`, so after a version change (for example 1.2.1.0 → 1.3.0.0) VISOR starts from defaults: window positions and every option in the Config window. Today's upgrade test didn't show it because the version is still 1.2.1.0.

- **Fix:** the standard pattern. Add an `UpgradeRequired` setting (default true); at startup, if it's true, call `Upgrade()`, set it to false and save.
- **Confirm first:** under `%LOCALAPPDATA%`, a VISOR settings folder with one subfolder per past version means each earlier upgrade started fresh.
- **Verify:** bump the version locally, install over the current build, and check that settings survive.

### Checked and found sound
- **ShiftPointProvider threading:** the lock discipline is correct, and the one write outside the lock (`_car`) is a harmless reference swap.
- **SDK arrays:** they are fresh per frame (the SDK doesn't pool them), so off-thread reads can't see a half-updated frame.
- **Handler guards:** every SDK handler catches its own exceptions.
- **Settings and window placement:** corrupt-settings recovery and off-screen window clamping both work.
- **Shift learner maths:** the crossover interpolation and Cholesky guards are correct.
- **Gap history buffer:** array bounds are correct.
- **Countdown:** the "flag must already be flying" latch logic is correct, apart from the ordering issue in B3.

---

## Dead code and dead ends (all **Mech**, bucket 1.2.1)

### D1: Unused NuGet package
Nothing references `System.Management` (`VISOR.csproj`), yet it ships `System.Management.dll`. I built a copy without it: clean build, and the DLL is gone from the output. Remove it.

### D2: Settings plumbing nobody listens to
In `Settings/SettingsManager.cs`:
- The `SettingsChanged`, `RadarVisibilityChanged` and `PositionDisplayModeChanged` events have no subscribers.
- Their EventArgs classes, the `SettingsChangeType` enum, and every payload field of `ElementVisibilityChangedEventArgs` / `WindowSizeChangedEventArgs` are never read.
- The main-window branch of `GetBaseDimensions` and `MAIN_WINDOW_HEIGHT_LARGE` are unused.

In `UserSettings`: `ReloadSettings`, `ResetToDefaults`, `GetRowVisibility` and `SetRowVisibility` are unused.

### D3: Session data that is parsed and never read
Unused `SessionDataCoordinator` methods:
- `GetStaticEventData`, `GetLiveSessionData`, `GetSessionSchedule`
- `GetCurrentSessionType`, `GetCurrentSessionName`
- `GetCurrentSessionResultsPositions`, `GetSessionResultsPositions`
- `GetCurrentSessionFastestLaps`, `GetSessionFastestLaps`
- `GetTrackDisplayShortName`

`GetStaticEventData` and `GetLiveSessionData` also hand out mutable internals past the lock, so deleting them closes a thread-safety hole.

Unused `ISessionDataProvider` members: `CarNumberRaw`, `CarClassEstLapTimes` and `GetQualifyResultsFastestTimes`. The interface comment still mentions a "reference lap time cascade" that no longer exists.

Data parsed on every session update and never read:
- fastest-lap results
- qualifying fastest times
- `EventType`
- `CurrentSessionType` / `CurrentSessionName`
- `ResultPosition.Lap`, `.Time` and `.LastTime`
- `DriverInfo.CarNumberRaw` and `.CarClassEstLapTime`

### D4: Other unused members
- **SDK wrapper and snapshot:** `SVappsLABSDKWrapper.GetSnapshot`/`_latestSnapshot`, `Name`, and the `Task.Delay(200)` in `Initialize`. `SVappsLABSnapshot.IsValid` (always true) and `Timestamp`.
- **PositionCalculator:** `HasEverHadValidData`, `_isCurrentlyPredicting` (written, never read), `CarPositionData.TrackPosition`, and the unused `sessionDataProvider` parameter of `DetectSessionTransition`.
- **Radar:** in `RadarViewModel`, `RADAR_HEIGHT`, `CANVAS_CAR_POSITIONS`, the unused `userNames` local, and `RadarCarData.IsAhead`. In `RadarWindow`, four unused parameters and one unused local.
- **Relative display:** `RelativeViewModel.OnPropertyChanged` (it never raises, so it doesn't need `INotifyPropertyChanged`). `RelativeRowViewModel.IncidentCount` is set for every row each frame but never displayed.
- **Colours:** in `ClassColorManager`, `HasColorAssignment`, `GetAllAssignments` and `AssignedClassCount`. `ProximityToWidthConverter` is declared in `MainWindow.xaml` but never used.
- **Shift learner:** `PlayerCarInfo.HasShiftLights`, `ShiftPointLearner.GetBinWeight`, and a dead `prevR` variable in `ShiftPointLearner.SolveGear`.
- **Elsewhere:** `ConfigWindow._telemetry`. `Log.EnableFileLogging` and `EnableDebugOutput` are never changed. `TelemetryCSVLogger` and `SessionDataLogger` have unused parameters.

Test-only members to **keep**: `ShiftPointLearner.RelativeTorqueAt` and `ShiftCue.RpmRate`.

### D5: Repo hygiene
- **`Planning/`:**
  - `File Plan.txt` and `Generic Prompt.txt` are from the .NET 8 beta.
  - `IRacingSessionInfo-20250917-120929.yaml` is a raw session dump with your name and iRacing customer ID. It's your own data, but there's no need to publish it; note it stays in git history.
  - `iracing-track-identities.json` is input to `tools/validate_track_catalog.py` and belongs next to it in `tools/`.
- **`VISOR User Guide.pdf`** (repo root) dates from April 2026, before 1.0, and is superseded by `VISOR_User_Guide_1.2.1.0.html`. Regenerate it or remove it.
- **`VISOR.csproj`** excludes `Archive\` and `Raw outputs\` folders that no longer exist.
- **`SessionDataLogger`** is compiled into Release builds but only used in Debug. Wrap it in `#if DEBUG` like the other loggers, or remove it (see the debug-tooling table).

---

## Consolidation and verbosity

| ID | What | Where | Bucket |
|---|---|---|---|
| C1 | Car-path → file-name sanitising written three times | `ShiftModelStore.SafeFileStem`, `ShiftPointLogger`, `WetResearchLogger` | 1.2.1 |
| C2 | Size-preset scale factors defined three times (0.6/0.8/1.0 main, 0.8/0.9/1.0 radar) | `MainViewModel.ScaleFactor`, `SettingsManager`, `RadarViewModel.GetScaleFactor` | 1.2.1 |
| C3 | Magic numbers: pace-car class `11`, SessionState `4/5/6`, finish-flag mask `0x7` | `PositionCalculator`, `RelativeDisplayBuilder`, `MainViewModel`, `CountdownViewModel` | 1.2.1 |
| C5 | `GetFastestLapPositioning()` rebuilt up to 8× per frame in practice/qualifying (once per relative row, once for the player) | `RelativeDisplayBuilder.cs:316`, `MainViewModel.cs:172` | 1.2.1 (also P2) |
| C7 | Radar zone assignment: five near-identical switch blocks | `RadarViewModel.UpdateZoneAssignments` | 1.2.1 (optional) |
| C4 | Session type (Practice/Qualify/Lone/Race) re-derived by string matching under a lock about 25× per frame. Not a speed problem (microseconds); it's clarity. Compute once per session-info parse. | `SessionDataCoordinator` + callers | 1.3 |
| C6 | Overlay and radar code-behind duplicate their connection, config-mode and drag handling | `Views/*.xaml.cs` | 1.3 (with A1) |
| C8 | Debug loggers each re-implement folder, file name and flush | `Diagnostics/*` | 1.3, if they're kept |

---

## File size (target: about 500 lines of C#)

| File | Lines | Split (code moves only; public API unchanged) |
|---|---|---|
| `ViewModels/PositionCalculator.cs` | 1152 | `FinishTracker` (freeze at checkered, departed cars, finish diagnostics), `CarTrackingCache` (valid roster, prediction, lap-desync correction), `RunningOrder` (sort, grid fallback, slot assignment). `PositionCalculator` stays as the thin front door the view models already call. Do **T2** first. |
| `Telemetry/ShiftPointLearner.cs` | 648 | Records and enums (`ShiftSample`, `GearShiftEstimate`, `SkipReason`, `ShiftModelState`) → `ShiftModelTypes.cs`. `RatioTracker` → own file. Cholesky solver → `LinearSolver.cs`. |
| `ViewModels/ShiftPointProvider.cs` | 626 | Calibration/stability tracking → `ShiftCalibrationTracker`. Progress-log formatting → `ShiftProgressLog`. What remains is lookup plus learning orchestration. |
| `ViewModels/RelativeDisplayBuilder.cs` | 576 | Removing the relative-gap debug plumbing (about 50 lines; see the debug table) and the brush cache (P1) get it near 500. Then move row styling and segment colours into `RelativeRowStyler`. |
| `Tests/.../ShiftPointLearnerTests.cs` | 501 | Soft limit; leave it. |
| `Views/MainWindow.xaml` | 476 | XAML; leave it. |

---

## Architecture and separation of concerns (bucket 1.3 unless noted)

- **A1: the radar view model builds WPF elements.** `RadarViewModel` creates `Rectangle`/`TextBlock` objects and moves them on a `Canvas` passed in by the window. Rendering should belong to the view: have the view model publish car positions and colours, and let `RadarWindow` (or an `ItemsControl` over a `Canvas`) draw them. This also makes B1 impossible to reintroduce.
- **A2: leaky session interface.** Four consumers downcast `ISessionDataProvider` to `SessionDataCoordinator` to get track length, track name or lone-qualifying status: `SettingsManager`, `RadarViewModel`, `PositionHistoryManager` and `TrackLocationViewModel`. Because of `SettingsManager`, the **Settings layer depends on the Telemetry layer**. Add those members to the interface (or publish one immutable per-session snapshot, which also fixes C4). Settings should take a plain "relative visible?" bool.
- **A3: PositionCalculator is in the wrong layer.** It is race-domain logic that lives in ViewModels and reads the SDK-backed snapshot directly, which is why it can't be unit-tested. Part of this is done in 1.2.1 under T2/F1: it gets a plain input record. Moving it to `Telemetry/` (or a `Race/` folder) is the 1.3 step.
- **A4: frame pipeline.** B3 fixes ordering. The fuller design is one UI tick per frame that updates both windows from one immutable state.
- **A5: catalog lives in Program Files.** The user-editable `TrackSections.json` sits under Program Files: it needs admin rights to edit and is overwritten on upgrade. Support an override file in `%LOCALAPPDATA%\VISOR\`.
- **Note:** the Settings singletons are created lazily without locking. That is fine today, because every access is on the UI thread (verified). Keep it that way.

---

## Performance, RAM and CPU

Verdict: **no significant bottleneck.** The items below are cheap tidy-ups, not problems you'd feel.

- **P1 (Low; 1.2.1): brushes allocated every frame.** `RelativeDisplayBuilder` allocates up to five new `SolidColorBrush` objects per relative row per frame (lines 494–508), about 1,800 a second, plus a new thresholds array per row. There are only 10 distinct colours: cache them as frozen brushes.
- **P2 (Low; 1.2.1):** compute the fastest-lap positioning once per frame (C5).
- **P3 (Low; 1.2.1): log file I/O.** `Log.cs` opens and closes the log file, and stats it, for every line (lines 221–227). Use one persistent `StreamWriter` with periodic flushes. This mainly matters with Debug Mode on.
- **P4 (Low; 1.2.1): delta bar notifications.** `DeltaBarViewModel` raises two property-changed notifications every frame, even when nothing changed (no equality check). Each one triggers a converter and a layout pass at 60 Hz.
- **Tidy during the splits:** per-frame LINQ and list allocations in `PositionCalculator` and `RelativeDisplayBuilder`.
- **P5 (measure, don't change): transparent windows.** Both overlays are transparent windows (`AllowsTransparency`) redrawn at 60 Hz. WPF composes those through a slower path than normal windows, so this is probably VISOR's single biggest real cost. Measure it on your rig: Task Manager CPU and GPU for VISOR over about 30 minutes, with the radar on and then off. Only if it's notable, consider updating the radar and relative display at 30 Hz in 1.3.
- **Memory:**
  - The fixed ring buffers total 2.4 MB.
  - Per-car dictionaries are capped at 64 entries.
  - The class-colour cache is capped at the number of classes.
  - The fuel history grows by one small entry per lap (negligible).
  - Debug builds only: the diagnostics folders are never cleaned, and the wet-research CSV alone runs about 40 MB an hour of wet running.

---

## Security

Attack surface: local files (settings, shift models, track catalog, logs), one HTTPS GET to the GitHub API, and the installer's runtime download. **No exploitable issue found in the app.**

- **S1 (Medium; 1.2.1): CI token scope.** `permissions: contents: write` is set for the whole workflow (`.github/workflows/build.yml:27`), so the **build job** holds a write token while it runs third-party build code: NuGet packages, including the SDK's source generator, and a Chocolatey install. Set the workflow default to `contents: read` and grant `write` only to the `sign` job, which attaches the release.
- **S2 (Medium; 1.2.1): unpinned actions and tools.**
  - `signpath/github-action-submit-signing-request@v1` and `softprops/action-gh-release@v2` run in the job that holds the SignPath API token and write access. They are referenced by movable tags, so whoever controls those repos controls what runs there. Pin them (and the first-party actions) to commit SHAs, and let Dependabot propose updates.
  - `choco install innosetup` is unversioned; pin a version.
- **S3 (Low; 1.2.1): log privacy.** Log headers record the machine name (`Log.cs:114`), and paths in logs include the Windows user name. People post logs in GitHub issues. Drop the machine name; optionally replace the user-profile path with `%USERPROFILE%` when writing.
- **S4 (Low; 1.2.1): license notice.** The imported lovely-track-data is CC BY-NC-SA 4.0. That is credited in the README and in the catalog's `_readme`, but not in `LICENSE.txt`'s third-party section, where the Symbola font is listed. Add a matching line. The NC (non-commercial) term is worth a moment's thought given CephasMedia is a company, though a free GPL tool should be fine.
- **S5 (Low; accept):** the `Global\` mutex and event can be opened or pre-created by any local process (to signal "come to front", or squat the mutex so VISOR won't start). It's needed for the installer's `AppMutex`, and a local attacker could simply kill the process anyway. Accept.
- **S6 (Low):** the installer's `DelTree` scope, already covered as B4. The catalog validation in B6 is about robustness, not privilege, since the file needs admin rights to edit.
- **S7 (Low; found in the CI log):** the installer compiler (Inno Setup 6.7.1) prints `Non-commercial use only`. VISOR is free and GPL, but it is published under a company name (CephasMedia). Check Inno Setup's current licence terms (jrsoftware.org) for whether that counts as commercial use. Not a code issue.

**Checked and found sound**
- **ShiftModels loading:**
  - 2 MB size cap.
  - Strictly typed JSON.
  - Schema, shape, range and finiteness checks.
  - Car build and version must match.
  - File names are reduced to `[A-Za-z0-9_-]`, which rules out path traversal.
  - Atomic temp-file-then-move writes.
- **UpdateChecker:**
  - HTTPS to a fixed GitHub API URL, with a 10 s timeout.
  - Only `tag_name` is read, and parsed with a strict version regex.
  - The URL it opens is a constant.
- **Installer runtime download:**
  - Authenticode must be `Valid` and signed by Microsoft Corporation, or nothing runs (fails closed).
  - The PowerShell path is quoted safely, and `{tmp}` is a private folder.
- **Other checks:**
  - The "open logs folder" button passes a quoted path to explorer.
  - There are no secrets in the repo.
  - No NuGet package has a known vulnerability, and all are current.

---

## Tests and CI

- **T1 (Medium; 1.2.1):** CI never builds or runs `Tests/VISOR.Tests`, and the project isn't in `VISOR.sln`. A change to the shift learner can break the tests without anyone noticing. Add a `dotnet test Tests/VISOR.Tests` step. It targets plain `net10.0`, so it adds well under a minute.
- **T2 (Medium; 1.2.1):** the position calculator has more changelog fixes than anything else and no tests. Before splitting it (F1), add *characterization tests*, which record what it does today:
  - lap-desync correction
  - grid-source selection
  - leader-gated finish freezing
  - departed-car holds
  - pace-car exclusion

  The tests must pass unchanged after the split. To make it testable, `PositionCalculator.Update` takes a small plain input record (the per-car arrays and scalars it reads) that the overlay fills from the snapshot. That is a signature change only; the logic stays the same.

---

## Debug-only tooling: verdicts and decisions

| Tool | Build | What it's for | Status | Recommendation |
|---|---|---|---|---|
| `WetResearchLogger` | Debug | 60 Hz wet-track data for a future wet shift model | Open research | **Keep** |
| `ShiftPointLogger` | Debug | Learner samples, fits and curves, for comparing cars | Feature just shipped, still tuning | **Keep** for now |
| `RelativeGapLogger` + `LogDiagnosticRow` | Debug | Relative-gap flicker investigation | Fixed in 1.1 (reject-and-hold) | **Remove.** Saves about 50 lines in `RelativeDisplayBuilder`. *Decided: remove (Pass 1).* |
| `TelemetryCSVLogger` | Debug | 1 Hz per-car telemetry CSV | iRacing's own `.ibt` recordings capture the same data at 60 Hz; also not thread-safe (called from pool threads) | **Remove.** *Decided: remove (Pass 1).* |
| `SessionDataLogger` | Release-compiled, Debug-used | Raw session YAML dumps | iRacing `.ibt` files include the session YAML too | **Remove**, or gate with `#if DEBUG` if you use it for fixtures. *Decided: remove (Pass 1).* |
| Finish diagnostics (`[Finish]`, `[Leader]`, latch and freeze lines) | **Release**, Info | Finishing-position investigation (1.1) | Cheap, but about 100 lines of `PositionCalculator` | **Keep for one more release**, then demote to Debug once a few real race finishes look right. *Decided: keep.* |
| Frame-gap / handler-latency detectors | Release | Telemetry health | Cheap, useful | **Keep** |
| Debug Mode LapDistPct readout | Release (user-visible) | Catalog calibration | Useful | **Keep** |
| `[ShiftPoint] progress` log | Release, Info | Learner progress | Already throttled to 10 min once settled | **Keep** |

---

## Agreed revision plan: three gated passes

Each pass:
- builds Debug and Release with 0 warnings and 0 errors before it is pushed to `claude/zen-carson-2vx0dk`
- adds `[Unreleased]` entries to `CHANGELOG.md` (the version bump is left until release)
- **stops** for compile-and-test on your rig before the next pass starts

### Pass 1: dead code, three debug loggers, small bug fixes (one commit)

- **Dead code:** D1, D2, D4 and D5 (not the PDF).
  - `RelativeRowViewModel.IncidentCount` stays: it is the same kind of unused read as D3.
  - Also kept on purpose:
    - `ShiftPointLearner.RelativeTorqueAt` and `ShiftCue.RpmRate`, which the tests use.
    - `RelativeViewModel`'s `INotifyPropertyChanged`, which avoids WPF's binding memory leak and a CS0067 warning.
- **Loggers removed:** RelativeGap, TelemetryCSV and SessionData, including the raw session-YAML stash that only SessionData used.
- **Bug fixes:** B1, B2 (including the same lifetime fix for a radar window closed with Alt+F4), B5, B6, B7, B8.
- **Rig results:**
  - Verified: Exit from the ⚙ window, the radar toggle, track location (66 entries loaded), shift points, and the debug-build folders.
  - Track location: Oulton Park Fosters shows no readout because that layout isn't in the catalog, as expected.
  - WetResearch wasn't exercised (dry race), and its code is unchanged.
  - Radar ghosts moves to the Pass 2 checklist, and the second-launch check to Pass 3.
- **Rig checklist:**
  - **Radar ghosts:** leave a session and join another; no stuck car blocks on the radar.
  - **Exit from the ⚙ window:** close the config window with Done, click ⚙ on the overlay, then **Exit VISOR**; VISOR closes.
  - **Second launch:** with the config window closed, launch VISOR again (or press the Stream Deck button); the running instance comes to the front.
  - **Radar toggle:** turn the radar off and on.
  - **Track location:** the readout still resolves, and the log shows `[TrackSections] Loaded 66 track entries`.
  - **Shift points:** shift models still save, and the indicator and calibration dot behave as before.
  - **Debug build only:** no `Telemetry`, `RelativeGap` or `SessionData` folders under `%LOCALAPPDATA%\VISOR\Diagnostics`; `ShiftPoints` and `WetResearch` are still written.

### Pass 2: ordered frame delivery (B3, with B9; one commit)

- **Frame delivery:** snapshots are raised on the SDK's single telemetry thread and posted to the UI with a non-blocking `BeginInvoke` at the same priority as today. That keeps frames in order, and a capped backlog drops frames rather than queueing them when the UI stalls; drops are logged as `[FrameBacklog]`.
- **Exit hang (B9):** the connection-state and primed-state handlers stop blocking too.
- **Rig results:**
  - A full practice → qualifying → race at Road Atlanta (multiclass) showed no issues.
  - Radar ghosts didn't show up. That case is hard to reproduce on purpose, so keep an eye out.
  - `[FrameBacklog]` and `[FrameGap]` appeared together twice, at session loads. That is expected: iRacing sends a burst of frames after a load stall.
  - One `Session time went backwards` in practice and none in the race. Since frames now arrive in order, this means iRacing's own session clock went backwards (for example during a replay or a session restart).
- **Rig checklist:**
  - A full practice → qualifying → race through the checkered: Final Lap and FINISHED latch, finishing positions hold, and the qualifying lap countdown is correct.
  - Carried over from Pass 1: leave a session and join another; no ghost cars on the radar.
  - Relative gaps and positions are steady.
  - Exit is prompt.
  - Logs show none of `Session time went backwards`, `raised on the same sample` or `did not shut down gracefully` mid-session; `[FrameBacklog]` is rare or absent.
  - CPU is comparable to Pass 1.

### Pass 3: performance, security and docs, installer (one commit per theme)

- **Performance:** P1 (cached frozen brushes), P2 (fastest-lap lookup once per frame), P3 (persistent log writer), P4 (delta bar change checks).
- **Security and docs:**
  - S1: the CI build job gets read-only access; only the sign job can write.
  - S2: actions pinned to commit SHAs, a pinned Inno Setup version, and Dependabot for GitHub Actions.
  - S3: no machine name in the log header, and `%USERPROFILE%` in place of the profile path.
  - S4: the lovely-track-data license notice in `LICENSE.txt`, and a README note that catalog edits are replaced on upgrade.
- **Installer:** B4. The recursive `DelTree` goes, replaced by targeted `[InstallDelete]` entries for VISOR's own DLLs, `deps.json`/`runtimeconfig.json` and `runtimes\`.
- **Rig results** (installer from the PR #42 CI build):
  - **Installs:**
    - The upgrade over 1.2.1 was clean. `System.Management.dll` and the `runtimes\` folder are gone, and the eight DLLs left match the Release build exactly.
    - A fresh install (uninstall, then reinstall) was clean too, and settings survived it.
  - **Second launch:** it brings the Config window forward when that window is open. With it closed, the overlay is the target and is already on top, so nothing visibly changes; that is by design.
  - **Log:**
    - The header has no machine name, and paths show `%USERPROFILE%`.
    - A session with Debug Mode on logged cleanly.
    - Opening the log while VISOR runs was dropped from the checklist (not needed).
  - **Performance** (typeperf, 5 s samples; 46 min with Debug Mode off, 27 min with it on):
    - On track, VISOR used about 17–18% of one core in both runs; on a 16-thread CPU that is roughly 1% of the whole CPU. Off track it used about 3%.
    - Memory levelled off around 125 MB, and threads held steady at 19–22.
    - Debug Mode had no measurable cost.
  - **Other:** a clean race on the installed build, and CI green with the pinned actions.
  - **Found during testing:** B10 (session info that won't parse), which the passes didn't cause.
- **Rig checklist:**
  - Carried over from Pass 1: with the config window closed, launch VISOR a second time; the running instance comes to the front.
  - Clean the build output first (Build → Clean Solution, or delete `bin\` and `obj\`). Otherwise a stale `bin\Release\...\runtimes\` folder from older builds still holds `System.Management.dll`, and the installer packs every DLL under `bin\Release`.
  - Compile the installer.
  - Do a fresh install and an upgrade over 1.2.1. Settings, logs and shift models survive; there are no stale DLLs and no `System.Management.dll`.
  - The overlay and radar behave as before.
  - The log has no machine name, and it can be opened while VISOR runs.
  - CPU and memory over about 30 minutes, compared with Pass 2.
  - CI is green with the pinned actions.

### What's left (updated after Pass 3, 2026-10-09)

**Before release**
1. **B11, settings upgrade:** small, and it has to land before the version bump, or every user's settings reset on upgrade. Recommended.
2. **B10, session-info safety net:** before release or in 1.3; your call.
3. **S7, Inno Setup licence:** check the terms (yours).
4. **Release housekeeping (yours):**
   - regenerate the user-guide PDF
   - bump the version
   - mark PR #42 ready and merge it

**Mechanical tidy-ups (low risk; any time before the architecture work)**
- C1–C3 and C7: duplicated helpers, scale factors, magic numbers, and the radar zone switch blocks.
- File splits:
  - F2: `ShiftPointLearner.cs`, 645 lines.
  - F3: `ShiftPointProvider.cs`, 626 lines.
  - F4: `RelativeDisplayBuilder.cs`, 541 lines (the P1 brush tables added about 30).
- C5 is done (as P2).

**CI pipeline discussion (next)**
- T1: build and run the existing 59 tests in CI.
- T2: characterization tests for `PositionCalculator`, needed before F1.
- Candidates:
  - tests for B10's safety net, using made-up session-info samples
  - `tools/validate_track_catalog.py` as a CI step

**Architecture (after the CI pipeline)**
- F1: split `PositionCalculator.cs` (1,137 lines); needs T2.
- A1–A5, C4, C6 and C8.

**Skipped or accepted**
- D3 and P5 were skipped; S5 was accepted.

**Watch items**
- **Radar ghosts (B1):** fixed, but the original case never reproduced on demand.
- **Session-info parse failures (B10):** run Debug builds when convenient, so a real sample gets captured.
- **Memory over long sessions:** both perf runs levelled off around 125 MB, but were still rising about 1 MB per 5 minutes near the end. One perf log over a session of an hour or more would settle it.
- **Track catalog gap:** Oulton Park Fosters has no section data.
