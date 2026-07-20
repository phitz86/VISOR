#!/usr/bin/env python3
"""Check Data/TrackSections.json against real iRacing layout identities.

Mirrors TrackSectionCatalog.Resolve exactly (substring venue matching,
whole-word config matching) and runs it over every layout in
Planning/iracing-track-identities.json, assuming an EMPTY TrackConfigName —
the worst case, where resolution must succeed on the slug alone. Multi-config
venues do populate TrackConfigName in the sim, so a real session can only
resolve more than this report shows, never less.

Run from anywhere:  python3 tools/validate_track_catalog.py

Read the output as three buckets:
  RESOLVED    — layout gets section data; verify it maps to the RIGHT entry.
  GUARDED     — layout shares a venue with a catalog entry but is deliberately
                excluded (short/alternate configs must hide, not mislabel).
  NO VENUE    — nothing in the catalog matches; expected for uncovered tracks.
"""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def contains_token(haystack: str, key: str) -> bool:
    """Whole-word containment, mirroring TrackSectionCatalog.ContainsToken."""
    start = 0
    while True:
        i = haystack.find(key, start)
        if i < 0:
            return False
        end = i + len(key)
        left_ok = i == 0 or not haystack[i - 1].isalnum()
        right_ok = end == len(haystack) or not haystack[end].isalnum()
        if left_ok and right_ok:
            return True
        start = i + 1


def resolve(tracks, slug: str, display: str, config: str = ""):
    """Mirror of TrackSectionCatalog.Resolve."""
    haystack = f"{slug} {display}".lower()
    config_haystack = f"{config} {slug}".lower()
    config_agnostic = None
    for t in tracks:
        if not any(m in haystack for m in t["match"]):
            continue
        if not t["configs"]:
            config_agnostic = config_agnostic or t
        elif any(contains_token(config_haystack, c) for c in t["configs"]):
            return t
    return config_agnostic


def main() -> int:
    catalog = json.loads((ROOT / "Data" / "TrackSections.json").read_text(encoding="utf-8"))
    identities = json.loads(
        (ROOT / "Planning" / "iracing-track-identities.json").read_text(encoding="utf-8")
    )
    tracks = catalog["tracks"]

    resolved, guarded, no_venue = [], [], []
    for layout in identities["layouts"]:
        slug, display = layout["slug"], layout["display"]
        entry = resolve(tracks, slug, display)
        if entry is not None:
            resolved.append((slug, display, entry["track"]))
            continue
        venue_hit = any(
            any(m in f"{slug} {display}".lower() for m in t["match"]) for t in tracks
        )
        (guarded if venue_hit else no_venue).append((slug, display))

    print(f"RESOLVED ({len(resolved)}) — verify each maps to the RIGHT entry:")
    for slug, display, name in sorted(resolved):
        print(f"  '{slug}'  ({display})  ->  {name}")
    print(f"\nGUARDED ({len(guarded)}) — same venue, deliberately no data:")
    for slug, display in sorted(guarded):
        print(f"  '{slug}'  ({display})")
    print(f"\nNO VENUE MATCH ({len(no_venue)}) — tracks the catalog doesn't cover:")
    for slug, display in sorted(no_venue):
        print(f"  '{slug}'  ({display})")

    # Structural sanity of the catalog itself
    errors = []
    for t in tracks:
        pcts = [s["pct"] for s in t["sections"]]
        if pcts != sorted(pcts):
            errors.append(f"{t['track']}: sections out of order")
        if len(set(pcts)) != len(pcts):
            errors.append(f"{t['track']}: duplicate pct")
        if not pcts or pcts[0] != 0.0:
            errors.append(f"{t['track']}: first section must start at 0.0")
        if any(p < 0 or p >= 1 for p in pcts):
            errors.append(f"{t['track']}: pct out of [0,1) range")
        if not t["match"]:
            errors.append(f"{t['track']}: no match keys")
    if errors:
        print("\nCATALOG ERRORS:")
        for e in errors:
            print(f"  {e}")
        return 1
    print("\nCatalog structure: OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
