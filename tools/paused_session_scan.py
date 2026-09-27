#!/usr/bin/env python3
"""Flag EyeCtrl sessions stranded in Paused (paused-trap guard, ticket 1).

Reads EyeCtrl Serilog files and reports sessions where the user was stranded in Paused. A session
flags when either:

  * it spent more than 60 seconds Paused *and* summoned the toolbar inside a pause that never
    ended — a user who was looking for a way out and never found it (this is what 2026-09-24
    looked like: 1 h 43 min, 10 paused summons, never resumed). Since the 2026-09-26 reversal the
    way back is summon → dwell Play, so a paused summon followed by a resume is the success path
    and must NOT flag;
  * it was still Paused 5 minutes or more after the pause started and ended in a clean Shutdown,
    whether or not any summon was logged; or
  * it was still Paused 5 minutes or more after the pause started and ended UNcleanly (kill,
    crash, reboot, or still running when scanned), for `provider=\"StreamEngine\"` sessions only.

The band hides again while paused, so a stranded session can summon and still never get out
(dwell Play is the step 2026-09-24 failed at), and a session where no summon ever happens is still
caught by time alone. The unclean-end case excludes Mouse-provider sessions because those are
dev/mouse-as-gaze runs (the 2026-09-16 restarts), not a stranded eye user. A run that resumed
before it ended is not a stranding.

Usage:
    python tools/paused_session_scan.py                       # scan %APPDATA%/EyeCtrl/logs
    python tools/paused_session_scan.py path/to/eyectrl-*.log
    python tools/paused_session_scan.py --check ...           # exit 1 when anything flags
    python tools/paused_session_scan.py --selftest            # synthetic checks, no files needed

The scan is deliberately a script under tools/ rather than test code: it reads the user's live
logs, which are machine-local and not in the repo. Ticket 2's replay tooling can absorb it later.
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime as _dt
import glob
import os
import re
import sys

PAUSE_FLAG_SECONDS = 60
STRANDED_FLAG_SECONDS = 300

_TIMESTAMP = r"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})"
_LOG_LINE = re.compile(r"^" + _TIMESTAMP + r" \[\w+\] \[[^\]]*\] (.*)$")
_STATE = re.compile(r'^State \u2192 "([A-Za-z]+)"$')
_PROVIDER = re.compile(r'provider="([^"]*)"')
_STARTUP_PREFIX = "Startup: EyeCtrl"
_SHUTDOWN = "Shutdown"
_SUMMON_PREFIX = "Toolbar summoned:"


@dataclasses.dataclass
class Session:
    """One app run: from a Startup line to Shutdown / the next Startup / EOF."""

    started: _dt.datetime
    provider: str | None = None
    ended: _dt.datetime | None = None
    paused_seconds: float = 0.0
    # The tail of a run that ended in a CLEAN Shutdown while still Paused. This is the "no resume"
    # signal: the user never came back before the session was closed.
    paused_at_clean_end_seconds: float = 0.0
    # The tail of a run that ended UNcleanly (kill, crash, reboot, or still running at EOF) while
    # still Paused. Only judged for eye-tracker sessions — the 2026-09-16 mouse-provider dev
    # restarts are exactly the noise this stays out of.
    paused_at_unclean_end_seconds: float = 0.0
    summons: int = 0
    # Summons logged while Paused, and whether the last of them was followed by a resume. Rule 1
    # asks what the 2026-09-26 reversal changed: a paused summon is only evidence of a stranding
    # if its pause never ended — pause → hide → summon → resume is now the normal way back.
    paused_summons: int = 0
    last_paused_summon_resolved: bool = True
    saw_startup: bool = True

    @property
    def flagged(self) -> bool:
        # Toolbar-based rule: a long pause the user tried to escape from (summoned the toolbar
        # while paused) and never left. Only an UNRESOLVED paused summon counts — a summon a
        # resume followed is the success path. Still what 2026-09-24 looked like, and it catches
        # strandings shorter than the 300 s the time-based rule needs.
        if (self.paused_seconds > PAUSE_FLAG_SECONDS
                and self.paused_summons >= 1
                and not self.last_paused_summon_resolved):
            return True
        # Time-based rule: a stranded session may never log a summon (or the auto-hide may never
        # fire). Any run that sat Paused for more than 5 minutes with no resume is a stranding —
        # whether it ended in a clean Shutdown, or (eye-tracker sessions only) went down uncleanly.
        if self.paused_at_clean_end_seconds > STRANDED_FLAG_SECONDS:
            return True
        return (self.provider == "StreamEngine"
                and self.paused_at_unclean_end_seconds > STRANDED_FLAG_SECONDS)

    def describe(self) -> str:
        end = self.ended.strftime("%Y-%m-%d %H:%M:%S") if self.ended else "(no shutdown line)"
        detail = f"paused {self.paused_seconds:.0f}s"
        if self.paused_at_clean_end_seconds > 0:
            detail += f" (of which {self.paused_at_clean_end_seconds:.0f}s unresolved at shutdown)"
        if self.paused_at_unclean_end_seconds > 0:
            detail += (f" (of which {self.paused_at_unclean_end_seconds:.0f}s unresolved at an "
                       "unclean end)")
        return (
            f"{self.started:%Y-%m-%d %H:%M:%S} -> {end} [{self.provider or 'unknown'}]: "
            f"{detail}, {self.summons} toolbar summon(s)"
        )


def _parse_timestamp(text: str) -> _dt.datetime:
    return _dt.datetime.strptime(text, "%Y-%m-%d %H:%M:%S.%f")


def parse_lines(lines) -> list[Session]:
    """Group a log's lines into sessions and measure each session's paused time."""
    sessions: list[Session] = []
    current: Session | None = None
    paused_since: _dt.datetime | None = None
    last_timestamp: _dt.datetime | None = None

    def close(at: _dt.datetime | None, cleaned: bool) -> None:
        nonlocal current, paused_since
        if current is None:
            return
        if paused_since is not None:
            tail = ((at or paused_since) - paused_since).total_seconds()
            current.paused_seconds += tail
            if cleaned:
                current.paused_at_clean_end_seconds = tail
            else:
                current.paused_at_unclean_end_seconds = tail
            paused_since = None
        current.ended = at
        sessions.append(current)
        current = None

    for raw in lines:
        match = _LOG_LINE.match(raw.rstrip("\r\n"))
        if not match:
            continue
        timestamp = _parse_timestamp(match.group(1))
        message = match.group(2)
        last_timestamp = timestamp

        if message.startswith(_STARTUP_PREFIX):
            # A startup closes the previous run (restart inside one rolling file).
            close(timestamp, cleaned=False)
            provider = _PROVIDER.search(message)
            current = Session(started=timestamp, provider=provider.group(1) if provider else None)
            continue

        if current is None:
            # Log lines from before this file's first Startup line (rolling files are appended to
            # across runs; the head can be a previous process's tail). Not a session to judge.
            continue

        if message == _SHUTDOWN:
            close(timestamp, cleaned=True)
            continue

        if message.startswith(_SUMMON_PREFIX):
            current.summons += 1
            if paused_since is not None:
                current.paused_summons += 1
                current.last_paused_summon_resolved = False
            continue

        state = _STATE.match(message)
        if state:
            if state.group(1) == "Paused":
                if paused_since is None:
                    paused_since = timestamp
            elif paused_since is not None:
                current.paused_seconds += (timestamp - paused_since).total_seconds()
                paused_since = None
                # The pause a paused summon was waiting in ended: the user got out.
                current.last_paused_summon_resolved = True

    close(last_timestamp, cleaned=False)
    return sessions


def scan_file(path: str) -> list[Session]:
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        return parse_lines(handle)


def collect_paths(arguments: list[str]) -> list[str]:
    if not arguments:
        arguments = [os.path.join(os.environ.get("APPDATA", ""), "EyeCtrl", "logs")]
    paths: list[str] = []
    for argument in arguments:
        if os.path.isdir(argument):
            paths.extend(sorted(glob.glob(os.path.join(argument, "eyectrl-*.log"))))
        else:
            paths.extend(sorted(glob.glob(argument)) or [argument])
    return paths


def run(paths: list[str]) -> int:
    flagged = 0
    for path in paths:
        if not os.path.isfile(path):
            print(f"skip (not a file): {path}")
            continue
        for session in scan_file(path):
            label = "FLAGGED" if session.flagged else "ok     "
            if session.flagged:
                flagged += 1
            print(f"{label} {os.path.basename(path)}: {session.describe()}")
    print(f"\n{flagged} session(s) flagged (paused > {PAUSE_FLAG_SECONDS}s with an unresolved "
          f"paused summon, or still paused > {STRANDED_FLAG_SECONDS}s at a shutdown — or, for "
          "eye-tracker sessions, at an unclean end).")
    return 1 if flagged else 0


def selftest() -> None:
    """Synthetic sessions: the 09-24 shape must flag; a healthy eye session and a short pause must not."""

    def log(*lines: str) -> list[str]:
        return list(lines)

    def line(stamp: str, message: str) -> str:
        return f"{stamp} [INF] [Test] {message}"

    # 1. 09-24 shape: starts paused, 10 summons never followed by a resume, 1h43m, clean
    # shutdown. The summon worked and the user never got out — rule 1's shape (also caught by the
    # time-based rule).
    stranded = log(
        line("2026-09-24 01:46:24.022", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-24 01:46:24.491", 'State \u2192 "Paused"'),
        line("2026-09-24 01:47:38.139", "Toolbar summoned: slide-in animating"),
        line("2026-09-24 02:53:38.820", "Toolbar summoned: slide-in animating"),
        line("2026-09-24 03:28:16.355", "Toolbar summoned: slide-in animating"),
        line("2026-09-24 03:29:31.068", "Shutdown"),
    )
    # 2. Healthy eye session: toolbar summons while unpaused, never paused.
    healthy = log(
        line("2026-09-22 10:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-22 10:01:00.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-22 11:30:00.000", "Shutdown"),
    )
    # 3. A pause that resolved itself inside the same minute.
    brief = log(
        line("2026-09-20 09:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-20 09:00:10.000", 'State \u2192 "Paused"'),
        line("2026-09-20 09:00:30.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-20 09:00:45.000", 'State \u2192 "FreeLook"'),
        line("2026-09-20 09:10:00.000", "Shutdown"),
    )
    # 4. Long pause, no summons, never resumed, clean shutdown: the new time-based rule flags it
    # even though the band no longer hides while paused and no summon is ever logged.
    quiet_pause = log(
        line("2026-09-21 09:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-21 09:00:01.000", 'State \u2192 "Paused"'),
        line("2026-09-21 11:00:00.000", "Shutdown"),
    )
    # 5. A restart inside one rolling file: two sessions, only the second is stranded.
    restart = log(
        line("2026-09-19 08:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-19 08:05:00.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-19 09:00:00.000", "Shutdown"),
        line("2026-09-19 09:00:05.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-19 09:00:06.000", 'State \u2192 "Paused"'),
        line("2026-09-19 09:00:07.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-19 10:00:00.000", "Shutdown"),
    )
    # 6. A long pause that the user DID resolve: 10 min paused, then a resume, then shutdown.
    # Not a stranding — this is what "no resume" excludes, and it is the 2026-09-16 69-min pause.
    resolved_pause = log(
        line("2026-09-18 09:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-18 09:00:01.000", 'State \u2192 "Paused"'),
        line("2026-09-18 09:10:00.000", 'State \u2192 "FreeLook"'),
        line("2026-09-18 10:00:00.000", "Shutdown"),
    )
    # 7. A long pause ended by a restart (no Shutdown line) rather than a clean exit, on a
    # MOUSE-provider dev run (the 2026-09-16 shape). Not flagged: mouse sessions are dev noise.
    restart_while_paused = log(
        line("2026-09-17 09:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"Mouse\""),
        line("2026-09-17 09:00:01.000", 'State \u2192 "Paused"'),
        line("2026-09-17 11:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"Mouse\""),
        line("2026-09-17 11:00:01.000", 'State \u2192 "FreeLook"'),
        line("2026-09-17 12:00:00.000", "Shutdown"),
    )
    # 8. An EYE session killed or crashing while paused (no Shutdown line). This is a real
    # stranding — the user's session never resumed and there is no clean exit to explain it.
    eye_killed_while_paused = log(
        line("2026-09-16 09:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-16 09:00:01.000", 'State \u2192 "Paused"'),
        line("2026-09-16 11:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-16 11:00:01.000", 'State \u2192 "FreeLook"'),
        line("2026-09-16 12:00:00.000", "Shutdown"),
    )
    # 9. An eye session still paused at EOF (log ends without Shutdown or restart).
    eye_paused_at_eof = log(
        line("2026-09-15 09:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-15 09:00:01.000", 'State \u2192 "Paused"'),
        line("2026-09-15 09:30:00.000", "Toolbar summoned: slide-in animating"),
    )
    # 10. A SUCCESSFUL paused return (the independent review's synthetic session, F5): pause, hide,
    # summon, resume, clean shutdown. Since the 2026-09-26 reversal this summon → dwell Play →
    # resume path is the normal way back — the pause resolved, so it is not a stranding even though
    # the old session-total rule flagged it.
    paused_return_success = log(
        line("2026-09-25 10:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-25 10:00:05.000", 'State \u2192 "Paused"'),
        line("2026-09-25 10:01:30.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-25 10:01:32.000", 'State \u2192 "FreeLook"'),
        line("2026-09-25 10:02:00.000", "Shutdown"),
    )
    # 11. The stranding rule still catches a paused summon whose pause never ends, even when the
    # tail is shorter than the 300 s the time-based rule needs — only rule 1 can flag this one.
    short_stranding = log(
        line("2026-09-26 10:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=\"StreamEngine\""),
        line("2026-09-26 10:00:01.000", 'State \u2192 "Paused"'),
        line("2026-09-26 10:01:25.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-26 10:01:30.000", "Shutdown"),
    )

    cases = [
        ("09-24 shape", stranded, [True]),
        ("healthy session", healthy, [False]),
        ("brief pause", brief, [False]),
        ("quiet long pause, clean shutdown", quiet_pause, [True]),
        ("restart: only the stranded run flags", restart, [False, True]),
        ("long pause that resolved", resolved_pause, [False]),
        ("mouse restart while paused (not a clean end)", restart_while_paused, [False, False]),
        ("eye session killed while paused", eye_killed_while_paused, [True, False]),
        ("eye session still paused at EOF", eye_paused_at_eof, [True]),
        ("paused return that resolved (summon + resume)", paused_return_success, [False]),
        ("short stranding that only rule 1 catches", short_stranding, [True]),
    ]

    failed = False
    for name, lines, expected in cases:
        sessions = parse_lines(lines)
        actual = [session.flagged for session in sessions]
        status = "PASS" if actual == expected else "FAIL"
        if actual != expected:
            failed = True
        print(f"{status}  {name}: flagged={actual} expected={expected}")
        if name == "09-24 shape":
            paused = sessions[0].paused_seconds
            print(f"      stranded paused_seconds={paused:.0f} (summons={sessions[0].summons})")
            if not (6000 < paused < 6400):
                failed = True
                print("      FAIL: expected the 09-24 shape to measure about 6187 s paused")

    if failed:
        sys.exit(1)
    print("\nselftest passed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("paths", nargs="*", help="log files or directories (default: %%APPDATA%%/EyeCtrl/logs)")
    parser.add_argument("--check", action="store_true", help="exit 1 when any session flags")
    parser.add_argument("--selftest", action="store_true", help="run synthetic checks instead of scanning files")
    args = parser.parse_args()

    if args.selftest:
        selftest()
        return 0

    paths = collect_paths(args.paths)
    if not paths:
        print("no log files found")
        return 0

    result = run(paths)
    return result if args.check else 0


if __name__ == "__main__":
    sys.exit(main())
