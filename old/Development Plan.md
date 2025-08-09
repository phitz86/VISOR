# GRiD: Generative Raider of iRacing Data

GRiD is a diagnostic telemetry tool designed to evaluate iRacing SDK wrappers head-to-head to improve the stability and performance of the IRODex overlay project.

---

## 🎯 Project Goal

Build a wrapper-inclusive telemetry inspection tool that:

- ✅ Loads multiple SDK wrappers concurrently
- ✅ Polls and logs telemetry data frame-by-frame
- ✅ Highlights discrepancies (missing fields, stale values, update frequency issues)
- ✅ Confirms performance and completeness during edge cases (e.g., session transitions, yellow flags)
- ✅ Uses IRSDKSharper as baseline for comparison

### Outcomes:
- Determine which wrapper (if any) is the best candidate for IRODex v1.0 and beyond
- Decide whether to write a native C++ SDK reader ("Talk to God" solution)

---

## 📦 Wrappers to Evaluate

### C# Options (Primary Focus)
- [x] IRSDKSharper *(current baseline)*
- [ ] vipoo's `iRacingSDK.Net` *(rich model layer)*
- [ ] NickThissen's `iRacingSdkWrapper` *(older, proven)*
- [ ] SVappsLAB `iRacingTelemetrySDK` *(unvetted)*

### Node.js Options (Lower Priority)
- [ ] `node-irsdk` *(most mature JS lib)*
- [ ] `node-irsdk-2023` *(actively maintained fork)*
- [ ] `iracing-sdk-js` *(modern alt)*

### Direct Access
- [ ] Raw memory reader using iRacing's C++ SDK *(only if all wrappers prove inadequate)*

---

## 🧱 Project Structure

TelemetryInspector/                // C# version
├── Interfaces/
│   └── IracingTelemetryWrapper.cs
├── Wrappers/
│   ├── IRSDKSharperWrapper.cs
│   └── ...
├── Services/
│   └── TelemetryInspectorService.cs
├── Logging/
│   └── TelemetrySnapshotLogger.cs
├── Program.cs                     // CLI runner

telemetry-inspector.js            // Node.js version
└── (similar telemetry polling loop)

logs/
├── grid-irsdksharper.jsonl
├── grid-iracingsdknet.jsonl
└── ...

diffs/
└── (optional: post-run JSON or HTML reports)

---

## 🧩 System Design Summary

### Run Mode
- C# and Node.js wrappers run in **separate programs**
- Shared output schema for logs (e.g., JSONL, one entry per tick)

### Core Loop
- On each tick:
  - Each wrapper polls telemetry
  - Logs snapshot (raw + normalized)
  - Captures performance metrics (CPU, memory usage)
  - Diffs fields vs other wrappers (optional)
- Runs until manually stopped or on fixed duration

### Output Format
- Per-wrapper JSONL logs with performance metrics
- Optional merged logs and comparison diffs

---

## 📡 Telemetry Fields to Evaluate

Fields categorized by use in IRODex:

### 🏁 Session State & Timing
- `SessionTime`, `SessionTimeRemain`, `SessionTick`
- `SessionState`, `SessionFlags`, `SessionNum`
- `SessionLapsRemain`, `SessionLapsRemainEx`
- `IsReplayPlaying`

### 🏎️ Player Car Status
- `PlayerCarIdx`
- `CarIdxLap`, `CarIdxLapDistPct`, `CarIdxTrackSurface`, `CarIdxOnPitRoad`
- `CarIdxEstTime`, `Gear`, `RPM`, `Throttle`, `Brake`, `Clutch`, `IsOnTrack`

### 🥇 Positioning & Classification
- `CarIdxPosition`, `CarIdxClassPosition`, `CarIdxClass`, `CarIdxF2Time`, `CarIdxEstTime`
- `DriverInfo:Drivers:CarIdx`, `CarClassColor`

### 🕒 Lap Times
- `LapCurrentLapTime`, `LapLastLapTime`, `LapBestLapTime`
- `LapBestLapNum`, `LapCompleted`
- `CarIdxLapLastLapTime`, `CarIdxLapBestLapTime`, `CarIdxLapBestLapNum`

### 👥 Opponent Data
- `CarIdxTrackSurface`, `CarIdxOnPitRoad`, `CarIdxEstTime`, `CarIdxLapDistPct`, `CarIdxF2Time`

### 💻 System Status
- `FrameRate`, `CpuUsagePct`, `Latency`, `LatencyMax`, `PacketLoss`, `SessionErrors`

### 📑 Metadata
- `WeekendInfo`, `DriverInfo`, `SessionInfo`

---

## ✅ Evaluation Criteria per Field

Each field from each wrapper will be evaluated for:

- **Presence** — Is the field exposed by the wrapper?
- **Freshness** — Does it update every tick?
- **Consistency** — Does it match other wrappers (using IRSDKSharper as baseline)?
- **Latency** — Is the data delayed after session transitions?

---

## 🧪 Test Scenarios

### **Connection & State Change Testing**
- **Connection drops** - Deliberate iRacing restarts mid-session
- **Rapid state changes** - Session transitions, race starts, crashes, towing back to pits, pit entry/exit, caution periods
- **Wrapper reestablishment** - How quickly does each wrapper reconnect after iRacing restart?

### **Performance Monitoring**
- **Memory usage** per wrapper over time
- **CPU overhead** of each wrapper's polling loop
- **Threading behavior** - does wrapper block main thread?

---

## 🧠 Next Steps

- Define JSONL schema (timestamp, wrapper name, field dictionary, performance metrics)
- Implement `IracingTelemetryWrapper` interface in C# (starting with IRSDKSharper baseline)
- Build CLI tick loop for C# program with performance monitoring
- Prepare skeleton for Node.js version
- Design test scenarios for connection drops and rapid state changes
- Test logging and diff output under practice, quali, and race conditions