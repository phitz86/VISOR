# VISOR — Nullable Reference Warning Cleanup

> Planning doc for a dedicated thread. The build is currently **green** (these are
> warnings, not errors), so this is polish, not a blocker. Pick it up on a rainy day.

---

## 1. The problem

`VISOR.csproj` has `<Nullable>enable</Nullable>` (net8.0-windows, C# 12), but the
codebase never had a nullable-annotation pass. The result is ~67 standing
`CS86xx` warnings that we've carried "basically forever." Almost all of them are
in code our typed-snapshot migration never touched — they're pre-existing.

Nothing here changes runtime behavior today; the compiler is just pointing out
places where its null-tracking can't prove a reference is non-null. Resolving
them = finishing the annotation pass the `<Nullable>enable` switch implies.

There is **no `TreatWarningsAsErrors`**, so today they're cosmetic — but they bury
genuinely useful signals (the handful of real "this can actually be null" cases)
in noise, and they make a clean build look dirty.

---

## 2. Current state (as of this doc, ~67 warnings)

Grouped by pattern, with the fix and the risk. Line numbers drift as files
change — trust the file + warning code, not the exact line.

| # | Pattern | Code(s) | Where (files) | Fix | Risk |
|---|---------|---------|---------------|-----|------|
| ~28 | **Non-nullable field/event/property not set in constructor** | CS8618 | App, Log, RelativeGapLogger, TelemetryCSVLogger, ConfigModeManager, SettingsManager, UserSettings, SVappsLABSDKWrapper, CountdownViewModel, RadarViewModel, ConfigWindow | Three sub-fixes below | Low |
| ~16 | **Event-handler `sender` nullability** | CS8622 | App, SVappsLABSDKWrapper, MainWindow, RadarWindow | `object sender` → `object? sender` | None |
| ~10 | **`null` assigned/defaulted to non-nullable** | CS8625 | Log, RelativeGapLogger, TelemetryCSVLogger, SettingsManager, ClassColorManager, MainWindow, RadarWindow | Mostly nullable default params: `T x = null` → `T? x = null` | None / Low |
| ~6 | **Possible-null assigned to non-nullable local** | CS8600 | Log, SessionDataLogger, SVappsLABSDKWrapper, SVappsLABSnapshot, MainWindow | Case-by-case (see §4) | Low / Judgment |
| 2 | **Possible-null argument** | CS8604 | SessionDataAdapter, MainWindow | Annotate param nullable or guard | Judgment |
| 2 | **Possible-null return** | CS8603 | PositionHistoryManager, SVappsLABSDKWrapper | Declare return `T?` + handle callers | Judgment |
| 1 | **Deref of possibly-null** | CS8602 | ConfigWindow | Null-check before use | Judgment |
| 2 | **`ILogger` interface signature mismatch** | CS8633, CS8767 | SVappsLABSDKWrapper (VisorSdkLogger) | Match interface's nullable annotations | None |

### The CS8618 sub-fixes (the biggest bucket)
1. **Events never initialized** (`SettingsChanged`, `WindowSizeChanged`,
   `ElementVisibilityChanged`, `RadarVisibilityChanged`, `ConfigModeChanged`,
   `ExitRequested`, `SnapshotAvailable`, `ConnectionStateChanged`,
   `PrimedStateChanged`): declare nullable — `event EventHandler? Foo;`. This is
   the idiomatic fix and already matches how we raise them (`Foo?.Invoke(...)`).
   **No behavior change.**
2. **Init-later fields** (`_client`, `_latestSnapshot`, `_cancellationTokenSource`,
   `_monitoringTask`, `_writer`, `_writerTask`, `_currentLogFilePath`,
   `_currentCsvPath`, the `_instance` singletons, `_currentLapDisplay`): these are
   assigned in an `Initialize()`/`Start()` method or a singleton accessor, not the
   ctor. Fix with `= null!;` (null-forgiving: "set before any use") **or** make
   them nullable and guard. `null!` is zero-risk *if* the "set before use"
   contract truly holds — confirm each one rather than reflexively stamping it.
3. **Auto-props set via object initializer** (`Rectangle`, `NumberText` on a
   RadarViewModel row type): add the `required` modifier (C# 11+, we're on 12) or
   make them nullable.

---

## 3. Proposed solution & workflow

Mirror the cadence that worked for the telemetry migration: small, reviewable
passes, each gated by a local build on Pete's machine (the agent can't compile
here). Keep it on its own branch / PR — **do not** mix with the position-indicator
work.

**Pass 1 — Mechanical sweep (~50 warnings, ~zero runtime risk).**
- CS8622: `object sender` → `object? sender` everywhere.
- CS8618 events → `event ...? Name;`.
- CS8625 nullable default params → `T? x = null` (the methods already null-check).
- CS8618 init-later fields → `= null!;` (after confirming the set-before-use
  contract per field).
- CS8618 auto-props → `required`.
- CS8633 / CS8767 / CS8603(BeginScope) on `VisorSdkLogger`: match `ILogger`'s
  signatures exactly (`Exception? exception`, `IDisposable? BeginScope<TState>`,
  matching `TState` constraints).
- One self-inflicted item: `SVappsLABSnapshot` `CarIdxTrackSurface` cast
  (`(int[])(object)...`, CS8600 ×2) introduced by the migration — silence with
  nullable cast annotations (`(int[]?)(object?)... ?? EmptyInt64`).

Commit, Pete builds, confirm the count drops and nothing cascaded unexpectedly.

**Pass 2 — Judgment cases (~10–15).** One small commit. Each is a place the
compiler thinks null is genuinely reachable; decide per site whether the honest
fix is *annotate nullable + handle* or *add a guard*. Flag any that look like a
**latent bug** rather than a missing annotation — those are the payoff of this
exercise. See §4.

**Pass 3 (optional) — Lock it in.** Once the count is 0, consider
`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (or at least
`<WarningsAsErrors>nullable</WarningsAsErrors>`) so new nullable regressions can't
sneak back. Decide at the end, not the start.

### Why iterative, not one big edit
Nullable changes **cascade**: making a field/param/return nullable forces
null-handling at its use sites, which can surface *new* warnings (or resolve
others). Doing it in passes with a build between keeps the blast radius visible.
This touches many files (App, all three Windows, all of Settings/, all of
Diagnostics/, the wrapper, several ViewModels) but is almost entirely additive
annotation — the 500-line-per-file rule is not at risk.

### What to avoid
Suppressing instead of resolving (`#nullable disable` per file, blanket
`<NoWarn>`) throws away the safety the annotations are meant to give. Pass 1
already clears most of the noise for almost no risk, so suppression isn't worth
it.

---

## 4. The judgment bucket (the cases worth a real look)

These are where null may actually be reachable — handle deliberately, and call
out anything that smells like a bug:

- **`PositionHistoryManager.GetBuffer` (CS8603)** — literally `return null` for an
  out-of-range `carIdx`, but the return type is non-nullable. Honest fix:
  `PositionHistoryBuffer?` and confirm callers null-check.
- **`SessionDataAdapter.ParseIncidentLimit(string raw)` (CS8604)** — being handed
  a possibly-null string. Does it tolerate null, or should the caller guard?
- **`ConfigWindow.xaml.cs:~33` (CS8602)** — dereference of a possibly-null
  reference. Check what's null and why.
- **`MainWindow.xaml.cs` (CS8600 / CS8604)** — incl.
  `GetMainWindowSize(ISessionDataProvider sessionDataProvider = null)`; the
  default-null param wants a nullable annotation, and confirm the body handles it.
- **`Log.cs:~268`, `SessionDataLogger.cs:~135`, `SVappsLABSDKWrapper.cs:~269`
  (CS8600)** — possibly-null values assigned to non-nullable locals; likely
  benign (TryGetValue/Invoke results) but verify each.

---

## 5. Constraints & working style (carry into the new thread)

- C# / .NET 8.0 (net8.0-windows) / WPF / MVVM. C# 12, `Nullable` enabled.
- Pete is not a coder but is technical; keep replies concise, don't dump all
  reasoning, give a token-usage update every ~3 messages.
- Discuss the plan before editing; the agent can't compile — Pete's local build
  is the verification gate after each pass.
- Prefer complete file replacements over scattered snippets (single-line/syntax
  fixes excepted).
- Keep every file under 500 lines.
- Branch + PR for this work; keep it separate from the position-indicator track.

---

## 6. Kickoff prompt for the new thread

> Copy-paste this to start the dedicated thread.

```
We're doing a focused cleanup of VISOR's standing nullable-reference warnings
(the ~67 CS86xx warnings from <Nullable>enable). The build is currently green —
these are warnings, not errors — so this is polish. Full context, the categorized
warning inventory, the judgment cases, and the proposed workflow are in
Planning/Nullable Warning Cleanup.md — read that first.

Plan: work in passes on a dedicated branch, each gated by my local build (you
can't compile in your environment, so I'm the verification gate).
  - Pass 1: the mechanical, ~zero-risk sweep (sender params, nullable events,
    nullable default params, init-later fields via `= null!` after confirming
    set-before-use, `required` auto-props, the VisorSdkLogger ILogger signatures,
    and the CarIdxTrackSurface cast).
  - Pass 2: the judgment cases — annotate-and-handle or guard, per site, and flag
    anything that looks like a real latent null bug rather than a missing
    annotation.
  - Pass 3 (decide at the end): whether to turn on WarningsAsErrors=nullable to
    prevent regressions.

Working style: I'm technical but not a coder — keep replies concise, don't show
all your reasoning, and give me a token-usage update every ~3 messages. Talk
through what you're about to change before each pass, then give me complete file
replacements (single-line/syntax fixes can be snippets). Keep every file under
500 lines. Don't touch the position-indicator work — that's a separate track.

Start by reading the plan, confirming the current warning count still matches
(I'll paste a fresh build log if it's drifted), and proposing the exact Pass 1
edit list for my sign-off before you change anything.
```
