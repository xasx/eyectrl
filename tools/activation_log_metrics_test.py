#!/usr/bin/env python3
"""Self-test for tools/activation_log_metrics.py.

    python tools/activation_log_metrics_test.py

Stdlib only. The script turns the rest-activation day-log lines into the four counts the owner's
Q5 answer reads (rests started / cancelled / fired / action-vs-closed), so a wrong regex or a
mis-keyed outcome would steer the unintended-open count with nothing red. This suite runs it over
a synthetic day log whose every count was worked out by hand, plus damaged-input cases.

The synthetic day (rest + button styles mixed, plus noise):

  rest:   shown #1..#6 (6), expired #1 #2 #4 (3), fired #3 #5 #6 (3),
          outcomes: #3 action, #5 closed (1500 ms), #6 paused
  button: shown #7 #8 (2), expired #8 (1), fired #7 (1), outcome: #7 action
  noise:  an Offer closed line, a Ring arm line (with activation=rest), an Action: line, a
          rearm refusal, a KB-E1 line and an Offer engine line — none of the four counts move.
  damaged: a cut-off 'Activation outcome' (heldMs= absent), an 'Offer fired' without style= and
          an 'Offer shown' with a truncated style= are skipped, never half-counted.

So: rests started 8 (rest 6, button 2), cancelled 4 (rest 3, button 1), fired 4 (rest 3,
button 1), outcomes action 2 / closed 1 / paused 1 / other 0, unintended opens 1,
fired_without_outcome 0, median closed held 1500.
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
    # A real log's prefix shape (Serilog render), rest + button mixed.
    "2026-11-08 09:00:00.100 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #1 P=(1000,1000) B=(1000,1000) style=rest",
    "2026-11-08 09:00:00.900 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer expired unused #1 (dwell 41 %) style=rest",
    "2026-11-08 09:01:02.200 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #2 P=(1200,1400) B=(1200,1400) style=rest",
    "2026-11-08 09:01:03.100 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer expired unused #2 (dwell 90 %) style=rest",
    "2026-11-08 09:02:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #3 P=(800,900) B=(800,900) style=rest",
    "2026-11-08 09:02:00.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #3 style=rest",
    "2026-11-08 09:02:00.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #3 → None at P=(800,900)",
    "2026-11-08 09:02:01.900 [INF] [EyeCtrl.App.Services.KeyboardInteractionDispatcher] Action: LeftClick at (800,900) [PreciseTarget=False] [Modifiers=] [BackdropVisible=False] [CursorBefore=(800,900)] [CursorAfter=(800,900)] [TargetProcess=unknown] [TargetElevated=False]",
    "2026-11-08 09:02:02.200 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Activation outcome #3 style=rest mode=None result=action heldMs=1400",
    "2026-11-08 09:03:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #4 P=(200,200) B=(200,200) style=rest",
    "2026-11-08 09:03:00.500 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer expired unused #4 (dwell 70 %) style=rest",
    "2026-11-08 09:04:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #5 P=(300,300) B=(300,300) style=rest",
    "2026-11-08 09:04:00.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #5 style=rest",
    "2026-11-08 09:04:00.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #5 → ManualSelection at P=(300,300)",
    "2026-11-08 09:04:02.200 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Activation outcome #5 style=rest mode=ManualSelection result=closed heldMs=1500",
    "2026-11-08 09:05:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #6 P=(400,400) B=(400,400) style=rest",
    "2026-11-08 09:05:00.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #6 style=rest",
    "2026-11-08 09:05:00.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #6 → ManualSelection at P=(400,400)",
    "2026-11-08 09:05:01.100 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Activation outcome #6 style=rest mode=ManualSelection result=paused heldMs=390",
    "2026-11-08 09:06:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #7 P=(500,500) B=(900,650) style=button",
    "2026-11-08 09:06:00.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #7 style=button",
    "2026-11-08 09:06:00.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #7 → AutoSelection at P=(500,500)",
    "2026-11-08 09:06:01.500 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Activation outcome #7 style=button mode=AutoSelection result=action heldMs=790",
    "2026-11-08 09:07:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #8 P=(600,600) B=(1000,750) style=button",
    "2026-11-08 09:07:00.800 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer expired unused #8 (dwell 12 %) style=button",
    # Noise that must not move any count.
    "2026-11-08 09:08:00.000 [INF] [EyeCtrl.App] Ring arm: hybrid (pin=RunOnly, hold=HybridFreeze, grace=500 ms once proven 90 ms, go=500 ms corridor=1.25R, glideOut=0) activation=rest [source: default]",
    "2026-11-08 09:08:00.100 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer closed #7 style=button",
    "2026-11-08 09:08:00.200 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer refused at P=(700,700): rearm",
    "2026-11-08 09:08:00.300 [INF] [EyeCtrl.ViewModels.KeyboardViewModel] KB-E1 key seq=1 ts=500 cls=letter penalty=False bs=- movems=-1",
    "2026-11-08 09:08:00.400 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer engine: chrome 3 zone(s), linger 750 ms, style=rest, H 120 px, G 500 ms",
    # Damaged lines: skipped, never half-counted.
    "2026-11-08 09:09:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #9 style=",
    "2026-11-08 09:09:00.100 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Activation outcome #9 style=rest mode=None result=closed",
    "2026-11-08 09:09:00.200 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #10 P=(1,1) B=(1,1) sty",
]) + "\n"


def run_cli(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, "-I", str(TOOL), *args], capture_output=True, text=True)


def synthetic_metrics() -> dict:
    return alm.metrics(alm.parse(SYNTHETIC.splitlines()))


class WholeDay(unittest.TestCase):
    def setUp(self) -> None:
        self.m = synthetic_metrics()

    def test_the_four_counts(self) -> None:
        self.assertEqual(self.m["rests_started"], {"rest": 6, "button": 2})
        self.assertEqual(self.m["rests_started_total"], 8)
        self.assertEqual(self.m["cancelled"], {"rest": 3, "button": 1})
        self.assertEqual(self.m["cancelled_total"], 4)
        self.assertEqual(self.m["fired"], {"rest": 3, "button": 1})
        self.assertEqual(self.m["fired_total"], 4)

    def test_action_versus_closed_of_the_fired(self) -> None:
        self.assertEqual(self.m["outcomes"], {"action": 2, "closed": 1, "paused": 1})
        self.assertEqual(self.m["outcomes_by_style"]["rest"], {"action": 1, "closed": 1, "paused": 1})
        self.assertEqual(self.m["outcomes_by_style"]["button"], {"action": 1})
        self.assertEqual(self.m["unintended_opens"], 1)
        self.assertEqual(self.m["median_closed_held_ms"], 1500)
        self.assertEqual(self.m["fired_without_outcome"], 0)

    def test_an_outcome_with_a_state_still_parses(self) -> None:
        events = alm.parse([
            "x Activation outcome #9 style=rest mode=None result=other state=RepeatMode heldMs=61"])
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]["state"], "RepeatMode")
        self.assertEqual(events[0]["result"], "other")


class Cli(unittest.TestCase):
    def test_prints_the_readouts(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "eyectrl-20261108.log"
            log.write_text(SYNTHETIC, encoding="utf-8")
            done = run_cli(str(log))
            self.assertEqual(done.returncode, 0, done.stderr)
            collapsed = " ".join(done.stdout.split())
            self.assertIn("rests started 8 (rest 6 button 2)", collapsed)
            self.assertIn("cancelled 4 (rest 3 button 1)", collapsed)
            self.assertIn("fired 4 (rest 3 button 1)", collapsed)
            self.assertIn("UNINTENDED OPENS", done.stdout)
            self.assertIn("median held, closed 1500 ms", collapsed)

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
