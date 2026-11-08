#!/usr/bin/env python3
"""Counts the rest-activation day-log lines R1/R3 write (the owner's Q5 answer: the logs count
the unintended opens — no recording first).

    python tools/activation_log_metrics.py eyectrl-20261108.log
    python tools/activation_log_metrics.py eyectrl-20261108.log --json

Stdlib only, by design (the kb_log_metrics.py precedent), Python 3.11+.

What it reads
-------------
Six kinds of line (all written by the App; any other line is ignored):

    Offer shown #N P=(x,y) B=(x,y) style=rest|button
    Offer expired unused #N (dwell P %) style=rest|button       [EyeCtrl.Core.GazeEngine]
    Offer fired #N style=rest|button
    Offer accepted #N → "Mode" at P=(x,y)
    Offer declined #N (reason)                                   reasons: stale, no control
                                                                 found, declined, activation error
    Activation outcome #N style=rest|button mode=ManualSelection|AutoSelection|None
                       result=action|closed|paused|other[ state=S] heldMs=MS   []

The bracketed source contexts are the REAL renders (the outcome line is written through the
static Serilog logger, so its context is empty; the fixture in the self-test copies real lines).

Read-outs (per style and overall)
---------------------------------
* rests started    the `Offer shown` count (a rest offer appears at the settle)
* cancelled        the `Offer expired unused` count (the look-away-mid-fill count; an expiry
                   writes NO outcome line, by design)
* fired            the `Offer fired` count — and `fired = accepted + declined`: a fire is
                   DECIDED on the UI thread (accepted, or declined with its reason), and a gap
                   means a crash or quit landed between the fire and the decision
* of accepted      the `Activation outcome` results: `action` (the session did something),
                   `closed` (magnifier or menu closed unused — the UNINTENDED-OPEN count),
                   `paused`, `other`. An accept still open at a day boundary, a crash or a quit
                   leaves no outcome line, and the note names the gap.

The tool reads only the file path it is handed; it touches no profile and no running app.
A line it cannot parse is skipped, never half-counted.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

SHOWN = re.compile(r"\bOffer shown #(\d+).*?\bstyle=(rest|button)\b")
EXPIRED = re.compile(r"\bOffer expired unused #(\d+).*?\bstyle=(rest|button)\b")
FIRED = re.compile(r"\bOffer fired #(\d+).*?\bstyle=(rest|button)\b")
# The real render quotes the enum (`→ "None"`, the MEL→Serilog bridge); the quotes stay optional
# so a sink that renders it bare still counts.
ACCEPTED = re.compile(r'\bOffer accepted #(\d+) → "?(\w+)"? at P=')
DECLINED = re.compile(r"\bOffer declined #(\d+)(?: \(([^)]*)\))?")
OUTCOME = re.compile(
    r"\bActivation outcome #(\d+) style=(rest|button) mode=(\w+)"
    r" result=(action|closed|paused|other)(?: state=(\w+))? heldMs=(\d+)")

STYLES = ("rest", "button")


def parse(lines) -> list[dict[str, str]]:
    """Every parseable activation line as a dict; anything else is skipped."""
    events: list[dict[str, str]] = []
    for raw in lines:
        line = raw.rstrip("\r\n")
        if m := SHOWN.search(line):
            events.append({"event": "shown", "id": m.group(1), "style": m.group(2)})
        elif m := EXPIRED.search(line):
            events.append({"event": "expired", "id": m.group(1), "style": m.group(2)})
        elif m := FIRED.search(line):
            events.append({"event": "fired", "id": m.group(1), "style": m.group(2)})
        elif m := ACCEPTED.search(line):
            events.append({"event": "accepted", "id": m.group(1), "mode": m.group(2)})
        elif m := DECLINED.search(line):
            events.append({"event": "declined", "id": m.group(1), "reason": m.group(2) or ""})
        elif m := OUTCOME.search(line):
            events.append({
                "event": "outcome", "id": m.group(1), "style": m.group(2),
                "mode": m.group(3), "result": m.group(4),
                "state": m.group(5) or "", "held_ms": m.group(6),
            })
    return events


def metrics(events: list[dict[str, str]]) -> dict:
    def by_style(event: str) -> Counter:
        return Counter(e["style"] for e in events if e["event"] == event)

    shown, expired, fired = by_style("shown"), by_style("expired"), by_style("fired")
    accepted = sum(1 for e in events if e["event"] == "accepted")
    declined_events = [e for e in events if e["event"] == "declined"]
    declined = len(declined_events)
    declined_reasons = Counter(e["reason"] or "unreasoned" for e in declined_events)
    outcomes = [e for e in events if e["event"] == "outcome"]

    results: Counter = Counter(e["result"] for e in outcomes)
    results_by_style = {s: Counter(e["result"] for e in outcomes if e["style"] == s)
                        for s in STYLES}
    held = sorted(int(e["held_ms"]) for e in outcomes if e["result"] == "closed")

    return {
        "rests_started": {s: shown[s] for s in STYLES},
        "rests_started_total": sum(shown.values()),
        "cancelled": {s: expired[s] for s in STYLES},
        "cancelled_total": sum(expired.values()),
        "fired": {s: fired[s] for s in STYLES},
        "fired_total": sum(fired.values()),
        # A fire is decided on the UI thread: accepted or declined (with its reason).
        "accepted": accepted,
        "declined": declined,
        "declined_reasons": dict(declined_reasons),
        "fired_undecided": sum(fired.values()) - accepted - declined,
        # The Q5 read: of the accepted, how many did something versus closed unused. An accept
        # still open at a day boundary, a crash or a quit writes no outcome line.
        "outcomes": dict(results),
        "outcomes_by_style": {s: dict(results_by_style[s]) for s in STYLES},
        "unintended_opens": results["closed"],
        "median_closed_held_ms": held[len(held) // 2] if held else None,
        "accepted_without_outcome": accepted - len(outcomes),
    }


def render(m: dict) -> str:
    def pair(d: dict) -> str:
        return "  ".join(f"{s} {d[s]}" for s in STYLES)

    out = [
        "Activation read-outs (rest activation)",
        "=" * 36,
        f"rests started      {m['rests_started_total']:>5}   ({pair(m['rests_started'])})",
        f"cancelled          {m['cancelled_total']:>5}   ({pair(m['cancelled'])})  "
        "(looked away mid-fill; writes no outcome line)",
        f"fired              {m['fired_total']:>5}   ({pair(m['fired'])})",
        f"accepted           {m['accepted']:>5}   declined {m['declined']}"
        + (f"  ({', '.join(f'{k} {v}' for k, v in sorted(m['declined_reasons'].items()))})"
           if m["declined_reasons"] else ""),
        f"of accepted: action {m['outcomes'].get('action', 0):>4}   closed "
        f"{m['outcomes'].get('closed', 0):>4} (UNINTENDED OPENS)   paused "
        f"{m['outcomes'].get('paused', 0):>3}   other {m['outcomes'].get('other', 0):>3}",
    ]
    for s in STYLES:
        r = m["outcomes_by_style"][s]
        out.append(f"  {s:<7} action {r.get('action', 0):>4}   closed {r.get('closed', 0):>4}"
                   f"   paused {r.get('paused', 0):>3}   other {r.get('other', 0):>3}")
    if m["median_closed_held_ms"] is not None:
        out.append(f"median held, closed {m['median_closed_held_ms']} ms")
    if m["accepted_without_outcome"]:
        out.append(f"NOTE  {m['accepted_without_outcome']} accepted with no outcome line"
                   " (day boundary, crash or quit)")
    if m["fired_undecided"]:
        out.append(f"NOTE  {m['fired_undecided']} fired with neither accepted nor declined"
                   " (a crash or quit between the fire and the decision)")
    return "\n".join(out)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="The rest-activation counts (started/cancelled/fired/accepted-vs-declined"
                    "/action-vs-closed) from a day log.")
    ap.add_argument("log", type=Path, help="an EyeCtrl day log (eyectrl-YYYYMMDD.log)")
    ap.add_argument("--json", action="store_true", help="print the counts as JSON")
    args = ap.parse_args(argv)

    try:
        lines = args.log.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError as e:
        print(f"cannot read {args.log}: {e}", file=sys.stderr)
        return 2

    events = parse(lines)
    if not events:
        print(f"no activation lines in {args.log}", file=sys.stderr)
        return 1

    result = metrics(events)
    print(json.dumps(result, indent=2, sort_keys=True) if args.json else render(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
