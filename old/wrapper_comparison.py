
import pandas as pd
import json
from pathlib import Path

# Path to the log directory (adjust if needed)
log_dir = Path("GRiD/bin/Debug/net8.0/logs")
files = list(log_dir.glob("grid-*.jsonl"))

if not files:
    raise FileNotFoundError(f"No .jsonl files found in {log_dir.resolve()}")

dfs = []

for file in files:
    wrapper_name = file.stem.replace("grid-", "")
    df = pd.read_json(file, lines=True)
    df["wrapper"] = wrapper_name
    dfs.append(df)

# Combine all data into one DataFrame
all_data = pd.concat(dfs, ignore_index=True)

# Core fields + common telemetry values
core_fields = ['timestamp', 'wrapper', 'sessionTick', 'sessionTime', 'sessionTimeRemain', 'playerCarIdx', 'sessionState']
telemetry_fields = ['Speed', 'RPM', 'Gear', 'Throttle', 'Brake', 'FuelLevel', 'LapDistPct']

# Expand the 'fields' dictionary
fields_expanded = pd.json_normalize(all_data['fields'])
fields_expanded = fields_expanded[telemetry_fields].copy()

# Merge with core fields
combined = pd.concat([all_data[core_fields], fields_expanded], axis=1)

# Pivot the data so each wrapper is a column group
pivoted = combined.pivot(index='timestamp', columns='wrapper')

# Drop all-NaN columns
pivoted.dropna(axis=1, how='all', inplace=True)

# Save to CSV
output_file = "wrapper_comparison.csv"
pivoted.to_csv(output_file)
print(f"✅ Comparison saved to: {output_file}")
