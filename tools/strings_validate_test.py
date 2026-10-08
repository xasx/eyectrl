#!/usr/bin/env python3
"""Fixture-driven self-test for tools/strings_validate.py.

    python tools/strings_validate_test.py

Stdlib only. The validator runs in CI against the shipped tables, which ship empty during
Step 0 — so without this suite every gate could silently stop firing and CI would stay green
until the first German surface. Each case under tests/fixtures/strings/<case>/ is a complete
little repo (strings.json, strings.de.json, strings.de.manifest.json, a src/ folder, and
optionally a case-local glossary), and each asserts the validator's exit code and the expected
error substring.
"""

from __future__ import annotations

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
VALIDATOR = Path(__file__).resolve().parent / "strings_validate.py"
FIXTURES = REPO_ROOT / "tests" / "fixtures" / "strings"
DEFAULT_GLOSSARY = REPO_ROOT / "localization" / "glossary.de.json"
DEFAULT_ALLOWLIST = REPO_ROOT / "localization" / "strings.identical-allowlist.json"

# case -> (expected exit code, expected substrings in the output)
CASES: dict[str, tuple[int, list[str]]] = {
    "green": (0, []),
    "missing-key": (1, ["missing key"]),
    "missing-crisis-key": (1, ["CRISIS KEY"]),
    "stale-src": (1, ["stale src"]),
    "unsorted-keys": (1, ["not sorted"]),
    "prefix-mismatch": (1, ["does not equal where"]),
    "mirrors-mismatch": (1, ["fit differs"]),
    "unused-key": (1, ["never referenced"]),
    "identical-flagged": (1, ["identical to English"]),
    "identical-allowlisted": (0, []),
    "placeholder-dropped": (1, ["placeholder parity"]),
    "placeholder-index": (1, ["placeholder parity"]),
    "placeholder-stray-brace": (1, ["unbalanced"]),
    "english-stray-brace": (1, ["unbalanced"]),
    "english-placeholder-parity": (1, ["placeholder parity"]),
    "pronoun-negatives": (0, []),
    "informal-address": (1, ["informal address"]),
    "forbidden-term": (1, ["forbidden glossary term"]),
    "unknown-meta": (1, ["unknown meta field"]),
    "missing-meta": (1, ["missing required meta"]),
    "bad-enums": (1, ["is not one of", "is not a fit class", "is not a surface id"]),
    "unknown-plural-form": (1, ["neither a string nor a complete"]),
    "plural-shape": (1, ["plural shape differs"]),
    "orphan-key": (1, ["does not exist in strings.json"]),
    "manifest-unknown-surface": (1, ["not a known surface id"]),
    "src-format": (1, ["src must be 8"]),
    "format-specifier-plural": (0, []),
    "format-specifier-at-index-one": (0, []),
    "format-specifier-colon-escape": (0, []),
    "alignment-padding": (1, ["alignment", "pads with spaces", "plural probe"]),
    "alignment-negative": (1, ["alignment"]),
    "empty-format": (1, ["empty format"]),
    "unclosed-format": (1, ["unbalanced"]),
    "specifier-parity-match": (0, []),
    "specifier-parity-differs": (1, ["format specifier for placeholder 0 differs",
                                     "German '{0:0.00}'", "English '{0:0.0}'"]),
    "specifier-parity-missing": (1, ["format specifier for placeholder 0 differs",
                                     "German '{0}'"]),
}


def validator_command(english: Path, german: Path, manifest: Path, src_dir: Path,
                      glossary: Path, allowlist: Path, *extra: str) -> list[str]:
    return [
        sys.executable, str(VALIDATOR),
        "--english", str(english),
        "--german", str(german),
        "--manifest", str(manifest),
        "--glossary", str(glossary),
        "--allowlist", str(allowlist),
        "--src-dir", str(src_dir),
        *extra,
    ]


def run_case(case: str, *extra: str) -> subprocess.CompletedProcess[str]:
    directory = FIXTURES / case
    glossary = directory / "glossary.de.json"
    return subprocess.run(
        validator_command(
            directory / "strings.json",
            directory / "strings.de.json",
            directory / "strings.de.manifest.json",
            directory / "src",
            glossary if glossary.exists() else DEFAULT_GLOSSARY,
            DEFAULT_ALLOWLIST,
            *extra,
        ),
        capture_output=True,
        text=True,
    )


class StringsValidateFixtureTests(unittest.TestCase):
    maxDiff = None

    def test_every_fixture_case_is_present(self) -> None:
        # Guards the guard: a deleted fixture would otherwise silently stop testing its gate.
        present = {p.name for p in FIXTURES.iterdir() if p.is_dir()} if FIXTURES.is_dir() else set()
        expected = set(CASES) | {"refresh-orphan", "refresh-non-object"}
        self.assertEqual([], sorted(expected - present), "fixture case directory is missing")

    def test_every_case_matches_its_expected_exit_code_and_errors(self) -> None:
        for case, (expected_code, substrings) in CASES.items():
            with self.subTest(case=case):
                result = run_case(case)
                output = result.stdout + result.stderr
                self.assertEqual(expected_code, result.returncode, output)
                for substring in substrings:
                    self.assertIn(substring, output)

    def test_refresh_de_src_carries_an_unknown_key_through(self) -> None:
        # F5: a German entry whose key is no longer in English is a finished translation of a
        # key that may come back; refresh must never drop it.
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "strings.de.json"
            shutil.copy(FIXTURES / "refresh-orphan" / "strings.de.json", target)

            result = subprocess.run(
                validator_command(
                    FIXTURES / "refresh-orphan" / "strings.json",
                    target,
                    FIXTURES / "refresh-orphan" / "strings.de.manifest.json",
                    FIXTURES / "refresh-orphan" / "src",
                    DEFAULT_GLOSSARY,
                    DEFAULT_ALLOWLIST,
                    "--refresh-de-src",
                ),
                capture_output=True,
                text=True,
            )

            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            refreshed = json.loads(target.read_text(encoding="utf-8"))
            self.assertEqual({"text": "Weg", "src": "00000000"}, refreshed["settings.gone"])
            self.assertNotEqual("00000000", refreshed["settings.sample"]["src"])

    def test_refresh_de_src_writes_nothing_on_a_malformed_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "strings.de.json"
            target.write_text("{ not json", encoding="utf-8")
            before = target.read_bytes()

            result = subprocess.run(
                validator_command(
                    FIXTURES / "green" / "strings.json",
                    target,
                    FIXTURES / "green" / "strings.de.manifest.json",
                    FIXTURES / "green" / "src",
                    DEFAULT_GLOSSARY,
                    DEFAULT_ALLOWLIST,
                    "--refresh-de-src",
                ),
                capture_output=True,
                text=True,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertEqual(before, target.read_bytes())

    def test_refresh_de_src_writes_nothing_on_a_non_object_entry(self) -> None:
        # Review 02 G2: a non-object entry is an error, and an error must leave the file alone.
        with tempfile.TemporaryDirectory() as tmp:
            target = Path(tmp) / "strings.de.json"
            shutil.copy(FIXTURES / "refresh-non-object" / "strings.de.json", target)
            before = target.read_bytes()

            result = subprocess.run(
                validator_command(
                    FIXTURES / "refresh-non-object" / "strings.json",
                    target,
                    FIXTURES / "refresh-non-object" / "strings.de.manifest.json",
                    FIXTURES / "refresh-non-object" / "src",
                    DEFAULT_GLOSSARY,
                    DEFAULT_ALLOWLIST,
                    "--refresh-de-src",
                ),
                capture_output=True,
                text=True,
            )

            self.assertNotEqual(0, result.returncode)
            self.assertIn("entry is not an object", result.stdout + result.stderr)
            self.assertEqual(before, target.read_bytes())

    def test_strip_markup_removes_a_format_specifier_whole(self) -> None:
        # Direct, because this guards the word scans: "{0:0.0}" must vanish whole, leaving no
        # "0.0" for the glossary or pronoun checks to see.
        spec = importlib.util.spec_from_file_location("strings_validate", VALIDATOR)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)

        stripped = module.strip_markup("{0:0.0} unit")
        self.assertNotIn("0.0", stripped)
        self.assertIn("unit", stripped)

    def test_refresh_de_src_makes_a_stale_file_validate(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp) / "stale-src"
            shutil.copytree(FIXTURES / "stale-src", directory)

            refresh = subprocess.run(
                validator_command(
                    directory / "strings.json",
                    directory / "strings.de.json",
                    directory / "strings.de.manifest.json",
                    directory / "src",
                    DEFAULT_GLOSSARY,
                    DEFAULT_ALLOWLIST,
                    "--refresh-de-src",
                ),
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, refresh.returncode, refresh.stdout + refresh.stderr)

            validation = subprocess.run(
                validator_command(
                    directory / "strings.json",
                    directory / "strings.de.json",
                    directory / "strings.de.manifest.json",
                    directory / "src",
                    DEFAULT_GLOSSARY,
                    DEFAULT_ALLOWLIST,
                ),
                capture_output=True,
                text=True,
            )
            self.assertEqual(0, validation.returncode, validation.stdout + validation.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
