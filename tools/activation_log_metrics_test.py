#!/usr/bin/env python3
"""Self-test for tools/activation_log_metrics.py.

    python tools/activation_log_metrics_test.py

Stdlib only. The script turns the activation-offer day-log lines into the counts the owner's
sittings read (offers shown / expired / never looked at / fired / accepted-vs-declined /
action-vs-closed / first look), so a wrong regex or a mis-keyed outcome would steer the
unintended-open count with nothing red. Two synthetic day logs whose every count was worked out
by hand, plus damaged-input cases.

TWO GENERATIONS of line must parse (round-button B1):

* SITTING_130 — the 1.3.0 sitting build (#322), every offer line with `style=rest|button` and no
  `firstLookMs`. Copied verbatim from #322's self-test, whose fixture copies REAL renders (R3
  review F4/F5): the outcome line is written through the static Serilog logger, so its source
  context is EMPTY (`[INF] []`); the expiry and the engine lines come from
  `[EyeCtrl.Core.GazeEngine]`; the accepted line quotes its enum (`→ "None"`).

    rest:   shown #1..#6 (6), expired #1 #2 #4 (3), fired #3 #5 #6 (3), accepted #3 #5 #6,
            outcomes: #3 action, #5 closed (1500 ms), #6 paused
    button: (the ids restart with the rebuilt engine) shown #1 #2 #3 (3), expired #2 (1),
            fired #1 #3; #1 accepted (outcome action), #3 DECLINED (no control found)
    noise and damaged lines: no count moves.

  So: shown 9 (rest 6, button 3), expired 4 (rest 3, button 1), never looked at 0 (no line
  carries the field), fired 5 (rest 3, button 2), accepted 4, declined 1, outcomes action 2 /
  closed 1 / paused 1, unintended opens 1, median closed held 1500, no first-look median.

* CURRENT — today's lines: one style (no `style=`, counted as `button`), `firstLookMs` on fired
  and expired. Shapes as the adapter and engine tests pin them (GazeEngineAdapterActivation-
  OutcomeTests, GazeEngineOfferFirstLookLogTests, ActivationOutcomeLogTests).

    shown #1..#5 (5); #1 expired never looked at (none), #2 expired after a glance (420);
    #3 fired (300) accepted → closed 1200 ms; #4 fired (500) accepted → action 2100 ms;
    #5 fired (700) DECLINED (no control found).
    noise and damaged lines: no count moves.

  So: shown 5 (button 5), expired 2, never looked at 1, fired 3, median first look 500,
  accepted 2, declined 1, outcomes action 1 / closed 1, unintended opens 1, median closed held
  1200, accepted_without_outcome 0.
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

SITTING_130 = "\n".join([
    # ── rest style ──
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
    # ── button style ── (a style switch rebuilds the engine, so the ids restart at #1)
    "2026-11-08 22:41:00.000 [INF] [EyeCtrl.Core.GazeEngine] Offer engine: 3840×2400 px @2, ring 250, R 90, dwell 700, linger 750 ms, style=button",
    "2026-11-08 22:41:01.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #1 P=(500,500) B=(755,755) style=button",
    "2026-11-08 22:41:01.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #1 style=button",
    "2026-11-08 22:41:01.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #1 → \"AutoSelection\" at P=(500,500)",
    "2026-11-08 22:41:02.500 [INF] [] Activation outcome #1 style=button mode=AutoSelection result=action heldMs=790",
    "2026-11-08 22:42:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #2 P=(600,600) B=(855,855) style=button",
    "2026-11-08 22:42:00.800 [INF] [EyeCtrl.Core.GazeEngine] Offer expired unused #2 (dwell 12 %) style=button",
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

CURRENT = "\n".join([
    "2026-11-10 09:00:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] State → \"Fixation\"",
    "2026-11-10 09:00:00.010 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #1 P=(1000,1000) B=(1255,1255)",
    "2026-11-10 09:00:00.800 [INF] [EyeCtrl.Core.GazeEngine] Offer expired unused #1 (dwell 0 %) firstLookMs=none",
    "2026-11-10 09:00:00.800 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer closed #1",
    "2026-11-10 09:00:01.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #2 P=(2000,1200) B=(1745,945)",
    "2026-11-10 09:00:02.100 [INF] [EyeCtrl.Core.GazeEngine] Offer expired unused #2 (dwell 34 %) firstLookMs=420",
    "2026-11-10 09:00:03.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #3 P=(400,400) B=(655,655)",
    "2026-11-10 09:00:03.800 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #3 firstLookMs=300",
    "2026-11-10 09:00:03.801 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] State → \"ManualSelectionActive\"",
    "2026-11-10 09:00:03.801 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #3 → \"ManualSelection\" at P=(400,400)",
    "2026-11-10 09:00:05.001 [INF] [] Activation outcome #3 mode=ManualSelection result=closed heldMs=1200",
    "2026-11-10 09:00:06.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #4 P=(500,500) B=(755,755)",
    "2026-11-10 09:00:07.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #4 firstLookMs=500",
    "2026-11-10 09:00:07.001 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #4 → \"ManualSelection\" at P=(500,500)",
    "2026-11-10 09:00:09.101 [INF] [] Activation outcome #4 mode=ManualSelection result=action heldMs=2100",
    "2026-11-10 09:00:10.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #5 P=(600,600) B=(855,855)",
    "2026-11-10 09:00:11.200 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #5 firstLookMs=700",
    "2026-11-10 09:00:11.210 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer declined #5 (no control found)",
    # ── noise ──
    "2026-11-10 09:00:12.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer refused at P=(10,10): in chrome",
    "2026-11-10 09:00:12.100 [INF] [EyeCtrl.App] Ring arm: hybrid (pin=RunOnly, hold=HybridFreeze) [source: default]",
    # ── damaged lines: skipped, never half-counted ──
    "2026-11-10 09:00:13.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #12 firstLookMs=",
    "2026-11-10 09:00:13.100 [INF] [EyeCtrl.Core.GazeEngine] Offer expired unused #13 (dwell 10 %) firstLook",
    "2026-11-10 09:00:13.200 [INF] [] Activation outcome #14 mode=None result=closed",
    "2026-11-10 09:00:13.300 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #15 P=(1,1) B=(1,",
]) + "\n"

# A tiny day for the gap notes: a fire whose app died before the decision, and an accept whose
# session was still open at the day boundary (or the app quit) — no outcome line for it.
GAPS = "\n".join([
    "2026-11-10 10:00:00.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #1 P=(100,100) B=(355,355)",
    "2026-11-10 10:00:00.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #1 firstLookMs=250",
    "2026-11-10 10:00:01.000 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer shown #2 P=(200,200) B=(455,455)",
    "2026-11-10 10:00:01.700 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer fired #2 firstLookMs=260",
    "2026-11-10 10:00:01.710 [INF] [EyeCtrl.App.Services.GazeEngineAdapter] Offer accepted #2 → \"None\" at P=(200,200)",
    # no decision for #1, no outcome for #2 — the log ends here (crash or midnight)
]) + "\n"


def run_cli(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, "-I", str(TOOL), *args], capture_output=True, text=True)


def metrics_of(text: str) -> dict:
    return alm.metrics(alm.parse(text.splitlines()))


class Sitting130Day(unittest.TestCase):
    """The owner's 1.3.0 sitting logs (`style=rest|button`) still parse."""

    def setUp(self) -> None:
        self.m = metrics_of(SITTING_130)

    def test_the_headline_counts_per_style(self) -> None:
        self.assertEqual(self.m["offers_shown"], {"rest": 6, "button": 3})
        self.assertEqual(self.m["offers_shown_total"], 9)
        self.assertEqual(self.m["expired"], {"rest": 3, "button": 1})
        self.assertEqual(self.m["expired_total"], 4)
        self.assertEqual(self.m["fired"], {"rest": 3, "button": 2})
        self.assertEqual(self.m["fired_total"], 5)

    def test_old_lines_carry_no_first_look(self) -> None:
        self.assertEqual(self.m["expired_never_looked_at"], 0)
        self.assertIsNone(self.m["median_fired_first_look_ms"])

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


class CurrentDay(unittest.TestCase):
    """Today's lines: no `style=` (= button), `firstLookMs` on fired and expired."""

    def setUp(self) -> None:
        self.m = metrics_of(CURRENT)

    def test_the_headline_counts_all_count_as_button(self) -> None:
        self.assertEqual(self.m["offers_shown"], {"rest": 0, "button": 5})
        self.assertEqual(self.m["offers_shown_total"], 5)
        self.assertEqual(self.m["expired"], {"rest": 0, "button": 2})
        self.assertEqual(self.m["expired_total"], 2)
        self.assertEqual(self.m["fired"], {"rest": 0, "button": 3})
        self.assertEqual(self.m["fired_total"], 3)

    def test_first_look_median_and_never_looked_at(self) -> None:
        self.assertEqual(self.m["median_fired_first_look_ms"], 500)
        self.assertEqual(self.m["expired_never_looked_at"], 1)

    def test_fired_accepted_declined_and_the_outcomes(self) -> None:
        self.assertEqual(self.m["accepted"], 2)
        self.assertEqual(self.m["declined"], 1)
        self.assertEqual(self.m["declined_reasons"], {"no control found": 1})
        self.assertEqual(self.m["fired_undecided"], 0)
        self.assertEqual(self.m["outcomes"], {"action": 1, "closed": 1})
        self.assertEqual(self.m["outcomes_by_style"]["button"], {"action": 1, "closed": 1})
        self.assertEqual(self.m["unintended_opens"], 1)
        self.assertEqual(self.m["median_closed_held_ms"], 1200)
        self.assertEqual(self.m["accepted_without_outcome"], 0)


class LineForms(unittest.TestCase):
    def test_a_line_without_style_is_the_button_and_an_old_rest_line_stays_rest(self) -> None:
        events = alm.parse([
            "x Offer fired #1 firstLookMs=330",
            "x Offer fired #2 style=rest",
            "x Offer fired #3 style=button",
            "x Offer shown #4 P=(1,1) B=(2,2)",
            "x Activation outcome #5 mode=None result=action heldMs=10",
            "x Activation outcome #6 style=rest mode=None result=action heldMs=10",
        ])
        self.assertEqual([e["style"] for e in events], ["button", "rest", "button", "button", "button", "rest"])
        self.assertEqual(events[0]["first_look"], "330")
        self.assertEqual(events[1]["first_look"], "")

    def test_an_outcome_with_a_state_still_parses_in_both_forms(self) -> None:
        events = alm.parse([
            "x Activation outcome #9 mode=None result=other state=RepeatMode heldMs=61",
            "x Activation outcome #9 style=rest mode=None result=other state=RepeatMode heldMs=61",
        ])
        self.assertEqual(len(events), 2)
        for e in events:
            self.assertEqual(e["state"], "RepeatMode")
            self.assertEqual(e["result"], "other")

    def test_a_quoting_sink_still_counts(self) -> None:
        events = alm.parse(['x Offer fired #1 firstLookMs="330"',
                            'x Offer expired unused #2 (dwell 0 %) firstLookMs="none"'])
        self.assertEqual([e["first_look"] for e in events], ["330", "none"])


class GapNotes(unittest.TestCase):
    def test_a_fired_without_a_decision_and_an_accepted_without_an_outcome_are_named(self) -> None:
        m = metrics_of(GAPS)
        self.assertEqual(m["fired_total"], 2)
        self.assertEqual(m["accepted"], 1)
        self.assertEqual(m["declined"], 0)
        self.assertEqual(m["fired_undecided"], 1)
        self.assertEqual(m["accepted_without_outcome"], 1)

        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "eyectrl-20261110.log"
            log.write_text(GAPS, encoding="utf-8")
            done = run_cli(str(log))
            self.assertEqual(done.returncode, 0, done.stderr)
            collapsed = " ".join(done.stdout.split())
            self.assertIn("NOTE 1 accepted with no outcome line (day boundary, crash or quit)", collapsed)
            self.assertIn("NOTE 1 fired with neither accepted nor declined"
                          " (a crash or quit between the fire and the decision)", collapsed)


class Cli(unittest.TestCase):
    def _run(self, text: str, *extra: str) -> subprocess.CompletedProcess:
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "eyectrl-20261110.log"
            log.write_text(text, encoding="utf-8")
            return run_cli(str(log), *extra)

    def test_prints_the_offer_readouts_for_a_current_log(self) -> None:
        done = self._run(CURRENT)
        self.assertEqual(done.returncode, 0, done.stderr)
        collapsed = " ".join(done.stdout.split())
        self.assertIn("offers shown 5 expired 2", collapsed)   # no per-style split without rest offers
        self.assertIn("never looked at 1 (expired with firstLookMs=none)", collapsed)
        self.assertIn("fired 3 accepted 2 declined 1 (no control found 1)", collapsed)
        self.assertIn("UNINTENDED OPENS", done.stdout)
        self.assertIn("median first look, fired 500 ms", collapsed)
        self.assertIn("median held, closed 1200 ms", collapsed)
        self.assertNotIn("rest", done.stdout)
        self.assertNotIn("NOTE", done.stdout)

    def test_prints_the_style_split_for_a_130_log(self) -> None:
        done = self._run(SITTING_130)
        self.assertEqual(done.returncode, 0, done.stderr)
        collapsed = " ".join(done.stdout.split())
        self.assertIn("offers shown 9 (button 3 rest 6)", collapsed)
        self.assertIn("expired 4 (button 1 rest 3)", collapsed)
        self.assertIn("fired 5 (button 2 rest 3)", collapsed)
        self.assertIn("accepted 4 declined 1 (no control found 1)", collapsed)
        self.assertIn("median held, closed 1500 ms", collapsed)
        self.assertNotIn("median first look", collapsed)
        self.assertNotIn("NOTE", done.stdout)

    def test_json_matches_the_module(self) -> None:
        for text in (CURRENT, SITTING_130):
            done = self._run(text, "--json")
            self.assertEqual(done.returncode, 0, done.stderr)
            self.assertEqual(json.loads(done.stdout), metrics_of(text))

    def test_a_log_without_activation_lines_fails_cleanly(self) -> None:
        done = self._run("nothing here\n")
        self.assertEqual(done.returncode, 1)
        self.assertIn("no activation lines", done.stderr)

    def test_a_missing_file_fails_cleanly(self) -> None:
        done = run_cli(str(REPO_ROOT / "tools" / "does-not-exist.log"))
        self.assertEqual(done.returncode, 2)


if __name__ == "__main__":
    unittest.main()
