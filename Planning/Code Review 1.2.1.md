# VISOR 1.2.1 pre-release code review: findings

Scope: a line-by-line review of everything changed from 1.0 to 1.2.1 (`git diff 51e9f5b HEAD`, 53 files), plus a sweep of the whole codebase for file size, dead code, threading, performance and security.
Risk appetite for 1.2.1: fix bugs, delete dead code, and split files mechanically. Architecture changes go to 1.3.

## Summary

**Status (2026-10-10):** Phase 1, the three revision passes, is done and validated on the rig. Testing turned up B10, B11 and S7, and the research for Phase 2 found B12 and B13. Phase 2 (pre-release fixes, the CI test pipeline, then the architecture work) is under "Revision plan" at the end. Passes 4–7 are done and rig-checked; Pass 8 is committed and waiting for its rig check.

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
| B10 | Med | Session info the SDK can't parse leaves VISOR blank for a whole event | Small–Med | **Done** (Pass 6; rig-checked 10 Oct) |
| B11 | Med | Settings reset to defaults on every version bump | Small | **Done** (Pass 4; rig-checked 10 Oct) |
| B12 | Low–Med | At the finish, a car whose telemetry stops isn't held: the car behind moves up and two cars can show the same position | Small | **Pinned** (Pass 8: a skipped test describes the right result); the fix is its own commit |
| B13 | Low | Radar switched on from the Config window can't be dragged into place | Small | **Done** (Pass 4; rig-checked 10 Oct) |
| D1 | Low | `System.Management` package unused but shipped | Mech | **Done** (Pass 1) |
| D2, D4 | Low | Unused members and events | Mech | **Done** (Pass 1) |
| D3 | Low | Session data parsed but never read | Mech | **Skipped** (cheap; unused reads have come in handy) |
| D5 | Low | Stale files in `Planning/` and the repo root; dead csproj entries | Mech | **Done** (Pass 1), except the stale PDF, which you regenerate before release |
| C1–C3, C7 | Low | Duplicated helpers, scale factors, magic numbers, radar zone switches | Mech | **Done** (Pass 7) |
| C5 | Low | Fastest-lap positioning rebuilt per row | Mech | **Done** (as P2) |
| C4 | Low | Session type re-derived under a lock many times per frame | — | **Proposed:** Pass 10 |
| C6 | Low | Overlay and radar code-behind duplicate their plumbing | — | **Proposed:** Pass 12 |
| C8 | Low | Debug loggers each re-implement folder, file name and flush | Mech | **Done** (Pass 7; file naming shared, flushing left to each logger) |
| F2–F4 | — | Files over 500 lines: split plans | Mech | **Done** (Pass 7) |
| F1 | — | `PositionCalculator.cs` (1,137 lines): split plan | Mech | **Proposed:** Pass 9, after T2 |
| A1 | — | Radar view model builds WPF elements | — | **Proposed:** Pass 11 |
| A2 | — | Leaky session interface; Settings depends on Telemetry | — | **Proposed:** Pass 10 |
| A3 | — | PositionCalculator in the wrong layer, untestable | — | **Partly done** (Pass 8: plain input record); the move to `Race/` is Pass 9 |
| A4 | — | One UI tick for both windows | — | Only if a problem appears |
| A5 | — | User-editable catalog lives in Program Files | — | Optional feature; your call |
| P1–P4 | Low | Small per-frame waste (brushes, list copies, log I/O, notifications) | Small | **Done** (Pass 3) |
| P5 | — | Measure the transparent-window rendering cost on your rig | — | **Skipped** |
| S1 | Med | CI token has write access during the build job | Small | **Done** (Pass 3) |
| S2 | Med | CI actions and Inno Setup not pinned to fixed versions | Small | **Done** (Pass 3) |
| S3, S4 | Low | Log privacy, license notice | Small | **Done** (Pass 3) |
| S5 | Low | Named-object squatting | — | **Accepted** |
| S6 | Low | Installer `DelTree` scope | — | **Done** (with B4) |
| S7 | Low | Inno Setup prints "Non-commercial use only" | — | **Open:** check the licence terms (yours) |
| T1 | Med | CI never builds or runs the tests | Small | **Done** (Pass 5) |
| T2 | Med | No tests for PositionCalculator (needed before splitting it) | Mech | **Done** (Pass 8; 29 tests); rig check pending |

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

### B12: A car whose telemetry stops at the finish isn't held (Low–Medium; found 2026-10-09, Phase 2 research)
`FreezeDepartedCars` (`ViewModels/PositionCalculator.cs:517-568`) is meant to hold the finishing slot of a car that leaves during the checkered, including, as its comment says, "a car whose telemetry simply stops". But about three seconds after its data stops, such a car drops out of the running order one frame before it leaves the roster. When the hold runs, the car's overall position already reads −1, so it is skipped.

The car behind then slides up, while the departed car keeps a stale class position, so two cars can show the same class position. This was reproduced in a scratch test harness: two cars at P2. Cars that drop out of the session's driver list (the usual offline and AI case) are held correctly.

- **Fix:** after T2 pins it with a test, hold the car on its last valid positions rather than the current frame's.
- **Verify:** the T2 test, then a race where a car disconnects under the checkered.

### B13: Radar switched on from the Config window can't be positioned (Low; found 2026-10-09, Phase 2 research)
When the radar is off at start-up and switched on in the Config window, `App.ShowRadarWindow` (`App.xaml.cs:361`) creates it while config mode is already on. The new window subscribes to later config-mode changes but never reads the current state. It also isn't forced visible: `ConfigWindow.xaml.cs:42` only does that for a radar that already existed when the Config window opened. As a result its drag handle stays hidden, and it may stay faded out, until the Config window is closed and reopened.

- **Fix:** apply the current config mode and forced visibility when the window is created. C6 later moves this into a shared behaviour.
- **Verify:** start with the radar off, open the Config window, turn the radar on, and drag it.

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

Line counts as of 2026-10-09, before Pass 7. After it: `PositionCalculator.cs` 1,132, `ShiftPointProvider.cs` 511, `ShiftPointLearner.cs` 510, `RelativeDisplayBuilder.cs` 395.

| File | Lines | Split (code moves only; public API unchanged) |
|---|---|---|
| `ViewModels/PositionCalculator.cs` | 1137 | `FinishTracker` (freeze at checkered, departed cars, finish diagnostics), `CarTrackingCache` (valid roster, prediction, lap-desync correction), `RunningOrder` (sort, grid fallback, slot assignment). `PositionCalculator` stays as the thin front door the view models already call. Do **T2** first. |
| `Telemetry/ShiftPointLearner.cs` | 645 | Records and enums (`ShiftSample`, `GearShiftEstimate`, `SkipReason`, `ShiftModelState`) → `ShiftModelTypes.cs`. `RatioTracker` → own file. Cholesky solver → `LinearSolver.cs`. |
| `ViewModels/ShiftPointProvider.cs` | 626 | Calibration/stability tracking → `ShiftCalibrationTracker`. Progress-log formatting → `ShiftProgressLog`. What remains is lookup plus learning orchestration. |
| `ViewModels/RelativeDisplayBuilder.cs` | 541 | The relative-gap plumbing is gone (Pass 1); the P1 brush tables added about 30 lines back. Move row styling and segment colours into `RelativeRowStyler`. |
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

- **T1 (Medium; 1.2.1):** CI never builds or runs `Tests/VISOR.Tests`, and the project isn't in `VISOR.sln`. A change to the shift learner can break the tests without anyone noticing. Add a `dotnet test Tests/VISOR.Tests` step. It targets plain `net10.0`, so it adds well under a minute. *Proposed for Pass 5, as part of the CI pipeline below.*
- **T2 (Medium; 1.2.1):** the position calculator has more changelog fixes than anything else and no tests. Before splitting it (F1), add *characterization tests*, which record what it does today:
  - lap-desync correction
  - grid-source selection
  - leader-gated finish freezing
  - departed-car holds
  - pace-car exclusion

  The tests must pass unchanged after the split. To make it testable, `PositionCalculator.Update` takes a small plain input record (the per-car arrays and scalars it reads) that the overlay fills from the snapshot. That is a signature change only; the logic stays the same. *Proposed for Pass 8.*

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
| `SessionYamlFailureLogger` (added 2026-10-09) | Debug | Saves session info that fails to parse (B10), at most five files a run | New | **Keep** until B10 has a real sample, or for good as a safety net |

---


## Revision plan

Ground rules for every pass:
- **Build:** Debug and Release build with 0 warnings and 0 errors before anything is pushed to `claude/zen-carson-2vx0dk`. From Pass 5 on, CI's tests and checks must be green as well.
- **Changelog:** each pass adds `[Unreleased]` entries to `CHANGELOG.md`. The version bump waits until release.
- **Gate:** each pass **stops** at a gate.
  - **Rig** passes wait for an on-track test on your rig.
  - **CI** passes change no runtime behaviour, so green CI plus a short smoke run is the gate.

### Phase 1 (1.2.x): three gated passes, done

| Pass | Commits | Scope | Rig result |
|---|---|---|---|
| 1 | `f23f1ae` | Dead code (D1, D2, D4, D5); the RelativeGap, TelemetryCSV and SessionData loggers; bugs B1, B2, B5–B8 | 8 Oct: verified Exit from the ⚙ window, the radar toggle, track location (66 entries), shift points and the debug folders |
| 2 | `7c85e70` | Ordered frame delivery (B3) and the exit-hang fix (B9) | 8 Oct: a full multiclass practice → qualifying → race at Road Atlanta with no issues; `[FrameBacklog]` only at session loads |
| 3 | `4030869`, `71e9595`, `c5d76f6` | P1–P4; S1–S4; installer B4 | 9 Oct: see below |
| — | `ca9502c`, `628f4a9` | Debug builds save session info that fails to parse (B10) | — |

Pass 3 rig results (9 Oct):
- The upgrade and fresh installs were clean.
- Second launch, the log header and Debug Mode logging were verified.
- Two perf runs: about 17–18% of one core on track, and memory levelling off at about 125 MB. Debug Mode had no measurable cost.
- A clean race on the installed build.

Decisions carried forward:
- D3 and P5 were skipped, and S5 was accepted.
- `RelativeRowViewModel.IncidentCount` and the finish diagnostics stay.
- The PDF is yours to regenerate.

Testing in Phase 1 found B10, B11 and S7.

### Phase 2 (proposed): release, CI pipeline, then architecture

This follows the order you set: a broader CI test pipeline first, then the architecture work. Research for it covered the position calculator, the session-data layer, the UI and radar, and the tests and CI setup. It also found B12 and B13.

#### Decisions for you

1. **Release first, or fold everything into 1.3?** *Decided (2026-10-09): no release yet.* There are no other users, so the release waits until the review and update work is finished.
   - Passes 4–7 go in order, one commit each, then one combined rig check before Pass 8 (see "Combined rig check for Passes 4–7" below).
   - Whichever release bumps the version must include B11 (it does: Pass 4).
2. **B10 timing.** *Recommended: Pass 6, right after the CI foundation, so the repair lands with its tests.* If the 1.3 work runs long, Pass 6 can ship on its own as a point release.
3. **Test project shape.** *Recommended: keep `Tests/VISOR.Tests` as a plain `net10.0` project that links source files.* It runs on Linux CI runners and in Visual Studio's Test Explorer. A Windows-only project that references the app would only be needed if WPF code ever needs testing.
4. **CI strictness.** *Recommended:*
   - warnings as errors in CI (there are 0 warnings today)
   - a whitespace-only format check, not style rules
   - coverage reported, with no threshold
   - build, test and lint set as required checks in branch protection (a GitHub setting you change)
5. **Dependabot for NuGet.** *Recommended: yes.* The test packages are already behind (Microsoft.NET.Test.Sdk 17.14 vs 18.10, the xUnit runner 3.1 vs 4.0). It means more bot PRs.
   - *Done (Pass 5).* Since then the tests moved to xUnit.net v3: NuGet marks xUnit v2 as legacy. They use `xunit.v3.mtp-off` 4.0.2 with `xunit.runner.visualstudio` 4.0.1 and Microsoft.NET.Test.Sdk 18.10.1. That keeps the classic VSTest runner, so Test Explorer and CI's `dotnet test` options (TRX results, coverage) are unchanged. The plain `xunit.v3` package would switch to Microsoft Testing Platform v2, which needs a repo-wide `global.json` setting and different CI options; that move can come later.
6. **A5, the user catalog override.** This is a feature, not a fix. It is independent of everything else, so it can go into 1.3 at any point, or wait.
7. **A4, one UI tick for both windows.** *Recommended: only if a problem shows up.* The rig shows `[FrameBacklog]` only at session loads.

#### Pass 4: pre-release fixes (rig)
- **B11, settings upgrade:**
  - Add an `UpgradeRequired` setting, default true.
  - In `CreateWithRecovery`, once the current file has loaded, call `Upgrade()` once, set the flag to false and save.
  - Put the upgrade in its own try/catch, so a corrupt *previous-version* file is skipped with a warning. Otherwise it would reach the recovery path, which backs up and deletes the file.
  - Carry over only when this version has no settings file of its own yet. The first build with the fix is still 1.2.1.0 and already has one, and an older version's values (1.2.0.0, say) must not overwrite it. The same rule means a corrupt-settings reset keeps the defaults rather than re-importing an older version.
- **B13, radar drag handle:** when the radar window is created, apply the current config mode and forced visibility.
- **Gate (rig):**
  - Build a test installer with the version bumped (for example 1.2.2.0), install it over the current build, and check that settings and window positions survive.
  - Start with the radar off, open the Config window, turn the radar on, and drag it.
- **At release time (your steps), now after Phase 2:**
  - the S7 licence check
  - the PDF
  - the version bump: CI requires `Version`, `FileVersion` and `AssemblyVersion` to be equal and four-part
  - mark the PR ready, merge it, and tag

#### Pass 5: CI test pipeline foundation, T1 (CI)
- **Solution:** add `Tests/VISOR.Tests` to `VISOR.sln`. Anything under `Tests/` is already excluded from the app's compile, and the installer only packs the app's output folder.
- **Log test seam:** not needed after Pass 4. `Log` writes nothing to disk until `StartNewSession`, which tests never call; it only creates the log folder, which already exists on your PC.
- **New `test` job** (ubuntu, in parallel with `build`):
  - `dotnet test` with TRX results and Cobertura coverage, uploaded as an artifact
  - a summary written to the run page
  - no extra token permissions, which keeps S1 intact
- **New `lint` job** (ubuntu, about a minute):
  - `tools/validate_track_catalog.py`, which needs only the Python standard library and exits 1 on structural errors
  - `dotnet format whitespace --verify-no-changes`, after fixing the three whitespace issues it finds today (`ShiftPointProvider.cs:497-500`)
  - `dotnet list package --vulnerable --include-transitive`
- **`build` job:** warnings as errors.
- **`sign` job:** `needs: [build, test, lint]`, so an untested tag can't be signed.
- **Housekeeping:**
  - a `concurrency` group that cancels superseded PR runs
  - every new action pinned to a SHA
  - the `nuget` ecosystem added to Dependabot (decision 5)
- **First new tests**, all pure logic with no WPF or SDK:
  - `FuelViewModel`: the rolling per-lap average.
  - `PositionHistoryBuffer`: teleport and stationary detection, and crossing-time interpolation, which is the core of the gap figures.
  - `TrackSectionCatalog.Resolve` against the shipped catalog, as a C# check alongside the Python mirror.
  - `UpdateChecker` version parsing, made `internal` with `InternalsVisibleTo`.
- **Cost:** about 1–1.5 minutes per parallel job. That adds little wall time, because the Windows build stays the longest job.
- **Gate (CI):** CI green on the PR, and the tests pass in Visual Studio.

#### Pass 6: session-info safety net, B10 (rig)
- **When VISOR steps in:**
  - VISOR registers the SDK's raw session-info handler in all builds.
  - For each update it repeats the SDK's two checks, syntax-only and cheap: the text as-is, then with the SDK's six-field quoting.
  - Only when both would fail does VISOR step in, so normal sessions take exactly the path they take today.
- **The repair:**
  - Rejoin values split across lines, and quote scalar values. Nested-mapping keys, `- Key: value` list lines and values already quoted are left alone.
  - Deserialize into the SDK's own `TelemetrySessionInfo` with the SDK's exact settings (`IgnoreUnmatchedProperties`, default naming).
  - Apply the result through `ApplySdkSession`, the entry point the SDK's own result uses.
- **Safety:**
  - *As built:* the first update the SDK can't read switches VISOR to reading every update itself until iRacing disconnects (`SessionInfoFallback`), so the two sources never alternate. Choosing the source and applying the result happen under one lock, so an SDK result already on its way can't land after a newer one from VISOR.
  - While VISOR reads session info itself, the SDK's repeated parse errors for it go to Debug level; the first one is logged in full.
  - The handler catches everything: an exception escaping an SDK handler stops telemetry for good.
  - An explicit `YamlDotNet` 18.1.0 package reference. Today it arrives only through the SDK.
- **Logging:** one Release-level warning per distinct failure. The Debug capture stays.
- **Tests:**
  - synthetic YAML fixtures, with no real names, for each confirmed breaking shape
  - valid YAML passes through untouched
  - the "would the SDK fail?" decision
  - the repaired text deserializes to the expected values
- **Upstream:** report the six-field limitation to SVappsLAB. I can draft the issue.
- **Gate (rig):** a few normal sessions with no repair warnings, and the HUD becomes ready as usual. The repair path itself is proven by the tests unless the problem comes back.

#### Pass 7: mechanical tidy-ups (CI)
These are pure code moves and consolidations: behaviour is unchanged, apart from log wording where noted. The build and the tests protect them.
- **C2:** one `WindowScale` table for the main and radar size presets, in place of four copies.
- **C3:** shared constants for SessionState values, flag masks and the pace-car class. Check the SDK's generated `SessionState` enum first.
- **C1, then C8:**
  - The loggers use `ShiftModelStore.SafeFileStem`, with its algorithm unchanged: saved models are found by that file name.
  - Then a small `DiagnosticFiles` helper for the three debug loggers' folders and file names.
- **C7:** one table from the CarLeftRight state to radar zones, driving both the zone assignment and the highlights. It replaces six parallel switches across `RadarViewModel` and `RadarWindow`; only log wording changes.
- **F2–F4:**
  - `ShiftPointLearner`: its types, `RatioTracker` and the Cholesky solver move into their own files.
  - `RelativeDisplayBuilder`: row styling moves into `RelativeRowStyler`. `AssignProximitySegments` splits into measuring and colouring, keeping its early returns.
  - `ShiftPointProvider`: the calibration tracker and the progress-log formatting move out, with care around its lock.
  - New files are added to the test project's links.
- **Gate (CI):** CI green, plus one short session on a Debug build to check that the `ShiftPoints` and `WetResearch` files are still written.

#### Combined rig check for Passes 4–7
One session on a Debug build from VS covers all four passes:
- **B11 (Pass 4):**
  - Change a setting and move a window, then close VISOR.
  - Temporarily set `Version`, `FileVersion` and `AssemblyVersion` in `VISOR.csproj` to 1.2.2.0 (don't commit it), and run again.
  - The settings and window positions should still be there, and the log should show `Settings carried over from the previous version` once. The next start shouldn't show it again.
  - Then set the version back.
- **B13 (Pass 4):** start with the radar off, open the Config window, turn the radar on, and drag it.
- **CI (Pass 5):** green on the PR, and the tests show in Visual Studio's Test Explorer.
- **B10 (Pass 6):** normal sessions log no repair warnings, and the HUD becomes ready as usual.
- **Tidy-ups (Pass 7):**
  - The overlay, relative and radar look and behave as before, including zone highlights and multiclass colours.
  - The `ShiftPoints` and `WetResearch` debug files are still written.

**Results (10 Oct): passed.**
- **B11:** with the version bumped to 1.2.2.0, settings and window positions survived, and the log showed `Settings carried over from the previous version`.
- **B13:** a radar switched on from the Config window can be dragged.
- **Tests:** 115 green in Test Explorer.
  - Getting there needed two follow-ups. The tests moved to xUnit.net v3 (`7ba7187`), because NuGet marks v2 as legacy.
  - Two session-info tests also failed on a Windows checkout, whose CRLF line endings broke their multi-line edits. They were fixed, and CI now runs the tests on Windows too (`187c81d`).
- **Normal sessions:** no `[SessionInfo]` repair warnings, and the HUD became ready as usual.
- **Visuals:** the overlay, relative and radar passed a quick check; keep an eye on them in later rig checks.
- **Debug files:** the `ShiftPoints` and `WetResearch` files are still written.

#### Pass 8: PositionCalculator characterization tests, T2 and the A3 input record (rig)
- **Input record:** `Update` takes a plain `PositionFrame` record, filled from the snapshot by `MainViewModel`.
  - It holds the eight values the calculator computes with (two scalars and six per-car arrays) and the five it only logs. The 64-length arrays are guaranteed.
  - This is a signature change only. It takes the SDK out of the tests, so they don't need a copy of the telemetry-variable list.
- **Test helpers:** a fake `ISessionDataProvider` and a frame-sequence builder.
- **About 25–30 tests** pinning today's behaviour:
  - lap-desync correction at the line: lap counter late or early, a stuck counter, the first frame after a gap
  - grid order before the green: live arrays vs qualifying results, the coverage tie-break, the green latch
  - leader-gated finish freezing: the leader first, lapped cars and slower classes not frozen, frozen slots skipped
  - departed-car holds under the checkered, and none before it
  - pace-car exclusion
  - roster entry and expiry timing, practice and qualifying mode, reset, session transition
- **B12:** a test that describes the correct result is marked as a known bug, then B12 gets its own small fix commit.
- **Gate (rig):** one race to the checkered, because the per-frame call changed.
- **Result (2026-10-10):**
  - `PositionFrame` (`Telemetry/PositionFrame.cs`) is filled by `SVappsLABSnapshot.ToPositionFrame()`. Arrays that already cover all 64 cars are passed through, not copied. The calculator's logic and log text are unchanged.
  - 29 tests in `PositionCalculatorTests`, run through a scripted-race helper (`PositionCalculatorHarness.cs`). The suite is 144 tests.
  - Each finish test was checked by breaking the code it covers, and the matching test failed each time:
    - the leader gate on class P1 instead of overall P1
    - frozen slots not skipped
    - −1 stored as the lap-completed baseline
    - the baseline seeded on the first checkered frame (the old leader-latch bug)
    - no lap offset for a car predicted across the line
  - B12 is pinned by `Departed_UnderTheCheckered_ACarWhoseTelemetryStopsKeepsItsPlace`, skipped until its fix.

#### Pass 9: PositionCalculator split, F1 and A3 (rig)
- **Split:** `FinishTracker`, `CarTrackingCache` and `RunningOrder` sit behind the existing `PositionCalculator` front door, moved to a `Race/` folder (A3).
- **Must stay exactly as is:**
  - the frame order: finish → roster → departed → prediction → sort
  - every clear in `Reset` and in the session transition
  - the 64-length assumption
  - the exact `[Finish]` and `[Leader]` log text
- **Tests:** the Pass 8 tests must pass unchanged.
- **Gate (rig):** a full practice → qualifying → race with a finish, ideally multiclass.

#### Pass 10: one session snapshot, A2 and C4 (rig)
- **The snapshot:** `ApplySdkSession` builds one immutable session view per parse and publishes it by a single reference; `ClearCache` publishes an empty one. Each frame then reads one consistent view, instead of taking the coordinator's lock 20–35 times.
- **Session type:** derived once per parse. Today's matching is kept unless you want it changed:
  - "Lone Qualify" also counts as qualifying, and "Heat Race" as a race.
  - Warmup and Testing use race positioning.
- **Downcasts:**
  - The four downcasts to `SessionDataCoordinator` go: track length, name, config and display name join the view.
  - Settings takes a plain "hide relative" bool, so the Settings layer no longer depends on Telemetry.
- **Small fixes along the way:**
  - The session schedule is rebuilt on each parse. Today, sessions from an earlier subsession can linger until a disconnect.
  - The track-location readout reads its three values from one view.
- **Tests:** `ApplySdkSession` fed YAML fixtures, reusing Pass 6's.
- **Gate (rig):** practice, qualifying, lone qualifying (the relative hides) and a race.

#### Pass 11: the radar draws itself, A1 (rig)
- **Design:**
  - The view model publishes a list of plain car items: position, colour, number and pit state.
  - `RadarWindow` draws them with an `ItemsControl` over a `Canvas`.
  - Items are reused from frame to frame. Rebuilding them would recreate the number shadows every frame, which is the expensive part.
  - `Reset` becomes `Clear()`, so ghost cars (B1) can't come back.
- **Two visible changes:**
  - Cars already on the radar resize when the size preset changes; today they keep their old size.
  - Each car's number draws over its own rectangle, rather than all numbers over all rectangles.
- **Gate (rig):** all three sizes, pit cars, multiclass colours, fade in and out, and a session hop.

#### Pass 12: shared window plumbing, C6 (rig)
- **Changes:**
  - An attached behaviour for the config-mode drag handle. It replaces four duplicated blocks and absorbs the B13 fix.
  - A small helper for the SDK subscriptions, the frame poster and one marshalling priority for both windows. Today the overlay and the radar use different priorities for the "HUD ready" change.
- **Gate (rig):** config mode on and off, drag both windows, disconnect and reconnect, and exit.

#### Optional
- **A5, user catalog override:**
  - Load `%LOCALAPPDATA%\VISOR\TrackSections.json` as well as the shipped file, with user entries winning track by track. That needs a tiered lookup: the current resolver prefers config-specific matches, so simply listing user entries first isn't enough.
  - Merge the two, so catalog updates still arrive.
  - The README, the user guide, the catalog's `_readme` and the validator (which needs a path argument) change with it.
- **A4, one UI tick:** if it's ever needed, the first step is one shared frame poster for both windows. That's about 30 lines, and easier after C6.
- **Replay tests:**
  - The SDK can play back `.ibt` recordings, so a test of the whole pipeline without iRacing is possible later.
  - Caveat: recordings may lack most per-car arrays, and they contain driver names and IDs, so scripted frames stay the main tool.

#### Watch items
- **Radar ghosts (B1):** fixed, but the original case never reproduced on demand.
- **Session-info parse failures (B10):** run Debug builds when convenient, so a real sample gets captured.
- **Memory over long sessions:** both perf runs levelled off at about 125 MB, but were still rising about 1 MB per 5 minutes near the end. One perf log over a session of an hour or more would settle it.
- **Track catalog gap:** Oulton Park Fosters has no section data.
