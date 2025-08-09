
import pandas as pd
import json
from pathlib import Path

# --- Configuration ---
log_dir = Path(r"C:\Users\cepha\Desktop\GRiD\bin\Debug\net8.0\logs")
output_csv = "wrapper_comparison_aligned.csv"
expected_car_count = 64

# Canonical telemetry fields (IRODex targets + multiclass support)
target_fields = [
    "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
    "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
    "CarIdxLapDistPct", "CarIdxClassPosition", "CarIdxPosition", "CarIdxTrackSurface",
    "CarIdxLap", "CarIdxLastLapTime", "CarIdxCarNumber", "CarIdxClass"
]

# Scalar session metadata
core_fields = [
    "timestamp", "wrapper", "sessionTick", "sessionTime", "sessionTimeRemain",
    "playerCarIdx", "sessionState", "SessionLapsRemain", "SessionNum", "SessionType"
]

# Fields expected as arrays
array_fields = {
    "CarIdxLapDistPct", "CarIdxClassPosition", "CarIdxPosition", "CarIdxTrackSurface",
    "CarIdxLap", "CarIdxLastLapTime", "CarIdxCarNumber", "CarIdxClass"
}

# --- Load and Normalize ---
files = list(log_dir.glob("grid-*.jsonl"))
if not files:
    raise FileNotFoundError(f"No .jsonl files found in {log_dir.resolve()}")

records = []
field_availability = {}

for file in files:
    wrapper = file.stem.replace("grid-", "")
    field_availability[wrapper] = set()

    with open(file, 'r') as f:
        for line in f:
            try:
                entry = json.loads(line)
                fields = entry.get("fields", {})

                row = {
                    "timestamp": pd.to_datetime(entry.get("timestamp")),
                    "wrapper": wrapper,
                    "sessionTick": entry.get("sessionTick"),
                    "sessionTime": entry.get("sessionTime"),
                    "sessionTimeRemain": entry.get("sessionTimeRemain"),
                    "playerCarIdx": entry.get("playerCarIdx"),
                    "sessionState": str(entry.get("sessionState")),
                    "SessionLapsRemain": fields.get("SessionLapsRemain"),
                    "SessionNum": fields.get("SessionNum"),
                    "SessionType": fields.get("SessionType"),
                }

                for key in target_fields:
                    val = fields.get(key)
                    if key in array_fields:
                        if isinstance(val, (int, float)):
                            val = [val] * expected_car_count
                        elif isinstance(val, list):
                            if len(val) != expected_car_count:
                                val = val[:expected_car_count] + [None] * (expected_car_count - len(val))
                        else:
                            val = [None] * expected_car_count
                    if val is not None:
                        field_availability[wrapper].add(key)
                    row[key] = val

                records.append(row)
            except json.JSONDecodeError:
                continue

df = pd.DataFrame(records)
df['timestamp_aligned'] = df['timestamp'].dt.round('20ms')

# Save to CSV only
df.to_csv(output_csv, index=False)

# Print summary of missing fields by wrapper
all_fields = set(target_fields)
print("\n=== Missing Field Summary by Wrapper ===")
for wrapper, present_fields in field_availability.items():
    missing = sorted(list(all_fields - present_fields))
    if missing:
        print(f"\n{wrapper}:")
        for field in missing:
            print(f"  ✗ {field}")
    else:
        print(f"\n{wrapper}: All target fields present ✔")
