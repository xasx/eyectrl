# tools/CorpusBuilder/prepare_tatoeba.py
# Prepares Tatoeba sentence exports (https://downloads.tatoeba.org, CC-BY 2.0 FR)
# into per-language plain-text corpora for CorpusBuilder.
#
# Requires: Tatoeba per-language exports downloaded, e.g.
#   curl -sL -o eng.tsv.bz2 https://downloads.tatoeba.org/exports/per_language/eng/eng_sentences.tsv.bz2
# Run from repo root: py tools/CorpusBuilder/prepare_tatoeba.py <download-dir> <out-dir> [count-per-lang]
import random
import sys
from pathlib import Path

# Tatoeba three-letter codes -> EyeCtrl language pack codes
LANGS = {"eng": "en", "fra": "fr", "deu": "de", "spa": "es", "pol": "pl"}
ALLOWED_EXTRA = set(" \t',.!?;:-()\"")
MAX_CHARS = 160


def plausible(sentence: str) -> bool:
    if len(sentence) > MAX_CHARS:
        return False
    letters = sum(c.isalpha() for c in sentence)
    if letters < 10:
        return False
    return all(c.isalnum() or c in ALLOWED_EXTRA or c.isspace() for c in sentence)


def main() -> None:
    src_dir = Path(sys.argv[1])
    out_dir = Path(sys.argv[2])
    count = int(sys.argv[3]) if len(sys.argv) > 3 else 15000
    rng = random.Random(20260831)

    for tatoeba_code, lang in LANGS.items():
        src = src_dir / f"{tatoeba_code}.tsv"
        sentences = []
        with src.open(encoding="utf-8") as f:
            for line in f:
                parts = line.rstrip("\n").split("\t")
                if len(parts) >= 3 and plausible(parts[2]):
                    sentences.append(parts[2])
        if len(sentences) > count:
            sentences = rng.sample(sentences, count)
        out = out_dir / f"{lang}.txt"
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text("\n".join(sentences) + "\n", encoding="utf-8")
        print(f"{lang}: {len(sentences)} candidate sentences -> {out}")


if __name__ == "__main__":
    main()
