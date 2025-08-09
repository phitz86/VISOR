
import pandas as pd
import json
from pathlib import Path

# --- Configuration ---
log_dir = Path(r"C:\Users\cepha\Desktop\GRiD\bin\Debug\net8.0\logs")
output_csv = "wrapper_comparison_aligned.csv"
output_parquet = "wrapper_comparison_aligned.parquet"

# Target fields from grid_target_fields.txt
target_fields = [
    "Gear", "Speed", "FuelLevel", "FuelUsePerHour",
    "LapCurrentLapTime", "LapBestLapTime", "LapDeltaToBestLap", "LapLastLapTime",
    "sessionTime", "sessionTimeRemain", "sessionState",
    "SessionNum", "SessionType", "performance.durationMs"
]

# Core fields from wrapper log headers
core_fields = ["timestamp", "wrapper", "sessionTick", "playerCarIdx"]

# --- Load and Normalize ---
files = list(log_dir.glob("grid-*.jsonl"))
if not files:
    raise FileNotFoundError(f"No .jsonl files found in {log_dir.resolve()}")

records = []
for file in files:
    wrapper = file.stem.replace("grid-", "")
    with open(file, 'r') as f:
        for line in f:
            try:
                entry = json.loads(line)
                row = {
                    "timestamp": pd.to_datetime(entry.get("timestamp")),
                    "wrapper": wrapper,
                    "sessionTick": entry.get("sessionTick"),
                    "playerCarIdx": entry.get("playerCarIdx"),
                }

                fields = entry.get("fields", {})
                perf = entry.get("performance", {})

                for key in target_fields:
                    if key.startswith("performance."):
                        metric = key.split(".")[1]
                        row[key] = perf.get(metric)
                    elif key in entry:
                        row[key] = entry[key]
                    else:
                        row[key] = fields.get(key)

                records.append(row)
            except json.JSONDecodeError:
                continue

df = pd.DataFrame(records)
df['timestamp_aligned'] = df['timestamp'].dt.round('67ms')  # ~15Hz

# Save to files
df.to_csv(output_csv, index=False)
df.to_parquet(output_parquet, index=False)

print(f"✅ Exported to {output_csv} and {output_parquet}")
