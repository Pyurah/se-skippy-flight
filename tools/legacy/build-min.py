#!/usr/bin/env python
"""
build-min.py - produce comment-stripped *.min.cs deploy artifacts for pasting
into a Programmable Block.

Space Engineers caps a PB script at 100,000 source characters, counting comments
and whitespace. The .cs sources are kept fully commented as the source of truth;
this tool emits the deploy artifacts the game actually runs, well under the cap.
Each script listed in SCRIPTS is stripped and size/brace-checked independently
(every PB is its own paste target with its own 100k budget): SkippyFlight.cs and
SkippyTower.cs.

What it strips (character-state aware, so it never touches a "//" or "/*" that
lives inside a string or char literal):
  - // line comments
  - /* ... */ block comments
  - blank / whitespace-only lines
  - trailing whitespace on every line

What it preserves: all code, string/char literals verbatim, and line indentation
(so the min file stays glanceable). Line numbers do NOT match the source once
comments and blank lines are removed - debug against SkippyFlight.cs, not the min.

Usage (from anywhere):
    python tools/build-min.py
Writes each *.min.cs next to its source and prints a size report per file.
Exits non-zero if any output exceeds the limit or its braces don't balance.
"""

import os
import sys

LIMIT = 100_000

# Character-state machine states.
CODE, LINE_COMMENT, BLOCK_COMMENT, STRING, VERBATIM, CHAR = range(6)


def strip_comments(src):
    """Return src with C# comments removed, literals left intact."""
    out = []
    i = 0
    n = len(src)
    state = CODE
    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ""

        if state == CODE:
            if c == "/" and nxt == "/":
                state = LINE_COMMENT
                i += 2
                continue
            if c == "/" and nxt == "*":
                state = BLOCK_COMMENT
                i += 2
                continue
            if c == "@" and nxt == '"':
                out.append(c)
                out.append(nxt)
                state = VERBATIM
                i += 2
                continue
            if c == '"':
                out.append(c)
                state = STRING
                i += 1
                continue
            if c == "'":
                out.append(c)
                state = CHAR
                i += 1
                continue
            out.append(c)
            i += 1
            continue

        if state == LINE_COMMENT:
            # Keep the newline so line structure survives.
            if c == "\n":
                out.append(c)
                state = CODE
            i += 1
            continue

        if state == BLOCK_COMMENT:
            if c == "*" and nxt == "/":
                state = CODE
                i += 2
                continue
            # Preserve newlines inside block comments so lines don't merge.
            if c == "\n":
                out.append(c)
            i += 1
            continue

        if state == STRING:
            out.append(c)
            if c == "\\":  # escape: copy the next char verbatim
                if i + 1 < n:
                    out.append(nxt)
                    i += 2
                    continue
            elif c == '"':
                state = CODE
            i += 1
            continue

        if state == VERBATIM:
            # In a verbatim string, "" is an escaped quote; \ is literal.
            if c == '"' and nxt == '"':
                out.append(c)
                out.append(nxt)
                i += 2
                continue
            out.append(c)
            if c == '"':
                state = CODE
            i += 1
            continue

        if state == CHAR:
            out.append(c)
            if c == "\\":
                if i + 1 < n:
                    out.append(nxt)
                    i += 2
                    continue
            elif c == "'":
                state = CODE
            i += 1
            continue

    return "".join(out)


def drop_blank_lines(src):
    """Trim trailing whitespace and drop whitespace-only lines."""
    kept = [ln.rstrip() for ln in src.splitlines()]
    kept = [ln for ln in kept if ln.strip() != ""]
    return "\n".join(kept) + "\n"


# Each SE Programmable Block script is its own paste target with its own 100k cap,
# so every (source -> min) pair is stripped and checked independently.
SCRIPTS = [
    ("SkippyFlight.cs", "SkippyFlight.min.cs"),
    ("SkippyTower.cs", "SkippyTower.min.cs"),
]


def build_one(root, src_name, out_name):
    """Strip one script and report; return a list of problem strings (empty = OK)."""
    src_path = os.path.join(root, src_name)
    out_path = os.path.join(root, out_name)

    with open(src_path, "r", encoding="utf-8") as f:
        src = f.read()

    stripped = drop_blank_lines(strip_comments(src))

    before = len(src)
    after = len(stripped)
    opens = stripped.count("{")
    closes = stripped.count("}")

    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(stripped)

    saved = before - after
    pct = (saved / before * 100) if before else 0
    print(f"source : {before:>7,} chars  ({src_name})")
    print(f"min    : {after:>7,} chars  ({out_name})")
    print(f"saved  : {saved:>7,} chars  ({pct:.1f}%)")
    print(f"headroom under {LIMIT:,}: {LIMIT - after:,} chars")
    print(f"braces : {{ {opens}  }} {closes}  {'OK' if opens == closes else 'MISMATCH'}")

    problems = []
    if opens != closes:
        problems.append(f"{src_name}: brace mismatch in output")
    if after > LIMIT:
        problems.append(f"{src_name}: output exceeds {LIMIT:,}-char limit")
    return problems


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)

    problems = []
    for i, (src_name, out_name) in enumerate(SCRIPTS):
        if not os.path.exists(os.path.join(root, src_name)):
            continue
        if i:
            print()
        problems += build_one(root, src_name, out_name)

    if problems:
        print("FAIL: " + "; ".join(problems), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
