#!/usr/bin/env python3
"""Reads EyeCtrl's keyboard E1 lines from a day log and prints the read-outs the C2 rule needs.

    python tools/kb_log_metrics.py eyectrl-20261019.log
    python tools/kb_log_metrics.py eyectrl-20261019.log --edge top
    python tools/kb_log_metrics.py eyectrl-20261019.log --json

Stdlib only, by design (the strings_validate.py / palette_validate.py precedent), Python 3.11+.

What it reads
-------------
Every line the keyboard writes as ``KB-E1 <event> k=v k=v ...`` (src/EyeCtrl.Core/Keyboard/
KeyboardE1Recorder.cs). Any other line in the file is ignored, as is a KB-E1 line it cannot
parse: a half-written last line must not sink the whole report. The lines are content-free by
contract -- geometry, key CLASSES, timings, lengths, the foreground process NAME -- so this
script never sees a typed character, and cannot print one.

The events: key, holdon, abandon, pick, pickundo, space, mode, modeundo, edge, shown, hidden,
mirror. ``key`` carries the whole activation (seq, ts, r/c, cls, act, boxw/boxh, sub, edge,
win*, movems, dx/dy, mdx/mdy, restarts, saccade, sincekey, dwell, penalty, typing, bs, likely,
start, nocont); ``holdon`` and ``space`` follow a key line and name it by ``seq``.

Read-outs (steward's C2 rule, evidence-steward "C2/C2b/C2c")
------------------------------------------------------------
* letters            letter activations
* slips              a letter whose NEXT activation is a Backspace inside the current word
                     (``bs=word``), no more than 3 s later by the log's own clock. A Backspace
                     into committed text is editing, not a slip, and does not count.
* slip rate          slips / letters
* likely=0 share     of slipped letters, those whose letter was NOT in the predictor's top-5
                     continuation set, excluding word starts (``start``) and prefixes with no
                     continuation (``nocont``). The rule wants n >= 20 before it is read.
* mode-undos         ``modeundo`` events per 100 letters
* hold-on            by dwell class (unpenalised letters, penalised letters, mode keys): n,
                     median ms, and the share >= 150 ms (the uniform-600 gate wants >= 90 %)
* pick-then-undo     ``pickundo`` events / picks
* chain-picks        a pick < 1 s after the previous pick with no key activation in between
* near-move          activations within 1 s of a window move (``movems``)
* bar empty          share of Spaces after which the bar was empty
* mirror             verdicts (match / mismatch / unsupported / suppressed / empty), the
                     divergence rate over match+mismatch, and the same per foreground process
* hides              keyboard hides by cause

``--edge top|bottom`` restricts every read-out to events that happened while the keyboard was
docked there (the dock edge is tracked from shown / flip / key lines): a Flip moves the keyboard
into a different bias region, so the steward stratifies by edge.
"""

from __future__ import annotations

import argparse
import json
import re
import statistics
import sys
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path
from typing import Any

_EPOCH = datetime(1970, 1, 1)

# 2026-10-19 21:57:05.123 [INF] [EyeCtrl.ViewModels.KeyboardViewModel] KB-E1 key seq=1 ...
LINE = re.compile(
    r"^(?P<ts>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[(?P<lvl>\w+)\] \[[^\]]*\] "
    r"KB-E1 (?P<event>[a-z]+)(?P<rest>.*)$"
)
FIELD = re.compile(r"(\w+)=(\S+)")

SLIP_WINDOW_MS = 3000
CHAIN_PICK_MS = 1000
NEAR_MOVE_MS = 1000
HOLD_ON_GATE_MS = 150
MODE_CLASSES = {"shift", "caps", "mode", "switchsub"}

# The steward's minimums before the C2 rule may be read.
MIN_LETTERS = 150
MIN_SLIPS = 20


def _value(raw: str) -> Any:
    raw = raw.strip('"')
    if raw in ("True", "true"):
        return True
    if raw in ("False", "false"):
        return False
    try:
        return int(raw)
    except ValueError:
        return raw


def _millis(stamp: str) -> int:
    """The stamp as milliseconds on one naive clock (no time zone, so no DST trap in deltas)."""
    dt = datetime.strptime(stamp, "%Y-%m-%d %H:%M:%S.%f")
    return round((dt - _EPOCH).total_seconds() * 1000)


# Per event: the fields that must be present, and their types. A KB-E1 line that fails this is
# damaged (a crash cut it off, a hand edit mangled it) and is skipped, never half-counted.
REQUIRED: dict[str, dict[str, type]] = {
    "key": {"seq": int, "ts": int, "cls": str, "likely": bool, "start": bool, "nocont": bool,
            "penalty": bool, "bs": str, "movems": int},
    "holdon": {"seq": int, "ms": int},
    "abandon": {"ts": int, "cls": str},
    "pick": {"slot": int, "src": str, "sincekey": int, "sincepick": int},
    "pickundo": {"ms": int},
    "space": {"seq": int, "empty": bool},
    "mode": {"kind": str},
    "modeundo": {"kind": str},
    "edge": {"button": str},
    "shown": {"proc": str, "edge": str},
    "hidden": {"cause": str},
    "mirror": {"res": str},
}


def _valid(event: str, fields: dict[str, Any]) -> bool:
    spec = REQUIRED.get(event)
    if spec is None:
        return False
    return all(isinstance(fields.get(name), kind) and not (kind is int and isinstance(fields[name], bool))
               for name, kind in spec.items())


def parse(lines) -> list[dict[str, Any]]:
    """Every parseable KB-E1 line as a dict: ``event``, ``at`` (ms, log clock), ``edge`` (the dock
    edge in force at that point, or None), and the line's own fields."""
    events: list[dict[str, Any]] = []
    edge: str | None = None
    for raw in lines:
        m = LINE.match(raw.rstrip("\r\n"))
        if not m:
            continue
        try:
            fields = {k: _value(v) for k, v in FIELD.findall(m.group("rest"))}
            at = _millis(m.group("ts"))
        except ValueError:
            continue
        event = m.group("event")
        if not _valid(event, fields):
            continue
        if event in ("shown", "key") and isinstance(fields.get("edge"), str) and fields["edge"] != "-":
            edge = fields["edge"]
        elif event == "edge" and fields.get("button") == "flip" and isinstance(fields.get("edge"), str):
            edge = fields["edge"]
        events.append({"event": event, "at": at, "dock": edge, **fields})
    return events


def _share(part: int, whole: int) -> float | None:
    return None if whole == 0 else part / whole


def _hold_stats(values: list[int]) -> dict[str, Any]:
    return {
        "n": len(values),
        "median_ms": statistics.median(values) if values else None,
        "share_ge_150ms": _share(sum(1 for v in values if v >= HOLD_ON_GATE_MS), len(values)),
    }


def metrics(events: list[dict[str, Any]], edge: str | None = None) -> dict[str, Any]:
    if edge is not None:
        events = [e for e in events if e["dock"] == edge]

    keys = [e for e in events if e["event"] == "key"]
    by_seq = {e["seq"]: e for e in keys if "seq" in e}
    letters = [k for k in keys if k.get("cls") == "letter"]

    # --- slips: a letter immediately followed (next activation) by an in-word Backspace <= 3 s.
    slipped: list[dict[str, Any]] = []
    for prev, nxt in zip(keys, keys[1:]):
        if (prev.get("cls") == "letter" and nxt.get("cls") == "backspace"
                and nxt.get("bs") == "word" and 0 <= nxt["at"] - prev["at"] <= SLIP_WINDOW_MS):
            slipped.append(prev)
    countable = [k for k in slipped if not k.get("start") and not k.get("nocont")]
    unlikely = [k for k in countable if not k.get("likely")]

    # --- hold-on, by dwell class
    holds: dict[str, list[int]] = {"letters_unpenalised": [], "letters_penalised": [], "mode_keys": []}
    for h in (e for e in events if e["event"] == "holdon"):
        k = by_seq.get(h.get("seq"))
        if k is None or not isinstance(h.get("ms"), int):
            continue
        if k.get("cls") == "letter":
            holds["letters_penalised" if k.get("penalty") else "letters_unpenalised"].append(h["ms"])
        elif k.get("cls") in MODE_CLASSES:
            holds["mode_keys"].append(h["ms"])

    # --- picks
    picks = [e for e in events if e["event"] == "pick"]
    pick_undos = [e for e in events if e["event"] == "pickundo"]
    chain = [
        p for p in picks
        if isinstance(p.get("sincepick"), int) and 0 <= p["sincepick"] < CHAIN_PICK_MS
        and (p.get("sincekey", -1) < 0 or p["sincekey"] > p["sincepick"])
    ]

    # --- spaces
    spaces = [e for e in events if e["event"] == "space"]
    empty_spaces = [s for s in spaces if s.get("empty") is True]

    # --- near a window move
    near_move = [k for k in keys if isinstance(k.get("movems"), int) and 0 <= k["movems"] < NEAR_MOVE_MS]

    # --- mirror, overall and per foreground process
    process = None
    mirror_all: Counter[str] = Counter()
    mirror_by_proc: dict[str, Counter[str]] = defaultdict(Counter)
    for e in events:
        if e["event"] == "shown":
            process = e.get("proc")
        elif e["event"] == "mirror":
            res = str(e.get("res"))
            mirror_all[res] += 1
            mirror_by_proc[str(process)][res] += 1

    def divergence(c: Counter[str]) -> float | None:
        return _share(c["mismatch"], c["match"] + c["mismatch"])

    hides = Counter(str(e.get("cause")) for e in events if e["event"] == "hidden")
    mode_undos = [e for e in events if e["event"] == "modeundo"]

    return {
        "edge": edge or "all",
        "letters": len(letters),
        "activations": len(keys),
        "slips": len(slipped),
        "slip_rate": _share(len(slipped), len(letters)),
        "slipped_letters_counted": len(countable),
        "slipped_likely0": len(unlikely),
        "slipped_likely0_share": _share(len(unlikely), len(countable)),
        "mode_undos": len(mode_undos),
        "mode_undos_per_100_letters": None if not letters else 100 * len(mode_undos) / len(letters),
        "hold_on": {name: _hold_stats(values) for name, values in holds.items()},
        "picks": len(picks),
        "picks_by_source": dict(Counter(str(p.get("src")) for p in picks)),
        "pick_undos": len(pick_undos),
        "pick_then_undo_rate": _share(len(pick_undos), len(picks)),
        "chain_picks": len(chain),
        "chain_picks_per_50_picks": None if not picks else 50 * len(chain) / len(picks),
        "activations_within_1s_of_move": len(near_move),
        "activations_within_1s_of_move_share": _share(len(near_move), len(keys)),
        "spaces": len(spaces),
        "bar_empty_after_space_share": _share(len(empty_spaces), len(spaces)),
        "mirror": dict(mirror_all),
        "mirror_divergence": divergence(mirror_all),
        "mirror_by_process": {
            p: {"verdicts": dict(c), "divergence": divergence(c)} for p, c in sorted(mirror_by_proc.items())
        },
        "hides_by_cause": dict(hides),
        "enough_for_c2": len(letters) >= MIN_LETTERS and len(countable) >= MIN_SLIPS,
    }


def _pct(x: float | None) -> str:
    return "n/a" if x is None else f"{100 * x:.1f} %"


def render(m: dict[str, Any]) -> str:
    out: list[str] = []
    add = out.append
    add(f"E1 keyboard read-outs ({m['edge']} edge)")
    add("=" * 44)
    add(f"letter activations        {m['letters']}   (all activations: {m['activations']})")
    add(f"slips                     {m['slips']}   slip rate {_pct(m['slip_rate'])}")
    add(f"slipped letters counted   {m['slipped_likely0']} of {m['slipped_letters_counted']} "
        f"have likely=0 ({_pct(m['slipped_likely0_share'])}); start / nocont excluded")
    add(f"mode-undos                {m['mode_undos']}   = "
        + ("n/a" if m["mode_undos_per_100_letters"] is None
           else f"{m['mode_undos_per_100_letters']:.2f}") + " per 100 letters")
    add("hold-on (gaze stayed in the box after the fire)")
    for name, h in m["hold_on"].items():
        median = "n/a" if h["median_ms"] is None else f"{h['median_ms']:g} ms"
        add(f"  {name:<20} n={h['n']:<4} median {median:<9} >=150 ms: {_pct(h['share_ge_150ms'])}")
    add(f"picks                     {m['picks']}   by source {m['picks_by_source']}")
    add(f"pick-then-undo            {m['pick_undos']}   rate {_pct(m['pick_then_undo_rate'])}")
    add(f"chain-picks               {m['chain_picks']}   = "
        + ("n/a" if m["chain_picks_per_50_picks"] is None else f"{m['chain_picks_per_50_picks']:.2f}")
        + " per 50 picks")
    add(f"within 1 s of a move      {m['activations_within_1s_of_move']}   "
        f"share {_pct(m['activations_within_1s_of_move_share'])}")
    add(f"bar empty after Space     {_pct(m['bar_empty_after_space_share'])}   (of {m['spaces']} spaces)")
    add(f"mirror check              {m['mirror']}   divergence {_pct(m['mirror_divergence'])}")
    for proc, v in m["mirror_by_process"].items():
        add(f"  {proc:<22} {v['verdicts']}   divergence {_pct(v['divergence'])}")
    add(f"hides by cause            {m['hides_by_cause']}")
    add("")
    add("C2 minimums (>= 150 letters, >= 20 counted slips): "
        + ("MET" if m["enough_for_c2"] else "NOT MET -- keep logging"))
    return "\n".join(out)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Read-outs from a day log's KB-E1 keyboard lines.")
    ap.add_argument("log", type=Path, help="an EyeCtrl day log (eyectrl-YYYYMMDD.log)")
    ap.add_argument("--edge", choices=("top", "bottom"), help="only events while docked on this edge")
    ap.add_argument("--json", action="store_true", help="print the read-outs as JSON")
    args = ap.parse_args(argv)

    try:
        lines = args.log.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError as e:
        print(f"cannot read {args.log}: {e}", file=sys.stderr)
        return 2

    events = parse(lines)
    if not events:
        print(f"no KB-E1 lines in {args.log}", file=sys.stderr)
        return 1

    result = metrics(events, args.edge)
    print(json.dumps(result, indent=2, sort_keys=True) if args.json else render(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
