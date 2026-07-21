#!/usr/bin/env python3
"""Import measured turn positions from lovely-track-data into TrackSections.json.

Data source: https://github.com/Lovely-Sim-Racing/lovely-track-data
(c) Lovely Sim Racing (https://lsr.gg), CC BY-NC-SA 4.0. Turn names and
lap-fraction positions imported with attribution; see the catalog _readme.

Fetch the data first (not committed to this repo):
    git clone https://github.com/Lovely-Sim-Racing/lovely-track-data /tmp/lovely
Then run:
    python3 tools/import_track_sections.py --data-dir /tmp/lovely/data

Treatments, chosen per catalog entry:
  CALIBRATE  — curated entries keep their names and section order; boundaries
               are warped piecewise-linearly through anchors where a curated
               section name matches a measured turn/straight name (ALIASES
               bridges naming-tradition gaps, e.g. Curva Grande = Curva
               Biassono). Fixes drift in hand estimates, keeps curated naming.
  REGENERATE — used when the measured data contradicts curated geometry
               (max anchor shift > 0.30 — e.g. a wrongly assumed start/finish
               location) or when the entry is in FORCE_REGENERATE because the
               source's real corner names beat curated placeholders. Sections
               are rebuilt from the measured turns and named straights.
  UNCHANGED  — no measured data, fewer than 4 usable features, or the source
               only has anonymous T-numbers where the curated entry has real
               names (regenerating would lose information).

New entries are generated for measured layouts nothing in the catalog
resolves; they match on the exact slug ("=slug") so a generated 'lagunaseca'
can never swallow 'lagunaseca school'. Section 0.0 is always pinned and
monotonicity is enforced everywhere.
"""

import argparse
import json
import re
import sys
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOG = ROOT / "Data" / "TrackSections.json"

# Which lovely slug measures which curated entry (newest scan where several exist).
CURATED_SOURCES = {
    "Spa-Francorchamps": "spa 2024 up",
    "Monza": "monza full",
    "Silverstone": "silverstone 2019 gp",
    "Nürburgring Nordschleife": "nurburgring nordschleife",
    "Nürburgring Combined (24h/VLN)": "nurburgring combined",
    "Watkins Glen (Boot)": "watkinsglen 2021 fullcourse",
    "Road America": "roadamerica full",
    "Laguna Seca": "lagunaseca",
    "Suzuka": "suzuka grandprix",
    "Interlagos": "interlagos gp",
    "Mount Panorama": "bathurst",
    "Brands Hatch": "brandshatch grandprix",
    "Zandvoort": "zandvoort 2023 gp",
    "Red Bull Ring": "spielberg gp",
    "Imola": "imola gp",
    "Sebring": "sebring international",
    "Circuit of the Americas": "cota-gp",
    "Daytona Road Course": "daytona 2011 road",
    "Lime Rock Park": "limerock 2019 classic",
    "Le Mans (Circuit de la Sarthe)": "lemans full",
    "Le Mans (No Chicanes)": "lemans nochicane",
    "Hockenheimring": "hockenheim gp",
    "Mugello": "mugello gp",
    "Circuit de Barcelona-Catalunya": "barcelona gp",
    "Road Atlanta": "roadatlanta full",
    "Okayama International Circuit": "okayama full",
    "Virginia International Raceway": "virginia 2022 full",
}

# Source names win over curated placeholders on these entries.
FORCE_REGENERATE = {"Sebring", "Hockenheimring", "Red Bull Ring"}

# curated name -> measured name, where tradition and dataset disagree.
ALIASES = {
    "Monza": {
        "Variante del Rettifilo": "Prima Variante",
        "Curva Grande": "Curva Biassono",
        "Variante della Roggia": "Seconda Variante",
        "Lesmo 1": "Prima Curva di Lesmo",
        "Lesmo 2": "Seconda Curva di Lesmo",
    },
    "Le Mans (Circuit de la Sarthe)": {
        "Dunlop Chicane": "Ralentisseur Dunlop",
        "Dunlop Curve": "Courbe Dunlop",
        "Esses de la Forêt": "S du Tetre Rouge",
        "First Chicane": "1er Ralentisseur",
        "Second Chicane": "2e Ralentisseur",
        "Mulsanne Corner": "Virage de Mulsanne",
        "Porsche Curves": "Virage Porsche",
        "Ford Chicanes": "Virage Ford",
    },
    "Le Mans (No Chicanes)": {
        "Dunlop Chicane": "Ralentisseur Dunlop",
        "Dunlop Curve": "Courbe Dunlop",
        "Esses de la Forêt": "S du Tetre Rouge",
        "Mulsanne Corner": "Virage de Mulsanne",
        "Porsche Curves": "Virage Porsche",
        "Ford Chicanes": "Virage Ford",
    },
    "Brands Hatch": {"Sheene Curve": "Sheene's"},
    "Silverstone": {"Maggotts": "Maggots"},
}

# ASCII transliterations and known misspellings in the source data.
NAME_FIXES = {
    "Hoheneichen": "Hocheichen",
    "Quiddelbacher Hoehe": "Quiddelbacher Höhe",
    "Fuchsroehre": "Fuchsröhre",
    "ExMuehle": "Ex-Mühle",
    "Ex-Muehle": "Ex-Mühle",
    "Bruennchen": "Brünnchen",
    "Hedwigshoehe": "Hedwigshöhe",
    "Doettinger Hoehe": "Döttinger Höhe",
    "Schwedenkreutz": "Schwedenkreuz",
    "Suedkurve": "Südkurve",
    "Wuerth": "Würth",
}

LEAD_IN = 0.010          # label a touch before the measured turn-in point
MIN_GAP = 0.002          # enforced spacing between section boundaries
STRAIGHT_GAP = 0.055     # unnamed gap longer than this gets a "Run to X" section
MAX_ANCHOR_SHIFT = 0.30  # larger disagreement means the curated geometry is wrong
MIN_ANCHORS = 5          # fewer than this and calibration is not trustworthy
TURN_NUMBER = re.compile(r"turn \d+[ab]?$")


def norm_name(name: str) -> str:
    s = unicodedata.normalize("NFD", name.lower())
    s = "".join(c for c in s if not unicodedata.combining(c))
    s = re.sub(r"\(.*?\)", " ", s)
    s = re.sub(r"\bt(\d+[ab]?)\b", r"turn \1", s)
    s = re.sub(r"[^a-z0-9 ]", " ", s)
    return " ".join(s.split())


def names_match(a: str, b: str) -> bool:
    na, nb = norm_name(a), norm_name(b)
    if not na or not nb:
        return False
    if na == nb or na in nb or nb in na:
        return True
    ca, cb = na.replace(" ", ""), nb.replace(" ", "")
    return ca == cb or ca in cb or cb in ca


def pretty_name(name: str) -> str:
    name = NAME_FIXES.get(name, name)
    m = re.fullmatch(r"T(\d+[ab]?)", name)
    if m:
        return f"Turn {m.group(1)}"
    if name.isupper() and len(name) > 3:
        return name.title()
    return name


def measured_features(lovely: dict) -> list[dict]:
    """Turns + named straights, deduped (chicane apexes share a name), lap order."""
    feats = []
    for kind in ("turn", "straight"):
        for f in lovely.get(kind, []):
            name = str(f.get("name", "")).strip()
            start = f.get("start")
            if start is None and f.get("marker") is not None:
                start = max(0.0, f["marker"] - 0.02)
            if not name or start is None:
                continue
            feats.append({"name": pretty_name(name), "start": round(float(start), 4),
                          "end": f.get("end"), "kind": kind})
    feats.sort(key=lambda f: f["start"])
    deduped = []
    for f in feats:
        if deduped and deduped[-1]["name"] == f["name"]:
            deduped[-1]["end"] = f.get("end") or deduped[-1]["end"]
            continue
        deduped.append(f)
    return deduped


def enforce_monotonic(sections: list[dict]) -> list[dict]:
    out = []
    for s in sections:
        pct = 0.0 if not out else max(s["pct"], out[-1]["pct"] + MIN_GAP)
        if pct >= 1.0:
            continue
        out.append({"pct": round(pct, 3), "name": s["name"]})
    out[0]["pct"] = 0.0
    return out


def calibrate(entry: dict, feats: list[dict]) -> tuple[list[dict], dict]:
    """Warp curated boundaries through name-matched anchors.

    Each section (except the pinned 0.0 one) anchors to the NEAREST matching
    feature by lap position — never to a far-away same-named feature, so a
    wrap-around straight at 0.0 cannot swallow its end-of-lap twin. Pairs that
    would break monotonicity are dropped.
    """
    aliases = ALIASES.get(entry["track"], {})

    def matches(sec_name: str, feat_name: str) -> bool:
        alias = aliases.get(sec_name)
        if alias is not None and names_match(alias, feat_name):
            return True
        return names_match(sec_name, feat_name)

    def is_exact(sec_name: str, feat_name: str) -> bool:
        alias = aliases.get(sec_name, sec_name)
        return norm_name(alias) == norm_name(feat_name)

    anchors = []  # (old, new, exact)
    for sec in entry["sections"][1:]:
        cands = [f for f in feats if matches(sec["name"], f["name"])]
        if cands:
            best = min(cands, key=lambda f: abs(f["start"] - sec["pct"]))
            anchors.append((sec["pct"], max(0.0, best["start"] - LEAD_IN),
                            is_exact(sec["name"], best["name"])))

    # "Descent to Eau Rouge" contains "Eau Rouge" and would steal its anchor;
    # when two sections claim the same measured position, the exact-name match
    # keeps it and the containment match falls back to interpolation.
    by_target = {}
    for old, new, exact in anchors:
        cur = by_target.get(new)
        if cur is None or (exact and not cur[2]):
            by_target[new] = (old, new, exact)
    anchors = sorted(by_target.values())

    monotonic = [(0.0, 0.0)]
    for old, new, _exact in anchors:
        if old > monotonic[-1][0] and new > monotonic[-1][1]:
            monotonic.append((old, new))
    monotonic.append((1.0, 1.0))

    max_shift = max((abs(o - n) for o, n in monotonic), default=0.0)

    def warp(p: float) -> float:
        for (o0, n0), (o1, n1) in zip(monotonic, monotonic[1:]):
            if o0 <= p <= o1:
                t = 0.0 if o1 == o0 else (p - o0) / (o1 - o0)
                return n0 + t * (n1 - n0)
        return p

    sections = [{"pct": warp(s["pct"]), "name": s["name"]} for s in entry["sections"]]
    stats = {"anchors": len(monotonic) - 2, "of": len(entry["sections"]),
             "max_shift": round(max_shift, 3)}
    return enforce_monotonic(sections), stats


def generate(feats: list[dict]) -> list[dict]:
    """Build a section list purely from measured features."""
    sections = []
    for i, f in enumerate(feats):
        sections.append({"pct": max(0.0, f["start"] - LEAD_IN), "name": f["name"]})
        nxt = feats[i + 1]["start"] if i + 1 < len(feats) else 1.0
        end = f.get("end")
        if end and f["kind"] == "turn" and nxt - end > STRAIGHT_GAP:
            nxt_name = feats[i + 1]["name"] if i + 1 < len(feats) else feats[0]["name"]
            sections.append({"pct": end + MIN_GAP, "name": f"Run to {nxt_name}"})
    if not sections:
        return []
    if sections[0]["pct"] > 0.025:
        sections.insert(0, {"pct": 0.0, "name": "Start/Finish Straight"})
    else:
        sections[0]["pct"] = 0.0
    return enforce_monotonic(sections)


def real_name_ratio(feats: list[dict]) -> float:
    if not feats:
        return 0.0
    real = sum(1 for f in feats if not TURN_NUMBER.fullmatch(norm_name(f["name"])))
    return real / len(feats)


def write_catalog(d: dict) -> None:
    def jstr(s):
        return json.dumps(s, ensure_ascii=False)

    lines = ["{", '  "_readme": [']
    for i, s in enumerate(d["_readme"]):
        lines.append(f"    {jstr(s)}" + ("," if i < len(d["_readme"]) - 1 else ""))
    lines += ["  ],", '  "tracks": [']
    for ti, t in enumerate(d["tracks"]):
        lines.append("    {")
        lines.append(f'      "track": {jstr(t["track"])},')
        if t.get("source"):
            lines.append(f'      "source": {jstr(t["source"])},')
        lines.append(f'      "match": [{", ".join(jstr(m) for m in t["match"])}],')
        lines.append(f'      "configs": [{", ".join(jstr(c) for c in t["configs"])}],')
        lines.append('      "sections": [')
        for si, s in enumerate(t["sections"]):
            comma = "," if si < len(t["sections"]) - 1 else ""
            lines.append(f'        {{ "pct": {s["pct"]:.3f}, "name": {jstr(s["name"])} }}{comma}')
        lines.append("      ]")
        lines.append("    }" + ("," if ti < len(d["tracks"]) - 1 else ""))
    lines += ["  ]", "}"]
    CATALOG.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data-dir", required=True,
                    help="directory containing manifest.json and iracing/*.json")
    args = ap.parse_args()
    data_dir = Path(args.data_dir)

    manifest = json.loads((data_dir / "manifest.json").read_text(encoding="utf-8"))
    lovely = {}
    for e in manifest["tracks"]["iracing"]:
        f = data_dir / e["path"]
        if f.exists():
            lovely[e["trackId"]] = json.loads(f.read_text(encoding="utf-8"))

    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))

    print("== curated entries ==")
    for entry in catalog["tracks"]:
        slug = CURATED_SOURCES.get(entry["track"])
        feats = measured_features(lovely[slug]) if slug and slug in lovely else []
        if len(feats) < 4:
            entry.setdefault("source", "curated")
            print(f"  {entry['track']}: no usable measured data, unchanged")
            continue

        sections, stats = calibrate(entry, feats)
        ratio = real_name_ratio(feats)
        detail = (f"anchors {stats['anchors']}/{stats['of']}, "
                  f"max shift {stats['max_shift']}, named {ratio:.0%}")

        force = entry["track"] in FORCE_REGENERATE
        geometry_wrong = stats["max_shift"] > MAX_ANCHOR_SHIFT and ratio >= 0.6
        if force or geometry_wrong:
            entry["sections"] = generate(feats)
            entry["source"] = "lovely-track-data (regenerated)"
            why = "forced" if force else "geometry mismatch"
            print(f"  {entry['track']}: REGENERATED from {slug} ({why}; {detail})")
        elif stats["anchors"] >= MIN_ANCHORS:
            entry["sections"] = sections
            entry["source"] = "curated, calibrated vs lovely-track-data"
            print(f"  {entry['track']}: calibrated vs {slug} ({detail})")
        elif ratio >= 0.6:
            entry["sections"] = generate(feats)
            entry["source"] = "lovely-track-data (regenerated)"
            print(f"  {entry['track']}: REGENERATED from {slug} (names disjoint; {detail})")
        else:
            entry.setdefault("source", "curated")
            print(f"  {entry['track']}: kept curated (source mostly unnamed; {detail})")

    # Layouts with measured turns that nothing in the catalog resolves yet.
    sys.path.insert(0, str(ROOT / "tools"))
    from validate_track_catalog import resolve

    print("\n== generated entries ==")
    generated = 0
    for slug, data in sorted(lovely.items()):
        feats = measured_features(data)
        if len(feats) < 4 or resolve(catalog["tracks"], slug, data.get("name", "")):
            continue
        catalog["tracks"].append({
            "track": data.get("name", slug),
            "source": "lovely-track-data",
            "match": ["=" + slug],
            "configs": [],
            "sections": generate(feats),
        })
        generated += 1
        print(f"  + {data.get('name', slug)} ('{slug}')")

    print(f"\n{generated} entries generated, {len(catalog['tracks'])} total")
    write_catalog(catalog)
    return 0


if __name__ == "__main__":
    sys.exit(main())
