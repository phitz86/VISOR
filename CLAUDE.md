# VISOR: conventions for Claude and contributors

VISOR is a WPF overlay and proximity radar for iRacing (.NET 10, Windows). This file is the short
list of rules that earlier cleanup passes (PR #42, Passes 1-12; PR #51, Passes 13-17) kept having
to apply by hand. The history is in `Planning/Code Review 1.2.1.md`. The build and CI enforce
what they can (`.editorconfig`, `Directory.Build.props`, `tools/check_conventions.py`); the rest
is judgment, below.

## Before you push

A change is not done until all of these pass. CI runs every one except the Debug build, so run
that one yourself: the Debug-only loggers compile only there, and CI (Release) cannot see them.

```
dotnet build VISOR.sln -c Release -warnaserror
dotnet build VISOR.sln -c Debug   -warnaserror
dotnet test  Tests/VISOR.Tests -c Release
dotnet format whitespace --folder --verify-no-changes
python3 tools/check_conventions.py
```

Re-read your own diff for the leftovers listed under "Finish the job".

The app and the test project compile some of the same files. The WPF SDK leaves `System.IO` and
`System.Net.Http` out of the app's implicit usings, so a shared file that uses them needs its own
`using` line; the test project removes the same two implicit usings (see its `.csproj`) so both
compile the same way. Do not delete such a line because one project's analyzer calls it unused.

Building on Linux (cloud sessions): add `-p:EnableWindowsTargeting=true`. Ubuntu's apt
`dotnet-sdk-10.0` (10.0.113) is too old for the iRacing SDK's source generator (error CS9057); use
Microsoft's SDK, or in a scratch copy add `Microsoft.Net.Compilers.Toolset` 5.9.0 to both
projects. Do not commit that workaround.

## Finish the job

A change that makes something obsolete removes it in the same change. Look for:

- `using` lines, private members, parameters and fields that nothing uses any more (the build
  now fails on these).
- XAML styles, converters, resources and settings keys that nothing references.
- Log lines, tests and helper methods that only the removed code used.
- Docs: `CHANGELOG.md`, the user guide and the README, if what they describe changed.

A refactor is not finished until its leftovers are gone. PR #51 existed only to remove what the
1.3.0 rework left behind.

Kept on purpose, so do not remove them as "unused" (D3 in the review doc): the nine unused SDK
telemetry subscriptions and the session fields that are parsed but never read.

## Logging

- Diagnostic logging added to chase a bug carries a `TEMP-DIAG` comment and is deleted in the
  same PR as the fix. CI fails while the tag is present. (Pass 15 deleted a whole set of these,
  one of which printed `System.Single[]`.)
- Permanent logs are for things worth seeing in a user's log file: Warning for the unexpected,
  Debug for per-event detail. Never log a collection or object directly; log the values.
- No personal or machine data in logs: no machine name, and paths under the user profile are
  shortened by `Log` (S3). Keep it that way when adding log lines that include paths.

## Code

- **Don't check what the type guarantees.** Nullable reference types are on. The per-car arrays
  in `PositionFrame` are always `PositionFrame.CarCount` (64) long. Validate only at the edges,
  where data is untrusted: the SDK, iRacing's session YAML, files on disk (`TrackSections.json`,
  shift models), settings and the update-check response.
- **Reuse before you write.** Search first. Shared pieces include `LapDistance` (lap wrap and
  range), `ClassColorManager.CreateFrozenBrush`, `WindowScale`, `ObservableObject`,
  `ConfigDragHandle`, `WindowSdkLink`, `FramePoster`, `DiagnosticFiles` and
  `ShiftModelStore.SafeFileStem`. A second copy of logic is a reason to extract it; a third is a bug
  waiting to happen.
- **Name magic numbers.** iRacing values belong in `IRacingIds` and `SessionStates`.
- **Behaviour change and cleanup go in separate commits.** A cleanup commit changes no behaviour
  and says how that was checked. Moving code is its own commit from changing it.
- `Race/` and everything the test project links in must not depend on WPF. The test project
  builds them as plain `net10.0`, which is the check.

## Comments

- Say why, in the present tense. Do not narrate history ("used to", "the old code", "replaces
  the previous …", "no longer"): that belongs in the commit message or `CHANGELOG.md`.
- No review-finding IDs such as `(B12)` in code (CI checks this). Point at the behaviour instead.
- Complete the XML doc comments you write: every parameter, and a summary that says something
  the name does not. The compiler checks malformed and partial docs.

## Size

About 500 lines per C# file is the target; CI fails an app file over 600 (tests are exempt).
Splits move code only and keep the public API.

## Security

- Treat iRacing session info, `TrackSections.json`, shift-model files and the update-check
  response as untrusted input. Reject bad entries one at a time instead of failing wholesale (B6).
- The installer removes only VISOR's own files, never a whole folder (B4).
- CI: actions are pinned to full commit SHAs, the workflow token is read-only by default, and
  only the sign job may write. Keep it that way.

## Releasing

A version bump touches `VISOR.csproj` (`Version`, `FileVersion`, `AssemblyVersion`), the README
badge, `CHANGELOG.md` and the user guide's file name. Tag the merge commit exactly
`v<version>`, then publish the release: the update check only sees published releases.
