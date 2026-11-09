#!/usr/bin/env python3
"""Counts the activation-offer day-log lines (the owner's ask: the logs count the unintended
opens — no recording first). Salvaged from #322 for the round activation button (B1).

    python tools/activation_log_metrics.py eyectrl-20261109.log
    python tools/activation_log_metrics.py eyectrl-20261109.log --json

Stdlib only, by design (the kb_log_metrics.py precedent), Python 3.11+.

What it reads
-------------
Six kinds of line (all written by the App; any other line is ignored). Two generations parse:

* current (one activation style, the offer button — no `style=`; `firstLookMs` on fired/expired):

    Offer shown #N P=(x,y) B=(x,y)
    Offer expired unused #N (dwell P %) firstLookMs=MS|none     [EyeCtrl.Core.GazeEngine]
    Offer fired #N firstLookMs=MS|none
    Offer accepted #N → "Mode" at P=(x,y)
    Offer declined #N (reason)                                   reasons: stale, no control
                                                                 found, declined, activation error
    Activation outcome #N mode=ManualSelection|AutoSelection|None
                       result=action|closed|paused|other[ state=S] heldMs=MS   []

* the 1.3.0 sitting build (#322): the same lines with a `style=rest|button` field and no
  `firstLookMs`. A line WITHOUT `style=` counts as `button` (the only style there is now).

The bracketed source contexts are the REAL renders (the outcome line is written through the
static Serilog logger, so its context is empty; the fixture in the self-test copies real lines).
Every pattern is anchored at the line's end, so a cut-off line is skipped, never half-counted.

Read-outs (overall, and per style when an old log has rest offers in it)
------------------------------------------------------------------------
* offers shown     the `Offer shown` count
* expired          the `Offer expired unused` count (an expiry writes NO outcome line, by design)
* never looked at  of the expired, how many carry `firstLookMs=none` (the button popped up and
                   the eye never went to it); old lines without the field are not counted here
* fired            the `Offer fired` count — and `fired = accepted + declined`: a fire is DECIDED
                   on the UI thread (accepted, or declined with its reason), and a gap means a
                   crash or quit landed between the fire and the decision
* first look       the median `firstLookMs` of the fired offers that carry one
* of accepted      the `Activation outcome` results: `action` (the session did something),
                   `closed` (magnifier or menu closed unused — the UNINTENDED-OPEN count),
                   `paused`, `other`. An accept still open at a day boundary, a crash or a quit
                   leaves no outcome line, and the note names the gap.

The tool reads only the file path it is handed; it touches no profile and no running app.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

# The optional pieces, shared by every pattern. `style=` is absent on current lines (= button);
# `firstLookMs` is absent on 1.3.0 lines. Values may be quoted by a sink that renders strings
# with quotes; the file sink's `{Message:lj}` writes them bare.
_STYLE = r'(?: style="?(rest|button)"?)?'
_FIRST_LOOK = r'(?: firstLookMs="?(\d+|none)"?)?'
_END = r"\s*$"

SHOWN = re.compile(r"\bOffer shown #(\d+) P=\(-?\d+,-?\d+\) B=\(-?\d+,-?\d+\)" + _STYLE + _END)
EXPIRED = re.compile(r"\bOffer expired unused #(\d+) \(dwell [^)]*\)" + _STYLE + _FIRST_LOOK + _END)
FIRED = re.compile(r"\bOffer fired #(\d+)" + _STYLE + _FIRST_LOOK + _END)
# The real render quotes the enum (`→ "None"`, the MEL→Serilog bridge); the quotes stay optional
# so a sink that renders it bare still counts.
ACCEPTED = re.compile(r'\bOffer accepted #(\d+) → "?(\w+)"? at P=')
DECLINED = re.compile(r"\bOffer declined #(\d+)(?: \(([^)]*)\))?")
OUTCOME = re.compile(
    r"\bActivation outcome #(\d+)" + _STYLE + r" mode=(\w+)"
    r" result=(action|closed|paused|other)(?: state=(\w+))? heldMs=(\d+)" + _END)

STYLES = ("button", "rest")
DEFAULT_STYLE = "button"


def parse(lines) -> list[dict[str, str]]:
    """Every parseable activation line as a dict; anything else is skipped."""
    events: list[dict[str, str]] = []
    for raw in lines:
        line = raw.rstrip("\r\n")
        if m := SHOWN.search(line):
            events.append({"event": "shown", "id": m.group(1), "style": m.group(2) or DEFAULT_STYLE})
        elif m := EXPIRED.search(line):
            events.append({"event": "expired", "id": m.group(1), "style": m.group(2) or DEFAULT_STYLE,
                           "first_look": m.group(3) or ""})
        elif m := FIRED.search(line):
            events.append({"event": "fired", "id": m.group(1), "style": m.group(2) or DEFAULT_STYLE,
                           "first_look": m.group(3) or ""})
        elif m := ACCEPTED.search(line):
            events.append({"event": "accepted", "id": m.group(1), "mode": m.group(2)})
        elif m := DECLINED.search(line):
            events.append({"event": "declined", "id": m.group(1), "reason": m.group(2) or ""})
        elif m := OUTCOME.search(line):
            events.append({
                "event": "outcome", "id": m.group(1), "style": m.group(2) or DEFAULT_STYLE,
                "mode": m.group(3), "result": m.group(4),
                "state": m.group(5) or "", "held_ms": m.group(6),
            })
    return events


def _median(values: list[int]) -> int | None:
    values = sorted(values)
    return values[len(values) // 2] if values else None


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
    held = [int(e["held_ms"]) for e in outcomes if e["result"] == "closed"]
    first_looks = [int(e["first_look"]) for e in events
                   if e["event"] == "fired" and e["first_look"].isdigit()]

    return {
        "offers_shown": {s: shown[s] for s in STYLES},
        "offers_shown_total": sum(shown.values()),
        "expired": {s: expired[s] for s in STYLES},
        "expired_total": sum(expired.values()),
        # Only lines that carry the field: a 1.3.0 expiry says nothing about where the eye went.
        "expired_never_looked_at": sum(1 for e in events
                                       if e["event"] == "expired" and e["first_look"] == "none"),
        "fired": {s: fired[s] for s in STYLES},
        "fired_total": sum(fired.values()),
        "median_fired_first_look_ms": _median(first_looks),
        # A fire is decided on the UI thread: accepted or declined (with its reason).
        "accepted": accepted,
        "declined": declined,
        "declined_reasons": dict(declined_reasons),
        "fired_undecided": sum(fired.values()) - accepted - declined,
        # Of the accepted, how many did something versus closed unused. An accept still open at
        # a day boundary, a crash or a quit writes no outcome line.
        "outcomes": dict(results),
        "outcomes_by_style": {s: dict(results_by_style[s]) for s in STYLES},
        "unintended_opens": results["closed"],
        "median_closed_held_ms": _median(held),
        "accepted_without_outcome": accepted - len(outcomes),
    }


def render(m: dict) -> str:
    # The per-style split only matters for a 1.3.0 log that mixed rest and button offers.
    split = any(m[k]["rest"] for k in ("offers_shown", "expired", "fired"))

    def pair(d: dict) -> str:
        return f"   ({'  '.join(f'{s} {d[s]}' for s in STYLES)})" if split else ""

    out = [
        "Activation read-outs (offers)",
        "=" * 29,
        f"offers shown       {m['offers_shown_total']:>5}{pair(m['offers_shown'])}",
        f"expired            {m['expired_total']:>5}{pair(m['expired'])}  "
        "(no fire; writes no outcome line)",
        f"  never looked at  {m['expired_never_looked_at']:>5}   (expired with firstLookMs=none)",
        f"fired              {m['fired_total']:>5}{pair(m['fired'])}",
        f"accepted           {m['accepted']:>5}   declined {m['declined']}"
        + (f"  ({', '.join(f'{k} {v}' for k, v in sorted(m['declined_reasons'].items()))})"
           if m["declined_reasons"] else ""),
        f"of accepted: action {m['outcomes'].get('action', 0):>4}   closed "
        f"{m['outcomes'].get('closed', 0):>4} (UNINTENDED OPENS)   paused "
        f"{m['outcomes'].get('paused', 0):>3}   other {m['outcomes'].get('other', 0):>3}",
    ]
    if split:
        for s in STYLES:
            r = m["outcomes_by_style"][s]
            out.append(f"  {s:<7} action {r.get('action', 0):>4}   closed {r.get('closed', 0):>4}"
                       f"   paused {r.get('paused', 0):>3}   other {r.get('other', 0):>3}")
    if m["median_fired_first_look_ms"] is not None:
        out.append(f"median first look, fired {m['median_fired_first_look_ms']} ms")
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
        description="The activation-offer counts (shown/expired/fired/accepted-vs-declined"
                    "/action-vs-closed, first look) from a day log.")
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
