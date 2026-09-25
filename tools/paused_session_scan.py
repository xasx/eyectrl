#!/usr/bin/env python3
"""Flag EyeCtrl sessions stranded in Paused (paused-trap guard, ticket 1).

Reads EyeCtrl Serilog files and reports every session that spent more than 60 seconds in the
Paused interaction state *and* showed toolbar summons — i.e. a user who was looking for a way
out and never found it. The 2026-09-24 session (1 h 43 min paused, 10 summons, never resumed)
is the shape this exists to catch.

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

_TIMESTAMP = r"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})"
_LOG_LINE = re.compile(r"^" + _TIMESTAMP + r" \[\w+\] \[[^\]]*\] (.*)$")
_STATE = re.compile(r'^State \u2192 "([A-Za-z]+)"$')
_STARTUP_PREFIX = "Startup: EyeCtrl"
_SHUTDOWN = "Shutdown"
_SUMMON_PREFIX = "Toolbar summoned:"


@dataclasses.dataclass
class Session:
    """One app run: from a Startup line to Shutdown / the next Startup / EOF."""

    started: _dt.datetime
    ended: _dt.datetime | None = None
    paused_seconds: float = 0.0
    summons: int = 0
    saw_startup: bool = True

    @property
    def flagged(self) -> bool:
        return self.paused_seconds > PAUSE_FLAG_SECONDS and self.summons >= 1

    def describe(self) -> str:
        end = self.ended.strftime("%Y-%m-%d %H:%M:%S") if self.ended else "(no shutdown line)"
        return (
            f"{self.started:%Y-%m-%d %H:%M:%S} -> {end}: "
            f"paused {self.paused_seconds:.0f}s, {self.summons} toolbar summon(s)"
        )


def _parse_timestamp(text: str) -> _dt.datetime:
    return _dt.datetime.strptime(text, "%Y-%m-%d %H:%M:%S.%f")


def parse_lines(lines) -> list[Session]:
    """Group a log's lines into sessions and measure each session's paused time."""
    sessions: list[Session] = []
    current: Session | None = None
    paused_since: _dt.datetime | None = None
    last_timestamp: _dt.datetime | None = None

    def close(at: _dt.datetime | None) -> None:
        nonlocal current, paused_since
        if current is None:
            return
        if paused_since is not None:
            current.paused_seconds += ((at or paused_since) - paused_since).total_seconds()
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
            close(timestamp)
            current = Session(started=timestamp)
            continue

        if current is None:
            # Log lines from before this file's first Startup line (rolling files are appended to
            # across runs; the head can be a previous process's tail). Not a session to judge.
            continue

        if message == _SHUTDOWN:
            close(timestamp)
            continue

        if message.startswith(_SUMMON_PREFIX):
            current.summons += 1
            continue

        state = _STATE.match(message)
        if state:
            if state.group(1) == "Paused":
                if paused_since is None:
                    paused_since = timestamp
            elif paused_since is not None:
                current.paused_seconds += (timestamp - paused_since).total_seconds()
                paused_since = None

    close(last_timestamp)
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
    print(f"\n{flagged} session(s) flagged (paused > {PAUSE_FLAG_SECONDS}s with toolbar summons).")
    return 1 if flagged else 0


def selftest() -> None:
    """Synthetic sessions: the 09-24 shape must flag; a healthy eye session and a short pause must not."""

    def log(*lines: str) -> list[str]:
        return list(lines)

    def line(stamp: str, message: str) -> str:
        return f"{stamp} [INF] [Test] {message}"

    # 1. 09-24 shape: starts paused, 10 summons, never resumes, 1h43m.
    stranded = log(
        line("2026-09-24 01:46:24.022", "Startup: EyeCtrl v1.0.0.0, provider=StreamEngine"),
        line("2026-09-24 01:46:24.491", 'State \u2192 "Paused"'),
        line("2026-09-24 01:47:38.139", "Toolbar summoned: slide-in animating"),
        line("2026-09-24 02:53:38.820", "Toolbar summoned: slide-in animating"),
        line("2026-09-24 03:28:16.355", "Toolbar summoned: slide-in animating"),
        line("2026-09-24 03:29:31.068", "Shutdown"),
    )
    # 2. Healthy eye session: toolbar summons while unpaused, never paused.
    healthy = log(
        line("2026-09-22 10:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=StreamEngine"),
        line("2026-09-22 10:01:00.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-22 11:30:00.000", "Shutdown"),
    )
    # 3. A pause that resolved itself inside the same minute.
    brief = log(
        line("2026-09-20 09:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=StreamEngine"),
        line("2026-09-20 09:00:10.000", 'State \u2192 "Paused"'),
        line("2026-09-20 09:00:30.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-20 09:00:45.000", 'State \u2192 "FreeLook"'),
        line("2026-09-20 09:10:00.000", "Shutdown"),
    )
    # 4. Long pause, no summons: the toolbar was never reached for — nothing to flag.
    quiet_pause = log(
        line("2026-09-21 09:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=StreamEngine"),
        line("2026-09-21 09:00:01.000", 'State \u2192 "Paused"'),
        line("2026-09-21 11:00:00.000", "Shutdown"),
    )
    # 5. A restart inside one rolling file: two sessions, only the second is stranded.
    restart = log(
        line("2026-09-19 08:00:00.000", "Startup: EyeCtrl v1.0.0.0, provider=StreamEngine"),
        line("2026-09-19 08:05:00.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-19 09:00:00.000", "Shutdown"),
        line("2026-09-19 09:00:05.000", "Startup: EyeCtrl v1.0.0.0, provider=StreamEngine"),
        line("2026-09-19 09:00:06.000", 'State \u2192 "Paused"'),
        line("2026-09-19 09:00:07.000", "Toolbar summoned: slide-in animating"),
        line("2026-09-19 10:00:00.000", "Shutdown"),
    )

    cases = [
        ("09-24 shape", stranded, [True]),
        ("healthy session", healthy, [False]),
        ("brief pause", brief, [False]),
        ("quiet long pause", quiet_pause, [False]),
        ("restart: only the stranded run flags", restart, [False, True]),
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
