#!/usr/bin/env python3
"""Validates EyeCtrl's localization string tables.

    python tools/strings_validate.py

Stdlib only, by design (the palette_validate.py precedent): CI runs it before the
build so a bad table fails in a second rather than behind a solution build.

Layout (paths are overridable for fixture-based mutation checks):

    src/EyeCtrl.Core/Localization/strings.json          English, embedded in Core
    src/EyeCtrl.App/Localization/strings.de.json        German, loose beside the App exe
    src/EyeCtrl.App/Localization/strings.de.manifest.json
    localization/glossary.de.json                        in repo, never shipped
    localization/strings.identical-allowlist.json        in repo, never shipped
    src/                                                 scanned for literal key uses

Gates
-----
English table shape:
  * required meta (text, who, where, fit, args) and closed enums (who, where, fit)
  * text is a string or both plural forms {one, other}
  * placeholder parity: the `{n}` indices in every form are exactly 0..len(args)-1
  * no unbalanced single `{` or `}` (doubled braces are literal)
  * keys sorted; every key's prefix equals its `where` surface id
  * `mirrors: <key>` names an existing key with the same `fit` and `args`

German table:
  * keys must exist in the English table; entry shape is {text, src}
  * German placeholder parity: every form's `{n}` set equals the English form's
  * no unbalanced single `{` or `}`
  * forbidden terms from the glossary's avoid[] (stem prefix, German values only,
    after stripping placeholders and U+00AD)
  * no informal address: whole words du/dich/dir/dein/deine/deinen/deinem/deiner/deines
  * German identical to English fails unless the value is on the allowlist

Staleness and coverage:
  * unused keys (a text scan of src/ for the literal key)
  * stale `src` on any manifest surface; a manifest surface missing any key fails,
    and a missing crisis key is named loudly

`src` canonical bytes (the ONE canonicalizer; C# deliberately has none):
  * canonical_text = the English `text` as a plain string, or for a plural entry
    json.dumps({"one": ..., "other": ...}, ensure_ascii=False, sort_keys=True,
    separators=(",", ":")) using exactly the two declared forms
  * src = sha256(utf8(canonical_text + "\x1f" + fit)).hexdigest()[:8]
  * Only this script writes src, and only in --refresh-de-src mode; that mode writes
    nothing on any error and carries unknown-key entries through unchanged.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

SURFACES = [
    "supervisor",
    "app-tray",
    "caregiver-alert",
    "drift-notice",
    "startup-error",
    "track-status",
    "language-recovery",
    "settings",
    "calibration",
    "eye-care",
    "overlays",
]

WHO_VALUES = ["caretaker", "user", "both"]

# A68 stays for the Shift/Ctrl/Alt modifier bar alone (D8); A-modes is dropped.
FIT_CLASSES = [
    "A105",
    "A68",
    "A44",
    "A56",
    "tab156",
    "tray63",
    "B94x2",
    "B114",
    "B260x2",
    "B260x3",
    "C-fallback",
    "C-alert",
    "row",
    "prose",
]

REQUIRED_ENGLISH_META = {"text", "who", "where", "fit", "args"}
OPTIONAL_ENGLISH_META = {"terms", "crisis", "mirrors"}

# Whole words only. "du" must not reject "durch"/"Dutzend", "dir" must not reject
# "Direktmodus", "dich" must not reject "Dichte" — the stem-prefix rule below is for the
# glossary's avoid[] stems alone.
PRONOUN_WORDS = {
    "du",
    "dich",
    "dir",
    "dein",
    "deine",
    "deinen",
    "deinem",
    "deiner",
    "deines",
}
SOFT_HYPHEN = "\u00ad"

PLACEHOLDER = re.compile(r"\{(\d+)\}")


def strip_markup(text: str) -> str:
    """Removes placeholders (both doubled escapes and {n}) and U+00AD."""
    text = text.replace("{{", "").replace("}}", "")
    text = PLACEHOLDER.sub(" ", text)
    return text.replace(SOFT_HYPHEN, "")


def words(text: str) -> list[str]:
    """Splits on anything that is not a letter or digit; umlauts count as letters."""
    return [word for word in re.split(r"[^\w]+", text.casefold()) if word]


def word_starts_with(text: str, stem: str) -> bool:
    """True when any word in text starts with stem, case-insensitively.

    The glossary's avoid[] entries are stems, not whole words: "Verweildauer"
    must also catch "Verweildauern".
    """
    stem_folded = stem.casefold()
    return any(word.startswith(stem_folded) for word in words(text))


def contains_pronoun(text: str) -> str | None:
    """The first informal-address word in text, or None. Whole words only."""
    for word in words(text):
        if word in PRONOUN_WORDS:
            return word
    return None


def unbalanced_brace(form: str) -> str | None:
    """Describes the first unbalanced `{` or `}`, or None when every brace is well formed.

    Doubled `{{`/`}}` are literals; `{n}` is a placeholder; anything else is an error.
    Kept as a scanner (not a regex) so a stray brace cannot hide behind a valid one.
    """
    index = 0
    while index < len(form):
        char = form[index]
        if char == "{" and index + 1 < len(form) and form[index + 1] == "{":
            index += 2
            continue
        if char == "}" and index + 1 < len(form) and form[index + 1] == "}":
            index += 2
            continue
        if char == "{":
            match = PLACEHOLDER.match(form, index)
            if match:
                index = match.end()
                continue
            return f"unbalanced '{{' at index {index}"
        if char == "}":
            return f"unbalanced '}}' at index {index}"
        index += 1
    return None


def read_json(path: Path, errors: list[str]) -> object:
    try:
        with path.open(encoding="utf-8") as handle:
            return json.load(handle)
    except FileNotFoundError:
        errors.append(f"{path}: file not found")
    except json.JSONDecodeError as exc:
        errors.append(f"{path}: not valid JSON ({exc})")
    except OSError as exc:
        errors.append(f"{path}: cannot be read ({exc})")
    return {}


def canonical_text(text: object, errors: list[str], where: str) -> str:
    """The canonical form of an English `text` value; see the module docstring."""
    if isinstance(text, str):
        return text
    if isinstance(text, dict) and set(text) == {"one", "other"}:
        return json.dumps(text, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    errors.append(f"{where}: text is neither a string nor {{one, other}}")
    return ""


def text_forms(text: object) -> list[str]:
    """The forms of a valid text value: one string, or exactly {one, other}."""
    if isinstance(text, str):
        return [text]
    if (
        isinstance(text, dict)
        and set(text) == {"one", "other"}
        and isinstance(text["one"], str)
        and isinstance(text["other"], str)
    ):
        return [text["one"], text["other"]]
    return []


def placeholders(form: str) -> list[int]:
    """The {n} indices in a template, ignoring doubled escapes."""
    index = 0
    found = []
    while index < len(form):
        char = form[index]
        if char == "{" and index + 1 < len(form) and form[index + 1] == "{":
            index += 2
            continue
        if char == "}" and index + 1 < len(form) and form[index + 1] == "}":
            index += 2
            continue
        if char == "{":
            match = PLACEHOLDER.match(form, index)
            if match:
                found.append(int(match.group(1)))
                index = match.end()
                continue
        index += 1
    return found


def validate_english(english: dict, errors: list[str]) -> None:
    keys = list(english)
    if keys != sorted(keys):
        errors.append("strings.json: keys are not sorted (sorted() order is the enforced merge-friendly order)")

    for key, entry in english.items():
        if not isinstance(key, str) or "." not in key:
            errors.append(f"{key}: key must contain a surface prefix and a dot")
            continue
        if not isinstance(entry, dict):
            errors.append(f"{key}: entry is not an object")
            continue

        unknown = set(entry) - REQUIRED_ENGLISH_META - OPTIONAL_ENGLISH_META
        if unknown:
            errors.append(f"{key}: unknown meta field(s): {', '.join(sorted(unknown))}")
        missing = REQUIRED_ENGLISH_META - set(entry)
        if missing:
            errors.append(f"{key}: missing required meta field(s): {', '.join(sorted(missing))}")
            continue

        where = entry["where"]
        if where not in SURFACES:
            errors.append(f"{key}: where={where!r} is not a surface id")
        else:
            prefix = key.split(".", 1)[0]
            if prefix != where:
                errors.append(f"{key}: key prefix {prefix!r} does not equal where={where!r}")

        if entry["who"] not in WHO_VALUES:
            errors.append(f"{key}: who={entry['who']!r} is not one of {WHO_VALUES}")

        if entry["fit"] not in FIT_CLASSES:
            errors.append(f"{key}: fit={entry['fit']!r} is not a fit class")

        args = entry["args"]
        if not isinstance(args, list) or not all(isinstance(a, str) and a for a in args):
            errors.append(f"{key}: args must be a list of non-empty strings")
            args = []

        forms = text_forms(entry["text"])
        if not forms:
            errors.append(f"{key}: text is neither a string nor a complete {{one, other}} pair")
            continue

        declared = set(range(len(args)))
        for form in forms:
            found = set(placeholders(form))
            if found != declared:
                errors.append(
                    f"{key}: placeholder parity broken in {form!r}: found "
                    f"{sorted(found)}, args declares {sorted(declared)}"
                )
            brace = unbalanced_brace(form)
            if brace:
                errors.append(f"{key}: {brace} in {form!r}")

        optional = entry.get("terms")
        if optional is not None and (
            not isinstance(optional, list) or not all(isinstance(t, str) for t in optional)
        ):
            errors.append(f"{key}: terms must be a list of strings")

        if "crisis" in entry and not isinstance(entry["crisis"], bool):
            errors.append(f"{key}: crisis must be a boolean")

        if "mirrors" in entry:
            target = entry["mirrors"]
            if not isinstance(target, str) or target not in english:
                errors.append(f"{key}: mirrors={target!r} does not name an existing key")
            else:
                other = english[target]
                if not isinstance(other, dict):
                    continue
                if other.get("fit") != entry["fit"]:
                    errors.append(
                        f"{key}: mirrors {target} but fit differs "
                        f"({entry['fit']!r} vs {other.get('fit')!r})"
                    )
                if other.get("args") != entry["args"]:
                    errors.append(f"{key}: mirrors {target} but args differ")


def validate_manifest(manifest: object, errors: list[str]) -> set[str]:
    if not isinstance(manifest, list):
        errors.append("strings.de.manifest.json: root is not a JSON array of surface ids")
        return set()

    surfaces: list[str] = []
    for item in manifest:
        if not isinstance(item, str):
            errors.append(f"strings.de.manifest.json: {item!r} is not a surface id")
            continue
        if item not in SURFACES:
            errors.append(f"strings.de.manifest.json: {item!r} is not a known surface id")
            continue
        surfaces.append(item)

    if surfaces != sorted(surfaces):
        errors.append("strings.de.manifest.json: surface ids are not sorted")
    if len(surfaces) != len(set(surfaces)):
        errors.append("strings.de.manifest.json: duplicate surface ids")
    return set(surfaces)


def validate_german(
    english: dict,
    german: dict,
    manifest: set[str],
    glossary: dict,
    allowlist: set[str],
    errors: list[str],
) -> None:
    keys = list(german)
    if keys != sorted(keys):
        errors.append("strings.de.json: keys are not sorted")
    if set(keys) - set(english):
        for orphan in sorted(set(keys) - set(english)):
            errors.append(f"strings.de.json: key {orphan!r} does not exist in strings.json")

    avoid_terms: list[str] = []
    for entry in glossary.get("entries", []) if isinstance(glossary, dict) else []:
        if isinstance(entry, dict):
            avoid_terms.extend(t for t in entry.get("avoid", []) if isinstance(t, str))

    for key, entry in german.items():
        if not isinstance(entry, dict):
            errors.append(f"strings.de.json: {key}: entry is not an object")
            continue
        unknown = set(entry) - {"text", "src"}
        if unknown:
            errors.append(f"strings.de.json: {key}: unknown field(s): {', '.join(sorted(unknown))}")
        if "text" not in entry:
            errors.append(f"strings.de.json: {key}: missing text")
            continue

        src = entry.get("src")
        if not isinstance(src, str) or not re.fullmatch(r"[0-9a-f]{8}", src):
            errors.append(f"strings.de.json: {key}: src must be 8 lowercase hex chars")

        forms = text_forms(entry["text"])
        if not forms:
            errors.append(f"strings.de.json: {key}: text is neither a string nor a complete {{one, other}} pair")
            continue

        if key not in english or not isinstance(english[key], dict):
            continue
        source = english[key]
        source_forms = text_forms(source.get("text"))

        if isinstance(source.get("text"), dict) != isinstance(entry["text"], dict):
            errors.append(
                f"strings.de.json: {key}: plural shape differs from English "
                "(a German draft must keep the same {one, other} shape)"
            )

        for form in forms:
            brace = unbalanced_brace(form)
            if brace:
                errors.append(f"strings.de.json: {key}: {brace} in {form!r}")

            plain = strip_markup(form)
            pronoun = contains_pronoun(plain)
            if pronoun:
                errors.append(f"strings.de.json: {key}: informal address {pronoun!r} in {form!r}")
            for term in avoid_terms:
                if word_starts_with(plain, term):
                    errors.append(f"strings.de.json: {key}: forbidden glossary term {term!r} in {form!r}")

        if len(forms) == len(source_forms):
            for form, source_form in zip(forms, source_forms):
                german_placeholders = set(placeholders(form))
                source_placeholders = set(placeholders(source_form))
                if german_placeholders != source_placeholders:
                    errors.append(
                        f"strings.de.json: {key}: placeholder parity broken in {form!r}: found "
                        f"{sorted(german_placeholders)}, English has {sorted(source_placeholders)}"
                    )
                if form == source_form and form not in allowlist:
                    errors.append(
                        f"strings.de.json: {key}: German is identical to English ({form!r}); "
                        "add the value to strings.identical-allowlist.json if that is intended"
                    )

        if source.get("where") in manifest:
            if not isinstance(src, str) or src != compute_src(source):
                marker = "CRISIS KEY: " if source.get("crisis") is True else ""
                errors.append(
                    f"strings.de.json: {key}: {marker}stale src {src!r} for manifest surface "
                    f"{source.get('where')!r} (expected {compute_src(source)!r}); run "
                    "--refresh-de-src after the English text changed"
                )

    for key, source in english.items():
        if not isinstance(source, dict) or source.get("where") not in manifest:
            continue
        if key not in german or not isinstance(german.get(key), dict) or "text" not in german[key]:
            marker = "CRISIS KEY: " if source.get("crisis") is True else ""
            errors.append(
                f"strings.de.json: {marker}manifest surface {source.get('where')!r} is missing "
                f"key {key!r}; a surface only enters the manifest complete"
            )


def compute_src(entry: dict) -> str:
    canonical = canonical_text(entry.get("text"), [], "<src>")
    payload = canonical + "\x1f" + str(entry.get("fit", ""))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()[:8]


def validate_unused_keys(english: dict, src_dir: Path, errors: list[str]) -> None:
    if not src_dir.is_dir():
        errors.append(f"{src_dir}: source directory not found; the unused-key scan cannot run")
        return

    corpus_parts: list[str] = []
    for path in src_dir.rglob("*"):
        if not path.is_file():
            continue
        if path.suffix not in (".cs", ".xaml"):
            continue
        if any(part in ("obj", "bin") for part in path.parts):
            continue
        try:
            corpus_parts.append(path.read_text(encoding="utf-8", errors="replace"))
        except OSError:
            continue
    corpus = "\n".join(corpus_parts)

    for key in english:
        if f'"{key}"' not in corpus and f"'{key}'" not in corpus:
            errors.append(
                f"strings.json: key {key!r} is never referenced as a literal in src/; "
                "either wire it up or delete it"
            )


def refresh_de_src(german_path: Path, english: dict, errors: list[str]) -> None:
    """Recomputes src in place. Writes nothing on any error, and never drops an entry.

    An entry whose key no longer exists in English is carried through unchanged (with a
    notice): it may be a finished translation of a key that is coming back, and silently
    deleting it is how data is lost. A malformed file or a non-object entry leaves the file
    untouched.
    """
    german = read_json(german_path, errors)
    if errors:
        return  # unreadable or unparseable: write nothing
    if not isinstance(german, dict):
        errors.append("strings.de.json: root is not an object; nothing to refresh")
        return

    refreshed: dict[str, dict] = {}
    for key, entry in german.items():
        if not isinstance(entry, dict):
            errors.append(f"strings.de.json: {key}: entry is not an object; nothing was written")
            continue
        if key not in english:
            print(f"strings_validate: {key!r} is not in strings.json; carried through unchanged")
            refreshed[key] = entry
            continue
        updated = dict(entry)
        updated["src"] = compute_src(english[key])
        refreshed[key] = updated

    if errors:
        return  # any error: the file is left exactly as it was

    german_path.write_text(
        json.dumps(refreshed, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"strings_validate: refreshed src for {len(refreshed)} key(s) in {german_path}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Validate EyeCtrl's string tables.")
    parser.add_argument("--english", type=Path,
                        default=REPO_ROOT / "src/EyeCtrl.Core/Localization/strings.json")
    parser.add_argument("--german", type=Path,
                        default=REPO_ROOT / "src/EyeCtrl.App/Localization/strings.de.json")
    parser.add_argument("--manifest", type=Path,
                        default=REPO_ROOT / "src/EyeCtrl.App/Localization/strings.de.manifest.json")
    parser.add_argument("--glossary", type=Path,
                        default=REPO_ROOT / "localization/glossary.de.json")
    parser.add_argument("--allowlist", type=Path,
                        default=REPO_ROOT / "localization/strings.identical-allowlist.json")
    parser.add_argument("--src-dir", type=Path, default=REPO_ROOT / "src")
    parser.add_argument("--refresh-de-src", action="store_true",
                        help="Recompute and write src in the German file, then exit")
    arguments = parser.parse_args(argv)

    errors: list[str] = []
    english = read_json(arguments.english, errors)
    if not isinstance(english, dict):
        errors.append(f"{arguments.english}: root is not a JSON object")

    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    validate_english(english, errors)

    if arguments.refresh_de_src:
        refresh_de_src(arguments.german, english, errors)
        for error in errors:
            print(f"ERROR: {error}")
        return 1 if errors else 0

    german = read_json(arguments.german, errors)
    manifest_raw = read_json(arguments.manifest, errors)
    glossary = read_json(arguments.glossary, errors)
    allowlist_raw = read_json(arguments.allowlist, errors)

    manifest = validate_manifest(manifest_raw, errors)

    allowlist: set[str] = set()
    if not isinstance(allowlist_raw, list) or not all(isinstance(a, str) for a in allowlist_raw):
        errors.append("strings.identical-allowlist.json: root must be a JSON array of strings")
    else:
        allowlist = set(allowlist_raw)

    if isinstance(german, dict):
        validate_german(english, german, manifest, glossary if isinstance(glossary, dict) else {},
                        allowlist, errors)
    else:
        errors.append(f"{arguments.german}: root is not a JSON object")

    validate_unused_keys(english, arguments.src_dir, errors)

    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        print(f"strings_validate: {len(errors)} error(s)")
        return 1

    count = len(english)
    print(f"strings_validate: OK ({count} key(s), {len(manifest)} manifest surface(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main())
