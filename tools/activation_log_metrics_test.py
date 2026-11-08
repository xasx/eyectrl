#!/usr/bin/env python3
"""Self-test for tools/activation_log_metrics.py.

    python tools/activation_log_metrics_test.py

Stdlib only. The script turns the rest-activation day-log lines into the counts the owner's Q5
answer reads (rests started / cancelled / fired / accepted-vs-declined / action-vs-closed), so a
wrong regex or a mis-keyed outcome would steer the unintended-open count with nothing red. This
suite runs it over a synthetic day log whose every count was worked out by hand, plus
damaged-input cases.

The fixture copies REAL renders (R3 review F4/F5): one line of every kind the tool reads, plus
the State/closed/Offer engine lines around them, rendered by the window-free adapter chain through
LoggingInitializer.OutputTemplate, invariant culture and the MEL→Serilog bridge (08.11). The real
shapes the hand-written fixture used to miss: the outcome line is written through the static
Serilog logger, so its source context is EMPTY (`[INF] []`); the expiry and the engine lines come
from `[EyeCtrl.Core.GazeEngine]`; the accepted line quotes its enum (`→ "None"`).

The synthetic day (rest + button styles mixed, plus noise):

  rest:   shown #1..#6 (6), expired #1 #2 #4 (3), fired #3 #5 #6 (3), accepted #3 #5 #6,
          outcomes: #3 action, #5 closed (1500 ms), #6 paused
  button: (the ids restart with the rebuilt engine) shown #1 #2 #3 (3), expired #2 (1),
          fired #1 #3; #1 accepted (outcome action), #3 DECLINED (no control found — a fire need
          not be accepted)
  noise:  Offer closed, State and Offer engine lines, a Ring arm line (with the rest
          parenthetical), an Action: line, a rearm refusal and a KB-E1 line — no count moves.
  damaged: a cut-off 'Activation outcome' (heldMs= absent), an 'Offer fired' without style= and
          an 'Offer shown' with a truncated style= are skipped, never half-counted.

So: rests started 9 (rest 6, button 3), cancelled 4 (rest 3, button 1), fired 5 (rest 3,
button 2), accepted 4, declined 1 (no control found), fired_undecided 0, outcomes
action 2 / closed 1 / paused 1 / other 0, unintended opens 1, accepted_without_outcome 0,
median closed held 1500.
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
TOOL = Path(__file__).resolve().parent / "activation_log_metrics.py"

_spec = importlib.util.spec_from_file_location("activation_log_metrics", TOOL)
alm = importlib.util.module_from_spec(_spec)
sys.modules["activation_log_metrics"] = alm
_spec.loader.exec_module(alm)

SYNTHETIC = "\n".join([
    # ── rest style ──
    # VERBATIM real render (#1 shown/expired, #3 and its State/closed/outcome lines): the window-free
    # adapter chain logged through LoggingInitializer.OutputTemplate, invariant culture and the
    # MEL→Serilog bridge, 08.11 (holistic fixup-01, R3 review F4/F5). The other lines follow it.
    "2026-11-08 22:37:15.856 [INF] [EyeCtrl.Core.GazeEngine] Offer engine: 3840×2400 px @2, ring 250, R 90, dwell 700, linger 500 ms, style=rest, H 135 px, G 500 ms",
    "2026-11-08 22:37:16.167 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] State → \"Fixation\"",
    "2026-11-08 22:37:16.168 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #1 P=(1000,1000) B=(1000,1000) style=rest",
    "2026-11-08 22:37:16.479 [INF] [EyeCtrl.Core.GazeEngine] Offer expired unused #1 (dwell 21 %) style=rest",
    "2026-11-08 22:37:16.479 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer closed #1 style=rest",
    "2026-11-08 22:37:16.479 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #2 P=(3000,2000) B=(3000,2000) style=rest",
    "2026-11-08 22:37:16.900 [INF] [EyeCtrl.Core.GazeEngine] Offer expired unused #2 (dwell 90 %) style=rest",
    "2026-11-08 22:37:17.101 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #3 P=(2000,1200) B=(2000,1200) style=rest",
    "2026-11-08 22:37:17.429 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #3 style=rest",
    "2026-11-08 22:37:17.430 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] State → \"ActionMenuVisible\"",
    "2026-11-08 22:37:17.430 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #3 → \"None\" at P=(2000,1200)",
    "2026-11-08 22:37:17.500 [INF] [EyeCtrl.App.Services.KeyboardInteractionDispatcher] Action: LeftClick at (2000,1200) [PreciseTarget=False] [Modifiers=] [BackdropVisible=False] [CursorBefore=(2000,1200)] [CursorAfter=(2000,1200)] [TargetProcess=unknown] [TargetElevated=False]",
    "2026-11-08 22:37:17.756 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] State → \"FreeLook\"",
    "2026-11-08 22:37:17.756 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer closed #3 style=rest",
    "2026-11-08 22:37:17.760 [INF] [] Activation outcome #3 style=rest mode=None result=action heldMs=329",
    "2026-11-08 22:38:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #4 P=(200,200) B=(200,200) style=rest",
    "2026-11-08 22:38:00.500 [INF] [EyeCtrl.Core.GazeEngine] Offer expired unused #4 (dwell 70 %) style=rest",
    "2026-11-08 22:39:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #5 P=(300,300) B=(300,300) style=rest",
    "2026-11-08 22:39:00.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #5 style=rest",
    "2026-11-08 22:39:00.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #5 → \"ManualSelection\" at P=(300,300)",
    "2026-11-08 22:39:02.200 [INF] [] Activation outcome #5 style=rest mode=ManualSelection result=closed heldMs=1500",
    "2026-11-08 22:40:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #6 P=(400,400) B=(400,400) style=rest",
    "2026-11-08 22:40:00.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #6 style=rest",
    "2026-11-08 22:40:00.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #6 → \"ManualSelection\" at P=(400,400)",
    "2026-11-08 22:40:01.100 [INF] [] Activation outcome #6 style=rest mode=ManualSelection result=paused heldMs=390",
    # ── button style ── (a style switch rebuilds the engine, so the ids restart at #1, as the
    # real render shows; the tool counts by kind and style, never by id)
    "2026-11-08 22:41:00.000 [INF] [EyeCtrl.Core.GazeEngine] Offer engine: 3840×2400 px @2, ring 250, R 90, dwell 700, linger 750 ms, style=button",
    "2026-11-08 22:41:01.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #1 P=(500,500) B=(755,755) style=button",
    "2026-11-08 22:41:01.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #1 style=button",
    "2026-11-08 22:41:01.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #1 → \"AutoSelection\" at P=(500,500)",
    "2026-11-08 22:41:02.500 [INF] [] Activation outcome #1 style=button mode=AutoSelection result=action heldMs=790",
    "2026-11-08 22:42:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #2 P=(600,600) B=(855,855) style=button",
    "2026-11-08 22:42:00.800 [INF] [EyeCtrl.Core.GazeEngine] Offer expired unused #2 (dwell 12 %) style=button",
    # VERBATIM real render: a button fire DECLINED under AutoSelection (no control, no pixel fallback).
    "2026-11-08 22:37:19.343 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #3 P=(2000,1200) B=(1745,945) style=button",
    "2026-11-08 22:37:19.653 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #3 style=button",
    "2026-11-08 22:37:19.655 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] State → \"FreeLook\"",
    "2026-11-08 22:37:19.655 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer declined #3 (no control found)",
    # ── noise that must not move any count ──
    "2026-11-08 22:43:00.000 [INF] [EyeCtrl.App] Ring arm: hybrid (pin=RunOnly, hold=HybridFreeze, grace=500 ms once proven 90 ms, go=500 ms corridor=1.25R, glideOut=0) activation=rest (rest offers run pin=OfferFollowing, hold=None) [source: default]",
    "2026-11-08 22:43:00.200 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer refused at P=(700,700): rearm",
    "2026-11-08 22:43:00.300 [INF] [EyeCtrl.ViewModels.KeyboardViewModel] KB-E1 key seq=1 ts=500 cls=letter penalty=False bs=- movems=-1",
    # ── damaged lines: skipped, never half-counted ──
    "2026-11-08 22:44:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #10 style=",
    "2026-11-08 22:44:00.100 [INF] [] Activation outcome #10 style=rest mode=None result=closed",
    "2026-11-08 22:44:00.200 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #11 P=(1,1) B=(1,1) sty",
]) + "\n"

# A second, tiny day for the gap notes: a fire whose app died before the decision, and an accept
# whose session was still open at the day boundary (or the app quit) — no outcome line for it.
GAPS = "\n".join([
    "2026-11-08 10:00:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #1 P=(100,100) B=(100,100) style=rest",
    "2026-11-08 10:00:00.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #1 style=rest",
    "2026-11-08 10:00:01.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #2 P=(200,200) B=(200,200) style=rest",
    "2026-11-08 10:00:01.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #2 style=rest",
    "2026-11-08 10:00:01.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #2 → \"None\" at P=(200,200)",
    # no decision for #1, no outcome for #2 — the log ends here (crash or midnight)
]) + "\n"


def run_cli(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, "-I", str(TOOL), *args], capture_output=True, text=True)


def synthetic_metrics() -> dict:
    return alm.metrics(alm.parse(SYNTHETIC.splitlines()))


class WholeDay(unittest.TestCase):
    def setUp(self) -> None:
        self.m = synthetic_metrics()

    def test_the_four_headline_counts(self) -> None:
        self.assertEqual(self.m["rests_started"], {"rest": 6, "button": 3})
        self.assertEqual(self.m["rests_started_total"], 9)
        self.assertEqual(self.m["cancelled"], {"rest": 3, "button": 1})
        self.assertEqual(self.m["cancelled_total"], 4)
        self.assertEqual(self.m["fired"], {"rest": 3, "button": 2})
        self.assertEqual(self.m["fired_total"], 5)

    def test_fired_is_accepted_plus_declined_with_the_reasons(self) -> None:
        self.assertEqual(self.m["accepted"], 4)
        self.assertEqual(self.m["declined"], 1)
        self.assertEqual(self.m["declined_reasons"], {"no control found": 1})
        self.assertEqual(self.m["fired_undecided"], 0)

    def test_action_versus_closed_of_the_accepted(self) -> None:
        self.assertEqual(self.m["outcomes"], {"action": 2, "closed": 1, "paused": 1})
        self.assertEqual(self.m["outcomes_by_style"]["rest"], {"action": 1, "closed": 1, "paused": 1})
        self.assertEqual(self.m["outcomes_by_style"]["button"], {"action": 1})
        self.assertEqual(self.m["unintended_opens"], 1)
        self.assertEqual(self.m["median_closed_held_ms"], 1500)
        self.assertEqual(self.m["accepted_without_outcome"], 0)

    def test_an_outcome_with_a_state_still_parses(self) -> None:
        events = alm.parse([
            "x Activation outcome #9 style=rest mode=None result=other state=RepeatMode heldMs=61"])
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]["state"], "RepeatMode")
        self.assertEqual(events[0]["result"], "other")


class GapNotes(unittest.TestCase):
    def test_a_fired_without_a_decision_and_an_accepted_without_an_outcome_are_named(self) -> None:
        m = alm.metrics(alm.parse(GAPS.splitlines()))
        self.assertEqual(m["fired_total"], 2)
        self.assertEqual(m["accepted"], 1)
        self.assertEqual(m["declined"], 0)
        self.assertEqual(m["fired_undecided"], 1)
        self.assertEqual(m["accepted_without_outcome"], 1)

        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "eyectrl-20261108.log"
            log.write_text(GAPS, encoding="utf-8")
            done = run_cli(str(log))
            self.assertEqual(done.returncode, 0, done.stderr)
            collapsed = " ".join(done.stdout.split())
            self.assertIn("NOTE 1 accepted with no outcome line (day boundary, crash or quit)", collapsed)
            self.assertIn("NOTE 1 fired with neither accepted nor declined"
                          " (a crash or quit between the fire and the decision)", collapsed)


class Cli(unittest.TestCase):
    def test_prints_the_readouts(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "eyectrl-20261108.log"
            log.write_text(SYNTHETIC, encoding="utf-8")
            done = run_cli(str(log))
            self.assertEqual(done.returncode, 0, done.stderr)
            collapsed = " ".join(done.stdout.split())
            self.assertIn("rests started 9 (rest 6 button 3)", collapsed)
            self.assertIn("cancelled 4 (rest 3 button 1)", collapsed)
            self.assertIn("fired 5 (rest 3 button 2)", collapsed)
            self.assertIn("accepted 4 declined 1 (no control found 1)", collapsed)
            self.assertIn("UNINTENDED OPENS", done.stdout)
            self.assertIn("median held, closed 1500 ms", collapsed)
            self.assertNotIn("NOTE", done.stdout)

    def test_json_matches_the_module(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "eyectrl-20261108.log"
            log.write_text(SYNTHETIC, encoding="utf-8")
            done = run_cli(str(log), "--json")
            self.assertEqual(done.returncode, 0, done.stderr)
            self.assertEqual(json.loads(done.stdout), synthetic_metrics())

    def test_a_log_without_activation_lines_fails_cleanly(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "empty.log"
            log.write_text("nothing here\n", encoding="utf-8")
            done = run_cli(str(log))
            self.assertEqual(done.returncode, 1)
            self.assertIn("no activation lines", done.stderr)

    def test_a_missing_file_fails_cleanly(self) -> None:
        done = run_cli(str(REPO_ROOT / "tools" / "does-not-exist.log"))
        self.assertEqual(done.returncode, 2)


if __name__ == "__main__":
    unittest.main()
