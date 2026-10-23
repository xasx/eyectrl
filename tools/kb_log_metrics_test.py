#!/usr/bin/env python3
"""Self-test for tools/kb_log_metrics.py.

    python tools/kb_log_metrics_test.py

Stdlib only. The parser turns the keyboard's E1 lines into the numbers the C2 decision rule is
read from, so a wrong slip rule or a mis-keyed hold-on would steer the dwell decision the wrong
way with nothing red. This suite runs it over a committed synthetic day log whose every answer
was worked out by hand from the lines (tests/fixtures/kb_log/synthetic-day.log -- the
composition table below), plus damaged-input cases.

The fixture, in event order (times are the log clock, ms after 10:00:00):

  TOP session (shown at 0.1 s). Letters L1..L9, Backspaces (inside the word unless noted),
  Spaces, a sub-layout switch undone after 2.5 s, a Shift, four picks, three mirror checks:

    L1  1.0 s likely  start    near a move (900 ms)       hold-on 600
    L2  2.0 s likely                                       hold-on 600
    L3  3.0 s UNLIKELY                                     hold-on 300
    BS  3.8 s in word (800 ms after L3)            -> L3 is a counted slip
    L4  5.0 s likely                                       hold-on 200
    L5  6.0 s unlikely, nocont                             hold-on 150
    BS  6.5 s in word                              -> L5 is a slip, excluded (nocont)
    L6  8.0 s unlikely, start                              hold-on 100
    BS  8.4 s in word                              -> L6 is a slip, excluded (start)
    L7 10.0 s unlikely                                     hold-on 600
    BS 13.5 s in word, 3.5 s later                 -> NOT a slip (> 3 s)
    L8 15.0 s likely, PENALISED                            hold-on 100
    BS 15.5 s into COMMITTED text                  -> NOT a slip
    SP 17.0 s  bar empty                           L9 18.0 s likely  hold-on 450
    SP 19.0 s  bar not empty
    switchSub:numbers 20.0 s, switchSub:letters 22.5 s (modeundo 2500), Shift 23.0 s
    picks: P1 dict (undone, pickundo), P2 pair (600 ms after P1, no key between: CHAIN),
           P3 lm (5 s after), P4 dict (400 ms after P3 but a key 300 ms ago: not a chain)
    mirror: match, mismatch (enter), unsupported (hide) under WINWORD.exe; hide by toolbar-toggle

  BOTTOM session (flip to bottom at 50.1 s; a second 'shown' for chrome.exe), then a sentence
  pick P5 at 50.3 s -- before any key line names the edge:
    L10 near a move (500 ms) likely hold-on 300, L11 unlikely hold-on 120 then BS in word
    (a counted slip), L12 likely hold-on 600; mirror suppressed; hide by edge-close.

  Text-key lines (letters) carry NO r/c/act/box size/pixel offset/saccade/restarts/likelihood and no
  edge-quarter class: one ``edgeq`` TALLY line per dock edge is written right before each ``hidden``
  line (top: 9 dwells, - E E - NE - E - S -> c=4 ne=1 e=3 s=1, plus 1 abandonment in the inner half;
  bottom: - W - -> c=2 w=1). ``synthetic-day-legacy.log`` is the same day in the older per-line form
  (``edgeq=`` on every text line, no tally); both must read the same. The slipped letters' arrival and
  likelihood facts ride on ``corr`` lines keyed to the in-word Backspace (L3: likely 0; L5: nocont; L6:
  start) and the bottom's L11 (likely 0). Non-text keys (Backspace, Space, switchSub, Shift) keep r/c/act.
  Four 'slip' repair lines follow the replacement keys:
    L3 -> L4  adj E  dist 1  800 ms  top        L5 -> L6  adj E  dist 1  500 ms  top
    L6 -> L7  not adjacent, dist 2   400 ms  mid        L11 -> L12 adj W dist 1  500 ms  bot (bottom edge)
  and two abandon lines (one identity-free letter, one Shift that keeps its position), a ``shown``
  line with the letter box, ``layout`` lines, and ONE ``KB-E1 sensitive`` marker (a password field).

  Noise that must be skipped: a non-E1 line, a damaged 'key seq=zzz' line, a line cut off by
  a crash.
"""

from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TOOL = Path(__file__).resolve().parent / "kb_log_metrics.py"
FIXTURE = REPO_ROOT / "tests" / "fixtures" / "kb_log" / "synthetic-day.log"

_spec = importlib.util.spec_from_file_location("kb_log_metrics", TOOL)
kb = importlib.util.module_from_spec(_spec)
sys.modules["kb_log_metrics"] = kb
_spec.loader.exec_module(kb)


def run_cli(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, "-I", str(TOOL), *args], capture_output=True, text=True)


def fixture_metrics(edge: str | None = None) -> dict:
    events = kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines())
    return kb.metrics(events, edge)


class WholeDay(unittest.TestCase):
    def setUp(self) -> None:
        self.m = fixture_metrics()

    def test_counts_letters_and_activations_and_skips_damaged_lines(self) -> None:
        self.assertEqual(self.m["letters"], 12)
        self.assertEqual(self.m["activations"], 23)   # the zzz line and the cut-off line are not counted

    def test_slips_are_letters_followed_by_an_in_word_backspace_within_3s(self) -> None:
        # L3, L5, L6 (top) and L11 (bottom). L7's Backspace is 3.5 s late; L8's goes into committed text.
        self.assertEqual(self.m["slips"], 4)
        self.assertAlmostEqual(self.m["slip_rate"], 4 / 12)

    def test_likely_zero_share_excludes_word_starts_and_no_continuation(self) -> None:
        # Of the four slipped letters only L3 and L11 count (L5 is nocont, L6 is a word start).
        self.assertEqual(self.m["slipped_letters_counted"], 2)
        self.assertEqual(self.m["slipped_likely0"], 2)
        self.assertEqual(self.m["slipped_likely0_share"], 1.0)

    def test_the_c2_minimums_are_not_met_by_a_tiny_log(self) -> None:
        self.assertFalse(self.m["enough_for_c2"])

    def test_mode_undos_per_100_letters(self) -> None:
        self.assertEqual(self.m["mode_undos"], 1)
        self.assertAlmostEqual(self.m["mode_undos_per_100_letters"], 100 / 12)

    def test_hold_on_by_dwell_class(self) -> None:
        hold = self.m["hold_on"]
        # 11 unpenalised letters: 600 600 300 200 150 100 600 450 300 120 600 -> two are under 150.
        self.assertEqual(hold["letters_unpenalised"]["n"], 11)
        self.assertEqual(hold["letters_unpenalised"]["median_ms"], 300)
        self.assertAlmostEqual(hold["letters_unpenalised"]["share_ge_150ms"], 9 / 11)
        # The one penalised letter held 100 ms.
        self.assertEqual(hold["letters_penalised"]["n"], 1)
        self.assertEqual(hold["letters_penalised"]["share_ge_150ms"], 0.0)
        # Mode keys: two sub-layout switches (400, 100) and a Shift (300).
        self.assertEqual(hold["mode_keys"]["n"], 3)
        self.assertEqual(hold["mode_keys"]["median_ms"], 300)
        self.assertAlmostEqual(hold["mode_keys"]["share_ge_150ms"], 2 / 3)

    def test_picks_undo_rate_chain_picks_and_sources(self) -> None:
        self.assertEqual(self.m["picks"], 5)
        self.assertEqual(self.m["picks_by_source"], {"dict": 2, "pair": 1, "lm": 1, "sentence": 1})
        self.assertEqual(self.m["pick_undos"], 1)
        self.assertEqual(self.m["pick_then_undo_rate"], 0.2)
        # P2 only: P4 is 400 ms after P3 but a key activation came in between.
        self.assertEqual(self.m["chain_picks"], 1)
        self.assertEqual(self.m["chain_picks_per_50_picks"], 10.0)

    def test_activations_within_a_second_of_a_window_move(self) -> None:
        self.assertEqual(self.m["activations_within_1s_of_move"], 2)   # L1 (900 ms) and L10 (500 ms)
        self.assertAlmostEqual(self.m["activations_within_1s_of_move_share"], 2 / 23)

    def test_bar_empty_after_space(self) -> None:
        self.assertEqual(self.m["spaces"], 2)
        self.assertEqual(self.m["bar_empty_after_space_share"], 0.5)

    def test_mirror_verdicts_overall_and_per_process(self) -> None:
        self.assertEqual(self.m["mirror"], {"match": 1, "mismatch": 1, "unsupported": 1, "suppressed": 1})
        self.assertEqual(self.m["mirror_divergence"], 0.5)   # suppressed and unsupported are excluded
        by_proc = self.m["mirror_by_process"]
        self.assertEqual(by_proc["WINWORD.exe"]["verdicts"], {"match": 1, "mismatch": 1, "unsupported": 1})
        self.assertEqual(by_proc["WINWORD.exe"]["divergence"], 0.5)
        self.assertEqual(by_proc["chrome.exe"]["verdicts"], {"suppressed": 1})
        self.assertIsNone(by_proc["chrome.exe"]["divergence"])

    def test_the_slipped_letters_arrival_comes_from_the_corr_lines(self) -> None:
        # L3, L5, L6 (top) and L11 (bottom): their saccade classes ride on the keyed corr lines.
        self.assertEqual(self.m["slipped_saccades"], {"mid": 2, "short": 1, "long": 1})

    def test_the_password_marker_is_counted_and_carries_nothing(self) -> None:
        self.assertEqual(self.m["sensitive_entries"], 1)
        events = kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines())
        marker = [e for e in events if e["event"] == "sensitive"]
        self.assertEqual(set(marker[0]) - {"event", "at", "dock"}, set())      # no fields, no count

    def test_hides_by_cause(self) -> None:
        self.assertEqual(self.m["hides_by_cause"], {"toolbar-toggle": 1, "edge-close": 1})


class Repairs(unittest.TestCase):
    def setUp(self) -> None:
        self.m = fixture_metrics()
        self.r = self.m["repairs"]

    def test_adjacent_share_and_direction_histogram(self) -> None:
        self.assertEqual(self.r["n"], 4)
        self.assertEqual(self.r["adjacent"], 3)
        self.assertAlmostEqual(self.r["adjacent_share"], 0.75)
        self.assertEqual(self.r["directions"], {"E": 2, "W": 1})
        self.assertEqual(self.r["top_direction"], "E")
        self.assertAlmostEqual(self.r["top_direction_share"], 2 / 3)
        self.assertEqual(self.r["median_ms_to_backspace"], 500)     # 800 500 400 500

    def test_by_region_uses_the_row_band_on_the_line(self) -> None:
        by = self.r["by_region"]
        self.assertEqual(sorted(by), ["bot", "mid", "top"])
        self.assertEqual((by["top"]["n"], by["top"]["adjacent"]), (2, 2))
        self.assertEqual(by["top"]["top_direction"], "E")
        self.assertEqual((by["mid"]["n"], by["mid"]["adjacent"]), (1, 0))
        self.assertIsNone(by["mid"]["top_direction"])
        self.assertEqual((by["bot"]["n"], by["bot"]["adjacent"]), (1, 1))
        self.assertEqual(by["bot"]["top_direction"], "W")

    def test_rule2_needs_the_minimum_repairs_in_a_region(self) -> None:
        # With the real minimum (10) a tiny log qualifies nowhere.
        self.assertEqual(self.r["rule2_regions"], [])
        events = kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines())
        # Lowered to 2: the top band (2 of 2 adjacent, both E) qualifies; mid (0 adjacent) and
        # bot (1 repair) do not.
        low = kb.metrics(events, min_region_slips=2)["repairs"]
        self.assertEqual(low["rule2_regions"], ["top"])
        self.assertTrue(low["by_region"]["top"]["meets_rule2"])
        self.assertFalse(low["by_region"]["mid"]["meets_rule2"])
        self.assertFalse(low["by_region"]["bot"]["meets_rule2"])

    @staticmethod
    def _slips(adjacent_dirs: list[str], not_adjacent: int = 0, reg: str = "top") -> list[str]:
        head = "2026-10-19 10:00:00.000 [INF] [X] KB-E1 slip"
        tail = f"dist=1 ms=500 reg={reg} saccade=mid restarts=0 likely=false start=false nocont=false"
        return ([f"{head} adj=true dir={d} {tail}" for d in adjacent_dirs]
                + [f"{head} adj=false dir=- {tail}"] * not_adjacent)

    def _rule2(self, lines: list[str]) -> list[str]:
        return kb.metrics(kb.parse(lines))["repairs"]["rule2_regions"]       # the REAL thresholds

    def test_rule2_boundary_exactly_at_the_thresholds_meets(self) -> None:
        # n = 10 (the minimum), 6 adjacent (exactly 3/5), 4 of those 6 east (exactly 2/3).
        self.assertEqual(self._rule2(self._slips(["E"] * 4 + ["W"] * 2, not_adjacent=4)), ["top"])

    def test_rule2_one_below_the_minimum_repairs_does_not_meet(self) -> None:
        # The same perfect pattern at n = 9.
        self.assertEqual(self._rule2(self._slips(["E"] * 6, not_adjacent=3)), [])
        self.assertEqual(self._rule2(self._slips(["E"] * 6, not_adjacent=4)), ["top"])     # n = 10

    def test_rule2_one_below_the_adjacent_share_does_not_meet(self) -> None:
        # 5 of 10 adjacent (one below 3/5), all east.
        self.assertEqual(self._rule2(self._slips(["E"] * 5, not_adjacent=5)), [])
        self.assertEqual(self._rule2(self._slips(["E"] * 6, not_adjacent=4)), ["top"])     # 6 of 10

    def test_rule2_one_below_the_direction_share_does_not_meet(self) -> None:
        # 7 adjacent: 4 east is 4/7 (< 2/3), 5 east is 5/7.
        self.assertEqual(self._rule2(self._slips(["E"] * 4 + ["W"] * 3, not_adjacent=3)), [])
        self.assertEqual(self._rule2(self._slips(["E"] * 5 + ["W"] * 2, not_adjacent=3)), ["top"])

    def test_rule2_is_not_tripped_by_a_row_that_can_only_go_east_or_west(self) -> None:
        # The old "top share >= 50 %" bar was vacuous with two directions: a bias-free day of row slips
        # (E/W only, evenly split) met it. At 2/3 it does not.
        self.assertEqual(self._rule2(self._slips(["E"] * 5 + ["W"] * 5)), [])

    def test_rule2_is_decided_per_region(self) -> None:
        east_top = self._slips(["E"] * 6, not_adjacent=4, reg="top")
        scattered_mid = self._slips(["E", "W", "N", "S", "NE", "SW", "NW", "SE", "E", "W"], reg="mid")
        self.assertEqual(self._rule2(east_top + scattered_mid), ["top"])

    def test_rule2_needs_a_consistent_direction_not_just_adjacency(self) -> None:
        scattered = kb.metrics(kb.parse(self._slips(["E", "W", "N", "S", "NE", "SW"])),
                               min_region_slips=2)["repairs"]
        self.assertEqual(scattered["adjacent_share"], 1.0)
        self.assertEqual(scattered["rule2_regions"], [])           # all adjacent, no direction dominates
        agreeing = kb.metrics(kb.parse(self._slips(["E"] * 4 + ["W"] * 2)), min_region_slips=2)["repairs"]
        self.assertEqual(agreeing["rule2_regions"], ["top"])       # 4 of 6 east: exactly 2/3

    def test_rule2_needs_the_adjacent_share(self) -> None:
        m = kb.metrics(kb.parse(self._slips(["E"] * 2, not_adjacent=3)), min_region_slips=2)["repairs"]
        self.assertAlmostEqual(m["adjacent_share"], 0.4)
        self.assertEqual(m["rule2_regions"], [])

    def test_a_damaged_slip_line_is_skipped(self) -> None:
        good = "2026-10-19 10:00:00.000 [INF] [X] KB-E1 slip adj=true dir=E dist=1 ms=500 reg=top"
        self.assertEqual(len(kb.parse([good])), 1)
        self.assertEqual(kb.parse([good.replace("adj=true", "adj=1")]), [])
        self.assertEqual(kb.parse([good.replace(" reg=top", "")]), [])

    def test_the_slip_split_by_edge_follows_the_flip(self) -> None:
        self.assertEqual(fixture_metrics("top")["repairs"]["n"], 3)
        self.assertEqual(fixture_metrics("bottom")["repairs"]["n"], 1)


LEGACY_FIXTURE = FIXTURE.with_name("synthetic-day-legacy.log")


class EdgeCrowding(unittest.TestCase):
    """Rule 2b: >= 30 % of accepted text-key dwells in the outer quarter of the box on one side.

    These tests feed the OLDER per-line ``edgeq`` form (kept readable); ``EdgeCrowdingTally`` below does
    the same for the tally line."""

    @staticmethod
    def _keys(quarters: list[str], dock: str = "top") -> list[str]:
        lines = [f"2026-10-19 10:00:00.000 [INF] [X] KB-E1 shown proc=a.exe edge={dock} via=ring boxw=210 boxh=210"]
        for i, q in enumerate(quarters):
            lines.append(f"2026-10-19 10:00:{i % 60:02d}.000 [INF] [X] KB-E1 key seq={i + 1} ts={i} cls=letter "
                         f"sub=letters edge={dock} winx=0 winy=0 winw=1 winh=1 movems=-1 edgeq={q} sincekey=1 "
                         f"dwell=450 penalty=false typing=true bs=-")
        return lines

    def _crowding(self, quarters: list[str], min_dwells: int = 10, dock: str = "top") -> dict:
        return kb.metrics(kb.parse(self._keys(quarters, dock)), min_edge_dwells=min_dwells)["edge_crowding"]

    def test_the_fixture_reads_per_dock_edge(self) -> None:
        low = kb.metrics(kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines()),
                         min_edge_dwells=2)["edge_crowding"]
        # top: - E E - NE - E - S -> east holds E, E, NE, E = 4 of 9; north 1, south 1, west 0.
        self.assertEqual(low["top"]["n"], 9)
        self.assertEqual(low["top"]["outer"], 5)
        self.assertEqual(low["top"]["sides"]["E"]["n"], 4)
        self.assertEqual(low["top"]["sides"]["N"]["n"], 1)
        self.assertTrue(low["top"]["meets_rule2b"])
        # bottom: one of three in the west quarter = 1/3 >= 3/10.
        self.assertEqual(low["bottom"]["sides"]["W"]["n"], 1)
        self.assertTrue(low["bottom"]["meets_rule2b"])

    def test_a_tiny_log_does_not_meet_rule_2b_at_the_real_minimum(self) -> None:
        real = kb.metrics(kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines()))["edge_crowding"]
        self.assertFalse(real["top"]["meets_rule2b"])
        self.assertFalse(real["bottom"]["meets_rule2b"])

    def test_only_text_keys_are_counted(self) -> None:
        # A non-text key never carries edgeq in a real log; were one to, it must not count as a text dwell.
        lines = self._keys(["-"] * 10)
        for i in range(10):
            lines.append(f"2026-10-19 10:01:{i:02d}.000 [INF] [X] KB-E1 key seq={100 + i} ts={100 + i} r=3 c=2 "
                         f"cls=space act=space boxw=210 boxh=210 sub=letters edge=top winx=0 winy=0 winw=1 winh=1 "
                         f"movems=-1 dx=0 dy=0 mdx=0 mdy=0 restarts=0 saccade=mid sincekey=1 dwell=450 "
                         f"penalty=false typing=true bs=- edgeq=E")
        top = kb.metrics(kb.parse(lines), min_edge_dwells=10)["edge_crowding"]["top"]
        self.assertEqual(top["n"], 10)
        self.assertFalse(top["meets_rule2b"])

    def test_exactly_30_percent_on_one_side_meets_and_one_below_does_not(self) -> None:
        self.assertTrue(self._crowding(["E"] * 3 + ["-"] * 7)["top"]["meets_rule2b"])       # 3 of 10
        self.assertFalse(self._crowding(["E"] * 2 + ["-"] * 8)["top"]["meets_rule2b"])      # 2 of 10

    def test_the_minimum_number_of_dwells_is_enforced_exactly(self) -> None:
        self.assertTrue(self._crowding(["E"] * 5 + ["-"] * 5, min_dwells=10)["top"]["meets_rule2b"])
        self.assertFalse(self._crowding(["E"] * 5 + ["-"] * 4, min_dwells=10)["top"]["meets_rule2b"])

    def test_the_default_minimum_is_50_dwells_exactly(self) -> None:
        def at(n: int) -> bool:
            quarters = ["E"] * (n // 2) + ["-"] * (n - n // 2)
            return kb.metrics(kb.parse(self._keys(quarters)))["edge_crowding"]["top"]["meets_rule2b"]
        self.assertFalse(at(49))
        self.assertTrue(at(50))
        self.assertEqual(kb.MIN_EDGE_DWELLS, 50)
        self.assertEqual(kb.MIN_REGION_SLIPS, 10)
        self.assertEqual(kb.ADJACENT_SHARE_RULE2, (3, 5))
        self.assertEqual(kb.CONSISTENT_DIRECTION_SHARE, (2, 3))
        self.assertEqual(kb.EDGE_CROWDING_SHARE, (3, 10))

    def test_it_is_one_side_not_the_sum_of_sides(self) -> None:
        # 2 east + 2 west + 2 north + 2 south of 10 is 80 % outer but only 20 % on any one side.
        self.assertFalse(self._crowding(["E"] * 2 + ["W"] * 2 + ["N"] * 2 + ["S"] * 2 + ["-"] * 2)["top"]["meets_rule2b"])

    def test_a_diagonal_counts_for_both_sides_it_touches(self) -> None:
        crowding = self._crowding(["NE"] * 3 + ["-"] * 7)["top"]
        self.assertEqual(crowding["sides"]["N"]["n"], 3)
        self.assertEqual(crowding["sides"]["E"]["n"], 3)
        self.assertTrue(crowding["meets_rule2b"])

    def test_non_text_keys_and_the_inner_half_do_not_count(self) -> None:
        lines = self._keys(["-"] * 10)
        lines.append("2026-10-19 10:00:30.000 [INF] [X] KB-E1 key seq=50 ts=1 r=3 c=2 cls=space act=space boxw=210 "
                     "boxh=210 sub=letters edge=top winx=0 winy=0 winw=1 winh=1 movems=-1 dx=90 dy=0 mdx=90 mdy=0 "
                     "restarts=0 saccade=mid sincekey=1 dwell=450 penalty=false typing=true bs=-")
        crowding = kb.metrics(kb.parse(lines), min_edge_dwells=10)["edge_crowding"]["top"]
        self.assertEqual(crowding["n"], 10)
        self.assertEqual(crowding["outer"], 0)

    def test_it_is_split_by_dock_edge(self) -> None:
        both = self._keys(["E"] * 4 + ["-"] * 6, "top") + self._keys(["-"] * 10, "bottom")
        crowding = kb.metrics(kb.parse(both), min_edge_dwells=10)["edge_crowding"]
        self.assertTrue(crowding["top"]["meets_rule2b"])
        self.assertFalse(crowding["bottom"]["meets_rule2b"])


class Privacy(unittest.TestCase):
    """The fixture is what a real day log looks like: a text key's line names no position or id."""

    TEXT_BANNED = (" r=", " c=", " act=", " boxw=", " boxh=", " dx=", " dy=", " mdx=", " mdy=", " saccade=",
                   " restarts=", " likely=", " start=", " nocont=")

    def test_fixture_letter_lines_carry_only_the_text_key_fields(self) -> None:
        seen = 0
        for line in FIXTURE.read_text(encoding="utf-8").splitlines():
            if "KB-E1 key " in line and " cls=letter " in line:
                seen += 1
                for banned in self.TEXT_BANNED:
                    self.assertNotIn(banned, line)
                self.assertNotIn("edgeq", line)       # the per-letter class is gone (a tally counts it)
        self.assertEqual(seen, 12)

    def test_non_text_lines_keep_their_position_and_id(self) -> None:
        backspaces = [l for l in FIXTURE.read_text(encoding="utf-8").splitlines() if " cls=backspace " in l]
        self.assertTrue(backspaces)
        for line in backspaces:
            self.assertIn(" act=backspace ", line)
            self.assertRegex(line, r" r=\d+ c=\d+ ")
            self.assertNotIn(" likely=", line)

    def test_parsing_never_needs_a_key_identity(self) -> None:
        events = kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines())
        letters = [e for e in events if e["event"] == "key" and e["cls"] == "letter"]
        self.assertEqual(len(letters), 12)
        for e in letters:
            for banned in ("r", "c", "act", "boxw", "boxh", "dx", "dy", "mdx", "mdy", "saccade", "restarts",
                           "likely", "start", "nocont"):
                self.assertNotIn(banned, e)
        abandons = [e for e in events if e["event"] == "abandon"]
        self.assertEqual(len(abandons), 2)
        self.assertNotIn("r", abandons[0])
        self.assertNotIn("edgeq", abandons[0])
        self.assertEqual(abandons[1]["r"], 3)

    def test_the_letter_box_comes_on_shown_and_layout_lines_only(self) -> None:
        events = kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines())
        self.assertEqual([e["boxw"] for e in events if e["event"] == "shown"], [210, 210])
        self.assertEqual(len([e for e in events if e["event"] == "layout"]), 2)


class ByEdge(unittest.TestCase):
    def test_top(self) -> None:
        m = fixture_metrics("top")
        self.assertEqual(m["letters"], 9)
        self.assertEqual(m["activations"], 19)
        self.assertEqual(m["slips"], 3)                       # L3, L5, L6
        self.assertEqual(m["slipped_letters_counted"], 1)     # L3 only
        self.assertEqual(m["activations_within_1s_of_move"], 1)
        self.assertEqual(m["picks"], 4)
        self.assertEqual(m["hides_by_cause"], {"toolbar-toggle": 1})
        self.assertEqual(m["mirror"], {"match": 1, "mismatch": 1, "unsupported": 1})

    def test_bottom_starts_at_the_flip(self) -> None:
        m = fixture_metrics("bottom")
        self.assertEqual(m["letters"], 3)
        self.assertEqual(m["activations"], 4)
        self.assertEqual(m["slips"], 1)                       # L11
        self.assertEqual(m["activations_within_1s_of_move"], 1)
        self.assertEqual(m["activations_within_1s_of_move_share"], 0.25)
        # The one pick right after the flip, BEFORE any key line says "bottom": only the
        # tracked Flip puts it on the bottom edge.
        self.assertEqual(m["picks"], 1)
        self.assertEqual(m["picks_by_source"], {"sentence": 1})
        self.assertEqual(m["hides_by_cause"], {"edge-close": 1})
        self.assertEqual(m["mirror"], {"suppressed": 1})

    def test_top_and_bottom_partition_the_day(self) -> None:
        whole, top, bottom = fixture_metrics(), fixture_metrics("top"), fixture_metrics("bottom")
        self.assertEqual(whole["letters"], top["letters"] + bottom["letters"])
        self.assertEqual(whole["activations"], top["activations"] + bottom["activations"])
        self.assertEqual(whole["slips"], top["slips"] + bottom["slips"])


class Parsing(unittest.TestCase):
    def test_non_e1_lines_and_unknown_events_are_ignored(self) -> None:
        events = kb.parse([
            "2026-10-19 10:00:00.000 [INF] [X] Keyboard shown",
            "2026-10-19 10:00:00.001 [INF] [X] KB-E1 frobnicate a=1",
            "garbage",
            "",
        ])
        self.assertEqual(events, [])

    def test_a_key_line_missing_a_required_field_is_skipped(self) -> None:
        line = ("2026-10-19 10:00:00.000 [INF] [X] KB-E1 key seq=1 ts=5 cls=letter penalty=False "
                "bs=- movems=-1")
        self.assertEqual(len(kb.parse([line])), 1)
        self.assertEqual(kb.parse([line.replace("movems=-1", "")]), [])
        self.assertEqual(kb.parse([line.replace("penalty=False", "penalty=1")]), [])   # wrong type

    def test_quoted_strings_are_accepted(self) -> None:
        events = kb.parse(['2026-10-19 10:00:00.000 [INF] [X] KB-E1 shown proc="WINWORD.exe" edge="top" via="ring"'])
        self.assertEqual(events[0]["proc"], "WINWORD.exe")

    def test_a_slip_needs_the_backspace_to_be_the_very_next_activation(self) -> None:
        def key(ms: int, cls: str, seq: int, bs: str = "-") -> str:
            return (f"2026-10-19 10:00:{ms // 1000:02d}.{ms % 1000:03d} [INF] [X] KB-E1 key seq={seq} ts={ms} "
                    f"cls={cls} penalty=False bs={bs} movems=-1")
        lines = [key(1000, "letter", 1), key(1500, "letter", 2), key(2000, "backspace", 3, "word")]
        m = kb.metrics(kb.parse(lines))
        self.assertEqual(m["slips"], 1)       # only the second letter, never the first

    def test_an_empty_log_has_no_ratios_rather_than_a_crash(self) -> None:
        m = kb.metrics([])
        self.assertEqual(m["letters"], 0)
        self.assertIsNone(m["slip_rate"])
        self.assertIsNone(m["mode_undos_per_100_letters"])
        self.assertIsNone(m["pick_then_undo_rate"])
        self.assertIsNone(m["chain_picks_per_50_picks"])


class CommandLine(unittest.TestCase):
    def test_text_report_names_the_c2_minimums(self) -> None:
        proc = run_cli(str(FIXTURE))
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertIn("letter activations        12", proc.stdout)
        self.assertIn("NOT MET -- keep logging", proc.stdout)

    def test_json_report_round_trips(self) -> None:
        proc = run_cli(str(FIXTURE), "--json", "--edge", "bottom")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(json.loads(proc.stdout)["letters"], 3)

    def test_a_log_without_e1_lines_exits_1(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "plain.log"
            path.write_text("2026-10-19 10:00:00.000 [INF] [X] Keyboard shown\n", encoding="utf-8")
            proc = run_cli(str(path))
        self.assertEqual(proc.returncode, 1)
        self.assertIn("no KB-E1 lines", proc.stderr)

    def test_a_missing_file_exits_2(self) -> None:
        proc = run_cli(str(REPO_ROOT / "does-not-exist.log"))
        self.assertEqual(proc.returncode, 2)


class EdgeCrowdingTally(unittest.TestCase):
    """Rule 2b from the per-dock-edge ``edgeq`` tally line, and its equivalence with the older form."""

    NAMES = ["c", "n", "ne", "e", "se", "s", "sw", "w", "nw"]

    @classmethod
    def _tally(cls, dock: str, fires: dict[str, int], aborts: dict[str, int] | None = None, second: int = 0) -> str:
        f = [fires.get(n, 0) for n in cls.NAMES]
        a = [(aborts or {}).get(n, 0) for n in cls.NAMES]
        body = " ".join(f"{n}={v}" for n, v in zip(cls.NAMES, f))
        abody = " ".join(f"a{n}={v}" for n, v in zip(cls.NAMES, a))
        return (f"2026-10-19 10:00:{second:02d}.000 [INF] [X] KB-E1 edgeq edge={dock} fires={sum(f)} {body} "
                f"aborts={sum(a)} {abody}")

    @staticmethod
    def _crowding(lines: list[str], min_dwells: int = 10) -> dict:
        return kb.metrics(kb.parse(lines), min_edge_dwells=min_dwells)["edge_crowding"]

    def test_a_tally_line_is_parsed_and_counted_per_dock_edge(self) -> None:
        got = self._crowding([self._tally("top", {"c": 7, "e": 3})])
        self.assertEqual(got["top"]["n"], 10)
        self.assertEqual(got["top"]["outer"], 3)
        self.assertEqual(got["top"]["sides"]["E"]["n"], 3)
        self.assertTrue(got["top"]["meets_rule2b"])         # 3 of 10 on one side

    def test_exactly_30_percent_meets_and_one_below_does_not(self) -> None:
        self.assertTrue(self._crowding([self._tally("top", {"c": 7, "w": 3})])["top"]["meets_rule2b"])
        self.assertFalse(self._crowding([self._tally("top", {"c": 8, "w": 2})])["top"]["meets_rule2b"])

    def test_a_diagonal_counts_for_both_sides_it_touches(self) -> None:
        got = self._crowding([self._tally("top", {"c": 7, "ne": 3})])["top"]
        self.assertEqual((got["sides"]["N"]["n"], got["sides"]["E"]["n"]), (3, 3))
        self.assertEqual(got["outer"], 3)

    def test_lines_written_at_the_20_dwell_minimum_read_the_same_as_one_summed_line(self) -> None:
        # The recorder carries an edge's counts across shows and writes a line only at 20 dwells, so a day
        # is a few lines of >= 20 each (spanning several shows), never one line per show. Summing them must
        # give exactly the verdict one big line gives.
        carried = [self._tally("top", {"c": 14, "e": 6}, second=1), self._tally("bottom", {"c": 20}, second=2),
                   self._tally("top", {"c": 14, "e": 6}, second=3), self._tally("top", {"c": 12, "e": 8}, second=4)]
        one = [self._tally("top", {"c": 40, "e": 20}, second=1), self._tally("bottom", {"c": 20}, second=2)]
        a = kb.metrics(kb.parse(carried))["edge_crowding"]        # the real minimum, 50 dwells
        b = kb.metrics(kb.parse(one))["edge_crowding"]
        self.assertEqual(a, b)
        self.assertEqual(a["top"]["n"], 60)
        self.assertTrue(a["top"]["meets_rule2b"])               # 20 of 60 = 1/3 on the east side
        self.assertFalse(a["bottom"]["meets_rule2b"])           # 20 dwells: under the 50 minimum

    def test_tallies_of_one_edge_add_up_and_edges_stay_apart(self) -> None:
        lines = [self._tally("top", {"c": 6, "e": 1}, second=1), self._tally("bottom", {"c": 10, "w": 5}, second=2),
                 self._tally("top", {"c": 2, "e": 1}, second=3)]
        got = self._crowding(lines)
        self.assertEqual(got["top"]["n"], 10)
        self.assertEqual(got["top"]["sides"]["E"]["n"], 2)
        self.assertFalse(got["top"]["meets_rule2b"])        # 2 of 10
        self.assertEqual(got["bottom"]["n"], 15)
        self.assertTrue(got["bottom"]["meets_rule2b"])      # 5 of 15 = 1/3

    def test_abandonments_are_not_counted_as_accepted_dwells(self) -> None:
        got = self._crowding([self._tally("top", {"c": 10}, aborts={"e": 9})])["top"]
        self.assertEqual(got["n"], 10)
        self.assertFalse(got["meets_rule2b"])

    def test_the_minimum_number_of_dwells_is_enforced(self) -> None:
        line = self._tally("top", {"c": 34, "e": 15})
        self.assertFalse(self._crowding([line], min_dwells=50)["top"]["meets_rule2b"])
        self.assertTrue(self._crowding([line], min_dwells=49)["top"]["meets_rule2b"])

    def test_an_empty_tally_adds_no_edge(self) -> None:
        self.assertEqual(self._crowding([self._tally("top", {})]), {})

    def test_a_damaged_tally_line_is_skipped(self) -> None:
        line = self._tally("top", {"c": 7, "e": 3}).replace(" nw=0", "")
        self.assertEqual(self._crowding([line]), {})
        self.assertEqual(self._crowding(["2026-10-19 10:00:00.000 [INF] [X] KB-E1 edgeq edge=top fires=x"]), {})

    def test_a_tally_is_attributed_to_the_edge_it_counted_not_the_edge_in_force(self) -> None:
        lines = [
            "2026-10-19 10:00:00.000 [INF] [X] KB-E1 shown proc=a.exe edge=bottom via=ring boxw=1 boxh=1",
            self._tally("top", {"c": 7, "e": 3}, second=1),
        ]
        self.assertEqual(list(self._crowding(lines)), ["top"])

    def test_mixed_old_and_new_lines_are_summed(self) -> None:
        old = EdgeCrowding._keys(["E"] * 3 + ["-"] * 2, dock="top")
        new = [self._tally("top", {"c": 3, "e": 2}, second=30)]
        got = self._crowding(old + new)["top"]
        self.assertEqual(got["n"], 10)
        self.assertEqual(got["sides"]["E"]["n"], 5)

    def test_the_tally_and_the_older_per_line_form_give_the_same_verdict(self) -> None:
        for quarters in (["E"] * 3 + ["-"] * 7, ["E"] * 2 + ["-"] * 8, ["NE"] * 4 + ["SW"] * 2 + ["-"] * 14,
                         ["-"] * 12, ["N", "S", "E", "W"] * 3 + ["-"] * 8):
            counts = {name: sum(1 for q in quarters if (q if q != "-" else "c").lower() == name) for name in self.NAMES}
            old = self._crowding(EdgeCrowding._keys(quarters))
            new = self._crowding([self._tally("top", counts)])
            self.assertEqual(new, old, quarters)

    def test_the_fixture_gives_the_same_rule_2b_in_both_forms(self) -> None:
        new = kb.metrics(kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines()), min_edge_dwells=2)
        old = kb.metrics(kb.parse(LEGACY_FIXTURE.read_text(encoding="utf-8").splitlines()), min_edge_dwells=2)
        self.assertEqual(new["edge_crowding"], old["edge_crowding"])
        self.assertEqual(new["edge_crowding"]["top"]["n"], 9)
        self.assertEqual(new["edge_crowding"]["top"]["sides"]["E"]["n"], 4)
        self.assertEqual(new["edge_crowding"]["bottom"]["sides"]["W"]["n"], 1)

    def test_the_whole_report_is_the_same_in_both_forms_apart_from_the_edgeq_lines(self) -> None:
        new = kb.metrics(kb.parse(FIXTURE.read_text(encoding="utf-8").splitlines()))
        old = kb.metrics(kb.parse(LEGACY_FIXTURE.read_text(encoding="utf-8").splitlines()))
        self.assertEqual({k: v for k, v in new.items() if k != "events"}, {k: v for k, v in old.items() if k != "events"})

    def test_the_fixture_tally_lines_count_only(self) -> None:
        tallies = [l for l in FIXTURE.read_text(encoding="utf-8").splitlines() if "KB-E1 edgeq " in l]
        self.assertEqual(len(tallies), 2)
        for line in tallies:
            self.assertNotRegex(line, r" (r|act|boxw|boxh|dx|dy|mdx|mdy|saccade|restarts)=")


if __name__ == "__main__":
    unittest.main()
