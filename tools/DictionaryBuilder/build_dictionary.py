"""Build an EyeCtrl prediction dictionary from orgtre's Google Books 1-grams + Tatoeba sentences.

usage: python -I build_dictionary.py <lang de|en> <orgtre 1grams csv> <tatoeba <lang>_sentences.tsv.bz2>
                                     <english tatoeba .tsv.bz2 (name list source)>
                                     <english orgtre 1grams csv (name list source)> <current dictionary json>
                                     <out json> [--size 10000] [--tatoeba-weight 0.5]

Sources and licences (keep this notice with the output):
- orgtre/google-books-ngram-frequency, 1grams_<language>.csv, CC BY 3.0
  (https://github.com/orgtre/google-books-ngram-frequency), derived from the Google Books Ngram dataset v3.
- Tatoeba sentences (https://tatoeba.org), CC BY 2.0 FR.

Method:
1. Both sources become parts-per-million (ppm) per lowercase key. orgtre's total token count is recovered
   from its last row: sum(freq of the 10,000 rows) / cumshare(last row).
2. Tatoeba is tokenised into word-shaped tokens (letters with internal apostrophe/hyphen; U+2019 -> ').
   Casing evidence only comes from non-sentence-initial positions.
3. Names are removed from the Tatoeba side (Tatoeba's sentences are full of "Tom", "Mary", "Maria"...):
   a Tatoeba-only key whose non-initial uses are >= 90 % capitalised is dropped when it is a name, i.e.
   capitalised-only in ENGLISH Tatoeba too, or listed in EXTRA_NAMES, or a name + "s"/"'s". In English every
   capitalised-only Tatoeba-only key is dropped except the I-contractions. German capitalised nouns that
   are not names (Hausaufgaben, Regenschirm...) are kept.
4. Single letters are dropped (except English "a" and "I").
5. blended ppm = (1 - w) * orgtre_ppm + w * tatoeba_ppm.
6. A short curated list of conversational essentials (greetings, politeness, yes/no; ESSENTIALS) is raised to at
   least the blended ppm of rank FLOOR_RANK, because books and learner sentences under-represent them.
7. The top <size> keys are kept; counts = max(1, round(blended_ppm * 3)), so the top word is ~100,000 and the
   10,000th is in the teens; a learned word (+1 per use) can still matter.
8. "casing" maps a key to its display form when the dominant form is not all-lowercase: German from orgtre +
   Tatoeba non-initial evidence (nouns); English from orgtre only (its vetted capitalised exceptions) + "I'm" etc.
9. "abbreviations" are carried over unchanged from the current dictionary file.

Determinism (the output must be byte-reproducible):
- candidate keys are processed in sorted order, not set order;
- equal blended values are ordered by key (`(-blended, key)`), so ties never follow hash order;
- a casing evidence tie picks the alphabetically first form;
- the "casing" object is written sorted by key; "words" follows the ranking order; "abbreviations" keeps the
  current file's order. Two runs in fresh processes produce identical bytes.
"""
import argparse
import bz2
import collections
import csv
import json
import re
import sys

WORD = re.compile(r"[^\W\d_]+(?:['\-][^\W\d_]+)*")
SCALE = 3.0
MAX_LEN = 25
I_CONTRACTIONS = {"i'm", "i've", "i'll", "i'd"}
# Conversational essentials that books and learner sentences under-represent (greetings, politeness, yes/no).
# Each is raised to at least the blended ppm of rank FLOOR_RANK. A curated list, documented in the README.
FLOOR_RANK = 300
ESSENTIALS = {
    # Regional greetings (moin, servus) are left to learning: raising them would push them into every bar.
    "de": ["hallo", "tschüss", "tschüs", "danke", "bitte", "ja", "nein", "okay", "gerne", "gern", "genau",
           "vielleicht", "entschuldigung", "klar", "super", "prima"],
    "en": ["hello", "hi", "bye", "goodbye", "thanks", "thank", "please", "yes", "no", "okay", "sorry", "yeah"],
}
ESSENTIAL_NOUNS = {"entschuldigung": "Entschuldigung"}
# Single letters are not useful suggestions (typing "s" must not suggest "S"), except real one-letter words.
ONE_LETTER_WORDS = {"de": set(), "en": {"a", "i"}}
# German Tatoeba names that are absent from English Tatoeba (found by inspecting the capitalised-only list).
EXTRA_NAMES = {
    "de": {"johannes", "elke", "liisa", "markku", "thomas", "stefan", "anne", "anna", "bob", "ken", "bill",
           "paul", "jane", "rima", "ziri", "sami", "layla", "toki", "pona", "tatoeba", "tōkyō", "yanni",
           "skura", "mennad", "jim", "tim", "lisa", "emily", "jack", "alice", "kate", "hans", "peter"},
    "en": set(),
}


def read_orgtre(path):
    rows = []
    with open(path, encoding="utf-8") as f:
        for row in csv.DictReader(f):
            rows.append((row["ngram"], int(row["freq"]), float(row["cumshare"])))
    total = sum(freq for _, freq, _ in rows) / rows[-1][2]
    ppm = collections.Counter()
    forms = collections.defaultdict(collections.Counter)
    for gram, freq, _ in rows:
        key = gram.lower()
        ppm[key] += freq / total * 1e6
        forms[key][gram] += freq / total * 1e6
    return ppm, forms


def read_tatoeba(path):
    non_initial = collections.Counter()   # exact form
    keys = collections.Counter()          # lowercase key, all positions
    total = 0
    with bz2.open(path, "rt", encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 3:
                continue
            for i, m in enumerate(WORD.finditer(parts[2].replace("’", "'"))):
                tok = m.group(0)
                total += 1
                keys[tok.lower()] += 1
                if i > 0:
                    non_initial[tok] += 1
    forms = collections.defaultdict(collections.Counter)
    for tok, c in non_initial.items():
        forms[tok.lower()][tok] += c
    ppm = {k: c / total * 1e6 for k, c in keys.items()}
    return ppm, forms, total


def capitalised_share(form_counts):
    n = sum(form_counts.values())
    if n == 0:
        return 0.0
    cap = sum(c for f, c in form_counts.items() if f[:1].isupper())
    return cap / n


def english_name_set(eng_path, eng_orgtre_keys):
    ppm, forms, _ = read_tatoeba(eng_path)
    names = set()
    for key, form_counts in forms.items():
        if sum(form_counts.values()) >= 5 and capitalised_share(form_counts) >= 0.9 and key not in eng_orgtre_keys \
                and key not in I_CONTRACTIONS:
            names.add(key)
    return names


def is_word(key):
    return 1 <= len(key) <= MAX_LEN and WORD.fullmatch(key) is not None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("lang")
    ap.add_argument("orgtre")
    ap.add_argument("tatoeba")
    ap.add_argument("english_tatoeba")
    ap.add_argument("english_orgtre")
    ap.add_argument("current")
    ap.add_argument("out")
    ap.add_argument("--size", type=int, default=10_000)
    ap.add_argument("--tatoeba-weight", type=float, default=0.5)
    a = ap.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    o_ppm, o_forms = read_orgtre(a.orgtre)
    t_ppm, t_forms, t_total = read_tatoeba(a.tatoeba)
    eng_orgtre_keys = set(read_orgtre(a.english_orgtre)[0])
    names = english_name_set(a.english_tatoeba, eng_orgtre_keys) | EXTRA_NAMES.get(a.lang, set())

    dropped = collections.Counter()
    w = a.tatoeba_weight
    blended = {}
    for key in sorted(set(o_ppm) | set(t_ppm)):
        if not is_word(key):
            dropped["not word-shaped"] += 1
            continue
        t = t_ppm.get(key, 0.0)
        if key not in o_ppm and t > 0:
            cap_only = capitalised_share(t_forms.get(key, {})) >= 0.9
            stem = key[:-2] if key.endswith("'s") else key[:-1] if key.endswith("s") else None
            is_name = key in names or (stem is not None and stem in names)
            if a.lang == "en" and cap_only and key not in I_CONTRACTIONS:
                dropped["en capitalised-only Tatoeba word (name)"] += 1
                continue
            if a.lang != "en" and cap_only and is_name:
                dropped["de Tatoeba name"] += 1
                continue
        # A name orgtre also lists (from books) keeps its book share but gets no Tatoeba boost.
        if key in o_ppm and (key in names) and capitalised_share(t_forms.get(key, {})) >= 0.9:
            t = 0.0
        if len(key) == 1 and key not in ONE_LETTER_WORDS.get(a.lang, set()):
            dropped["single letter"] += 1
            continue
        blended[key] = (1 - w) * o_ppm.get(key, 0.0) + w * t

    ranked_values = sorted(blended.values(), reverse=True)
    floor = ranked_values[FLOOR_RANK - 1]
    raised = []
    for key in ESSENTIALS.get(a.lang, []):
        if blended.get(key, 0.0) < floor:
            raised.append(f"{key} (rank {ranked_values.index(blended[key]) + 1 if key in blended else 'absent'})")
            blended[key] = floor

    top = sorted(blended.items(), key=lambda kv: (-kv[1], kv[0]))[: a.size]
    words = {k: max(1, round(v * SCALE)) for k, v in top}

    casing = {}
    for k, _ in top:
        evidence = collections.Counter()
        for f, v in o_forms.get(k, {}).items():
            evidence[f] += v
        if a.lang == "en":
            # English capitals come from orgtre only (its capitalised entries are a vetted exception list);
            # Tatoeba's names would otherwise capitalise common words ("Cookie", a dog). Plus "I'm" etc.
            if k in I_CONTRACTIONS:
                evidence = collections.Counter({"I" + k[1:]: 1.0})
        else:
            t_n = sum(t_forms.get(k, {}).values())
            for f, c in t_forms.get(k, {}).items():
                evidence[f] += t_ppm.get(k, 0.0) * c / t_n if t_n else 0
        if k in ESSENTIALS.get(a.lang, []):
            # Their casing evidence is thin and mostly sentence-initial ("Tschüss!"); only nouns get a capital.
            evidence = collections.Counter({ESSENTIAL_NOUNS.get(k, k): 1.0})
        if evidence:
            best = max(evidence.values())
            tied = sorted(form for form, count in evidence.items() if count == best)
            # Equal evidence: the alphabetically first form wins (deterministic).
            display = tied[0]
            if display != k and display.lower() == k:
                casing[k] = display

    casing = dict(sorted(casing.items()))

    with open(a.current, encoding="utf-8") as f:
        abbreviations = json.load(f).get("abbreviations", [])

    with open(a.out, "w", encoding="utf-8") as f:
        json.dump({"words": words, "casing": casing, "abbreviations": abbreviations}, f, ensure_ascii=False, indent=2)

    print(f"{a.lang}: tatoeba tokens={t_total:,}; candidates={len(blended):,}; kept={len(words):,}; casing entries={len(casing):,}")
    print(f"  dropped: {dict(dropped)}; name list size={len(names):,}")
    print(f"  essentials raised to the rank-{FLOOR_RANK} floor: {', '.join(raised) or 'none'}")
    print(f"  only from Tatoeba in the kept set: {sum(1 for k in words if k not in o_ppm):,}")
    print(f"  top 30: {', '.join(casing.get(k, k) for k, _ in top[:30])}")


if __name__ == "__main__":
    sys.exit(main())
