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

The events: key, holdon, abandon, corr, slip, pick, pickundo, space, mode, modeundo, edge, shown,
layout, hidden, mirror, sensitive.

Privacy (the owner's rule, "all recommendations"): after a text key (class letter, digit,
symbol, emoji) the line says only that a text key was typed -- plus timing (``ts``, ``sincekey``),
the dock edge, ``typing`` (was a word open) and the dwell. It carries NO edge-quarter class either
(older logs had one per line, ``edgeq=N|NE|E|SE|S|SW|W|NW|-``; this script still reads them): the outer
quarter of the box the gaze rested in is only COUNTED per dock edge, in one ``edgeq`` tally line
(``KB-E1 edgeq edge=top fires=F c=.. n=.. ne=.. e=.. se=.. s=.. sw=.. w=.. nw=.. aborts=A ac=.. an=.. ...``,
written right before a ``hidden`` line and, for the edge being left, right before a Flip's ``edge``
line; ``c`` = the inner half, the other keys the outer quarters; ``a*`` the same for abandonments).
It carries NO row/column/action id, NO box size (``boxw``/``boxh`` come once on ``shown`` and
``layout`` lines), NO pixel offset, and NO saccade, restart count or likelihood. Those arrival facts
exist only for a CORRECTED letter: the in-word Backspace is followed by a keyed ``corr`` line
(``seq`` = the Backspace's) and the ``slip`` line repeats them for the slipped key. Only non-text keys
(space, backspace, enter, modifiers, switches, shortcuts) keep ``r``/``c``/``act``, box size, pixel
offsets, ``saccade`` and ``restarts`` -- except that those last two are withheld (``saccade=-``,
``restarts=-1``) when the PREVIOUS activation was a text key, because the path from a letter to a Space
would carry the letter's position. While a password-like field has focus the keyboard writes NO
per-key line at all, only a single ``KB-E1 sensitive`` marker per entry (no count).

So this script cannot say WHICH letter was slipped: the ``slip`` event is the live, content-free
substitute -- whether the replacement was a geometric neighbour of the slipped key, in which compass
direction, how many key pitches away, how long before the Backspace, and in which third of the
rows that hold text keys (``reg``: top/mid/bot; on QWERTZ the q-row, the a-row, the y-row).

Read-outs (steward's C2 rule, evidence-steward "C2/C2b/C2c")
------------------------------------------------------------
* letters            letter activations
* slips              a letter whose NEXT activation is a Backspace inside the current word
                     (``bs=word``), no more than 3 s later by the log's own clock. A Backspace
                     into committed text is editing, not a slip, and does not count.
* slip rate          slips / letters
* repairs            the ``slip`` events: share whose replacement was ADJACENT to the slipped key,
                     the compass-direction histogram of the adjacent ones, and the same per row
                     band (``reg``). The steward's C2 rule 2 -- ">= 60 % of slips adjacent with
                     one consistent direction in a region" -- is flagged per region as
                     ``meets_rule2``. The steward gave no number for "consistent" or for how
                     many repairs a region needs, and simulating a bias-free day shows why that
                     matters: with n = 5 and a top-share bar of 50 % one region trips about one
                     time in five by chance (and always for row slips, which can only go E/W).
                     So the rule here needs ALL of: at least ``MIN_REGION_SLIPS`` (10) repairs
                     in the region, >= 3/5 of them adjacent, and the commonest direction holding
                     >= 2/3 of the adjacent ones (about 1 in 500 by chance at n = 10 with six
                     neighbours). A one-sided binomial test against the region's own
                     neighbour-direction base rate would be the sharper alternative; this
                     integer rule was chosen because it can be read without a table.
* rule 2b            edge-crowding, the other half of the steward's rule 2: ">= 30 % of accepted
                     dwells in the outer quarter of the box on one side". From the ``edgeq`` tally
                     lines (and, in older logs, the per-line ``edgeq`` class): per dock edge, the share of accepted dwells whose mean
                     offset sat in the outer quarter of each side (a diagonal counts for both
                     sides it touches); flagged ``meets_rule2b`` when one side holds >= 3/10 of at
                     least ``MIN_EDGE_DWELLS`` (50) dwells. With option B there is no per-letter
                     region, so it is computed per dock edge only.
* likely=0 share     of slipped letters (read from their ``corr`` lines), those whose letter was
                     NOT in the predictor's top-5 continuation set, excluding word starts
                     (``start``) and prefixes with no continuation (``nocont``). The rule wants
                     n >= 20 before it is read.
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
# Rule 2 (steward): >= 60 % of repairs adjacent, one consistent direction, in a region. Fractions are
# (numerator, denominator) so the boundary is exact integer arithmetic, not float rounding.
#
# CHANCE RATE, read before acting on a "meets_rule2" flag: a region whose slips can only go EAST or
# WEST (a row of keys with no row above or below it, e.g. a single-row sub-layout) trips this rule by
# chance about 34 % of the time at n = 10 and about 12 % at n = 20 (review 02's figures for a bias-free
# day, taken from its simulation, not re-derived here), because every adjacent slip there
# is E or W and the top direction then holds >= 2/3 of them almost always. The thresholds only
# separate bias from chance for regions with several neighbour directions. So ALWAYS check the
# direction histogram (`directions`) of a flagged region before acting: a flag whose histogram is just
# E and W says nothing about a bias.
ADJACENT_SHARE_RULE2 = (3, 5)
CONSISTENT_DIRECTION_SHARE = (2, 3)
MIN_REGION_SLIPS = 10
# Rule 2b (steward): >= 30 % of accepted dwells in the outer quarter of the box, on one side.
EDGE_CROWDING_SHARE = (3, 10)
MIN_EDGE_DWELLS = 50
TEXT_CLASSES = {"letter", "digit", "symbol", "emoji"}
# The ``edgeq`` tally line's counters: accepted text-key dwells per outer quarter (c = the inner half),
# then abandoned ones (prefix a). The quarter names are the old per-line ``edgeq`` classes, lower-cased.
TALLY_QUARTERS = ["-", "N", "NE", "E", "SE", "S", "SW", "W", "NW"]
TALLY_FIRES = ["c"] + [q.lower() for q in TALLY_QUARTERS[1:]]
TALLY_ABORTS = ["a" + name for name in TALLY_FIRES]
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
    "key": {"seq": int, "ts": int, "cls": str, "penalty": bool, "bs": str, "movems": int},
    "corr": {"seq": int, "saccade": str, "restarts": int, "likely": bool, "start": bool, "nocont": bool},
    "holdon": {"seq": int, "ms": int},
    "abandon": {"ts": int, "cls": str},
    "slip": {"adj": bool, "dir": str, "dist": int, "ms": int, "reg": str},
    "pick": {"slot": int, "src": str, "sincekey": int, "sincepick": int},
    "pickundo": {"ms": int},
    "space": {"seq": int, "empty": bool},
    "mode": {"kind": str},
    "modeundo": {"kind": str},
    "edge": {"button": str},
    "shown": {"proc": str, "edge": str},
    "layout": {"sub": str, "boxw": int, "boxh": int},
    "edgeq": {"edge": str, "fires": int, "aborts": int, **{name: int for name in TALLY_FIRES + TALLY_ABORTS}},
    "sensitive": {},
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
        dock = fields["edge"] if event == "edgeq" else edge
        events.append({"event": event, "at": at, "dock": dock, **fields})
    return events


def _share(part: int, whole: int) -> float | None:
    return None if whole == 0 else part / whole


def _hold_stats(values: list[int]) -> dict[str, Any]:
    return {
        "n": len(values),
        "median_ms": statistics.median(values) if values else None,
        "share_ge_150ms": _share(sum(1 for v in values if v >= HOLD_ON_GATE_MS), len(values)),
    }


def _at_least(part: int, whole: int, fraction: tuple[int, int]) -> bool:
    """part/whole >= numerator/denominator, exactly (integers only)."""
    return whole > 0 and part * fraction[1] >= whole * fraction[0]


def edge_crowding(events: list[dict[str, Any]], min_dwells: int) -> dict[str, Any]:
    """Rule 2b: per dock edge, the share of accepted text-key dwells in the outer quarter, by side.

    Reads the per-edge ``edgeq`` tally lines AND, so older logs still parse, the per-line ``edgeq``
    class of a text key; a log holding both is summed."""
    by_dock: dict[str, Counter] = defaultdict(Counter)
    for e in events:
        if e["event"] == "edgeq":
            for quarter, name in zip(TALLY_QUARTERS, TALLY_FIRES):
                by_dock[str(e["dock"])][quarter] += e[name]
        elif e["event"] == "key" and e.get("cls") in TEXT_CLASSES and isinstance(e.get("edgeq"), str):
            by_dock[str(e["dock"])][e["edgeq"]] += 1
    result: dict[str, Any] = {}
    for dock, counts in sorted(by_dock.items()):
        n = sum(counts.values())
        if n == 0:
            continue
        outer = n - counts["-"]
        sides = {side: sum(c for q, c in counts.items() if side in q) for side in "NESW"}
        result[dock] = {
            "n": n,
            "outer": outer,
            "outer_share": _share(outer, n),
            "sides": {side: {"n": c, "share": _share(c, n)} for side, c in sides.items()},
            "meets_rule2b": n >= min_dwells and any(_at_least(c, n, EDGE_CROWDING_SHARE) for c in sides.values()),
        }
    return result


def repairs(events: list[dict[str, Any]], min_region_slips: int) -> dict[str, Any]:
    """The ``slip`` events (a letter/digit slipped, taken back, replaced): adjacency, direction, region."""
    slips = [e for e in events if e["event"] == "slip"]

    def summary(group: list[dict[str, Any]]) -> dict[str, Any]:
        adjacent = [e for e in group if e["adj"]]
        directions = Counter(e["dir"] for e in adjacent)
        top_dir, top_n = directions.most_common(1)[0] if directions else (None, 0)
        adjacent_share = _share(len(adjacent), len(group))
        top_dir_share = _share(top_n, len(adjacent))
        return {
            "n": len(group),
            "adjacent": len(adjacent),
            "adjacent_share": adjacent_share,
            "directions": dict(directions),
            "top_direction": top_dir,
            "top_direction_share": top_dir_share,
            "median_ms_to_backspace": statistics.median([e["ms"] for e in group]) if group else None,
            "meets_rule2": bool(
                len(group) >= min_region_slips
                and _at_least(len(adjacent), len(group), ADJACENT_SHARE_RULE2)
                and _at_least(top_n, len(adjacent), CONSISTENT_DIRECTION_SHARE)),
        }

    result = summary(slips)
    by_region: dict[str, Any] = {}
    for reg in sorted({e["reg"] for e in slips}):
        by_region[reg] = summary([e for e in slips if e["reg"] == reg])
    result["by_region"] = by_region
    result["rule2_regions"] = sorted(r for r, v in by_region.items() if v["meets_rule2"])
    return result


def metrics(events: list[dict[str, Any]], edge: str | None = None,
            min_region_slips: int = MIN_REGION_SLIPS, min_edge_dwells: int = MIN_EDGE_DWELLS) -> dict[str, Any]:
    if edge is not None:
        events = [e for e in events if e["dock"] == edge]

    keys = [e for e in events if e["event"] == "key"]
    by_seq = {e["seq"]: e for e in keys if "seq" in e}
    letters = [k for k in keys if k.get("cls") == "letter"]

    # --- slips: a letter immediately followed (next activation) by an in-word Backspace <= 3 s.
    # The slipped letter's own arrival and likelihood facts are NOT on its key line any more: they ride on
    # the keyed ``corr`` line of the Backspace that takes it back.
    corr_by_seq = {e["seq"]: e for e in events if e["event"] == "corr"}
    slipped: list[dict[str, Any]] = []
    slipped_corr: list[dict[str, Any]] = []
    for prev, nxt in zip(keys, keys[1:]):
        if (prev.get("cls") == "letter" and nxt.get("cls") == "backspace"
                and nxt.get("bs") == "word" and 0 <= nxt["at"] - prev["at"] <= SLIP_WINDOW_MS):
            slipped.append(prev)
            if nxt["seq"] in corr_by_seq:
                slipped_corr.append(corr_by_seq[nxt["seq"]])
    countable = [c for c in slipped_corr if not c.get("start") and not c.get("nocont")]
    unlikely = [c for c in countable if not c.get("likely")]

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
        "slipped_saccades": dict(Counter(str(c.get("saccade")) for c in slipped_corr)),
        "repairs": repairs(events, min_region_slips),
        "edge_crowding": edge_crowding(events, min_edge_dwells),
        "sensitive_entries": sum(1 for e in events if e["event"] == "sensitive"),
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
    r = m["repairs"]
    add(f"repairs (slip lines)       {r['n']}   adjacent {_pct(r['adjacent_share'])}"
        f"   directions {r['directions']}")
    for reg, v in r["by_region"].items():
        add(f"  {reg:<5} n={v['n']:<4} adjacent {_pct(v['adjacent_share']):<8} "
            f"top dir {v['top_direction']} {_pct(v['top_direction_share'])}   "
            + ("MEETS rule 2" if v["meets_rule2"] else "-"))
    add("edge-crowding (rule 2b, text-key dwells in the outer quarter, per dock edge)")
    for dock, v in m["edge_crowding"].items():
        sides = "  ".join(f"{side} {_pct(info['share'])}" for side, info in v["sides"].items())
        add(f"  {dock:<7} n={v['n']:<5} outer {_pct(v['outer_share']):<8} {sides}   "
            + ("MEETS rule 2b" if v["meets_rule2b"] else "-"))
    add(f"password-field entries    {m['sensitive_entries']}   (no per-key lines are logged inside them)")
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
