#!/usr/bin/env python3
"""Check the parts of CLAUDE.md that a text search can check.

  1. No review-finding IDs in comments, such as (B12) or (C4, C9). They point at
     Planning/Code Review 1.2.1.md, which the code should not depend on.
  2. No TEMP-DIAG markers. Temporary diagnostic logging carries the tag while a bug is
     chased and must be gone before the fix merges.
  3. No app source file over MAX_LINES. The target is about 500; this is the line past
     which a file has clearly grown, not a goal. Tests are exempt.

Run from anywhere:  python3 tools/check_conventions.py
Prints GitHub Actions annotations and exits 1 if anything is found.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MAX_LINES = 600
IGNORED_DIRS = {".git", "obj", "bin"}

# A comment (// , /// or <!-- ) with a finding ID in brackets: A1 architecture, B bugs,
# C consolidation, D dead code, F file size, P performance, S security, T tests.
FINDING_ID = r"[ABCDFPST]\d{1,2}"
REVIEW_ID = re.compile(rf"(?://|<!--).*\({FINDING_ID}(?:,\s*{FINDING_ID})*\)")


def source_files(suffixes, skip_tests):
    for path in sorted(ROOT.rglob("*")):
        parts = path.relative_to(ROOT).parts
        if path.suffix not in suffixes or not path.is_file():
            continue
        if any(p in IGNORED_DIRS for p in parts):
            continue
        if skip_tests and parts[0] == "Tests":
            continue
        yield path


def main() -> int:
    problems = []

    def report(path, line, message):
        rel = path.relative_to(ROOT).as_posix()
        problems.append(f"::error file={rel},line={line}::{message}")

    # Tests are checked for 1 and 2 as well; only the size limit skips them.
    for path in source_files({".cs", ".xaml"}, skip_tests=False):
        for number, text in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
            if REVIEW_ID.search(text):
                report(path, number, "Review finding ID in a comment; say why in words "
                                     "instead (see CLAUDE.md, Comments).")
            if "TEMP-DIAG" in text:
                report(path, number, "TEMP-DIAG marker: remove the temporary diagnostic "
                                     "before this merges (see CLAUDE.md, Logging).")

    for path in source_files({".cs"}, skip_tests=True):
        count = len(path.read_text(encoding="utf-8-sig").splitlines())
        if count > MAX_LINES:
            report(path, 1, f"{count} lines; over {MAX_LINES}. Split it (code moves only) "
                            f"or agree an exception (see CLAUDE.md, Size).")

    for line in problems:
        print(line)
    if problems:
        print(f"{len(problems)} convention problem(s).")
        return 1
    print("Conventions OK.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
