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

    def test_hides_by_cause(self) -> None:
        self.assertEqual(self.m["hides_by_cause"], {"toolbar-toggle": 1, "edge-close": 1})


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
        line = ("2026-10-19 10:00:00.000 [INF] [X] KB-E1 key seq=1 ts=5 cls=letter likely=True start=False "
                "nocont=False penalty=False bs=- movems=-1")
        self.assertEqual(len(kb.parse([line])), 1)
        self.assertEqual(kb.parse([line.replace("movems=-1", "")]), [])
        self.assertEqual(kb.parse([line.replace("likely=True", "likely=1")]), [])   # wrong type

    def test_quoted_strings_are_accepted(self) -> None:
        events = kb.parse(['2026-10-19 10:00:00.000 [INF] [X] KB-E1 shown proc="WINWORD.exe" edge="top" via="ring"'])
        self.assertEqual(events[0]["proc"], "WINWORD.exe")

    def test_a_slip_needs_the_backspace_to_be_the_very_next_activation(self) -> None:
        def key(ms: int, cls: str, seq: int, bs: str = "-") -> str:
            return (f"2026-10-19 10:00:{ms // 1000:02d}.{ms % 1000:03d} [INF] [X] KB-E1 key seq={seq} ts={ms} "
                    f"cls={cls} likely=False start=False nocont=False penalty=False bs={bs} movems=-1")
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


if __name__ == "__main__":
    unittest.main()
