# Dictionary builder (German and English)

`build_dictionary.py` regenerates
`src/EyeCtrl.App/Resources/Dictionaries/{de,en}.json` from orgtre's Google Books
1-grams and Tatoeba sentences. Python 3.11+, standard library only.

## Sources and licences

| Input | Source | Licence | sha256 (first 16) |
| --- | --- | --- | --- |
| `ngrams/1grams_german.csv` | [orgtre/google-books-ngram-frequency](https://github.com/orgtre/google-books-ngram-frequency) at commit `e20471c15a758be3362b16d07870b34df4f7ccc3` (2023-08-14), derived from the Google Books Ngram dataset v3 | CC BY 3.0 | `28104763E42FF9FE` |
| `ngrams/1grams_english.csv` | same repository and commit | CC BY 3.0 | `F7F63AA08F0BB2F7` |
| `deu_sentences.tsv.bz2` | [Tatoeba per-language export](https://downloads.tatoeba.org/exports/per_language/deu/), Last-Modified 2026-10-17 06:37:12 GMT | CC BY 2.0 FR | `CCD063727CF3B6D8` |
| `eng_sentences.tsv.bz2` | [Tatoeba per-language export](https://downloads.tatoeba.org/exports/per_language/eng/), same export | CC BY 2.0 FR | `BB3DCC28A8984A26` |

The current `{de,en}.json` is read only for its `abbreviations` list, which is
carried over unchanged.

## Download

```powershell
curl -L -o 1grams_german.csv   https://raw.githubusercontent.com/orgtre/google-books-ngram-frequency/e20471c15a758be3362b16d07870b34df4f7ccc3/ngrams/1grams_german.csv
curl -L -o 1grams_english.csv  https://raw.githubusercontent.com/orgtre/google-books-ngram-frequency/e20471c15a758be3362b16d07870b34df4f7ccc3/ngrams/1grams_english.csv
curl -L -o deu_sentences.tsv.bz2 https://downloads.tatoeba.org/exports/per_language/deu/deu_sentences.tsv.bz2
curl -L -o eng_sentences.tsv.bz2 https://downloads.tatoeba.org/exports/per_language/eng/eng_sentences.tsv.bz2
```

## Build

The `--size 10000` and `--tatoeba-weight 0.5` defaults are the shipped recipe.
From the repository root:

```powershell
python -I tools/DictionaryBuilder/build_dictionary.py de `
  <1grams_german.csv> <deu_sentences.tsv.bz2> <eng_sentences.tsv.bz2> <1grams_english.csv> `
  src/EyeCtrl.App/Resources/Dictionaries/de.json src/EyeCtrl.App/Resources/Dictionaries/de.json

python -I tools/DictionaryBuilder/build_dictionary.py en `
  <1grams_english.csv> <eng_sentences.tsv.bz2> <eng_sentences.tsv.bz2> <1grams_english.csv> `
  src/EyeCtrl.App/Resources/Dictionaries/en.json src/EyeCtrl.App/Resources/Dictionaries/en.json
```

The current file is both the abbreviations source and the output; after the
first run the file carries its own abbreviations, so rebuilding is idempotent.

## Reference outputs

| File | sha256 | Bytes |
| --- | --- | --- |
| `de.json` | `C2E90F1A0BF66E4E43A2C80E7CABE2B38C3CCDEBFA59D341038CA485C034DA97` | 327,589 |
| `en.json` | `918A471BBEEE33E376AE694341BDBDA840228C31E186FF109731605650ECB3BD` | 209,220 |

## Determinism

The output is byte-reproducible:

- candidate keys are iterated in sorted order, never in `set` order;
- equal blended values are ranked by key (`(-blended, key)`), so ties do not
  follow Python's per-process hash randomization;
- a `"casing"` evidence tie picks the alphabetically first form;
- `"casing"` is written sorted by key, `"words"` follows the ranking order, and
  `"abbreviations"` keeps the source file's order.

Two runs in fresh processes with the inputs above produce identical bytes.
