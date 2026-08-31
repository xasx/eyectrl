# Sentence completion tries

`completions_{lang}.bin` files are binary sentence-completion tries loaded by
`PredictionStackFactory` (one per language pack: en, fr, de, es, pl).

## Data source and license

The sentences come from [Tatoeba](https://tatoeba.org) (weekly exports at
https://downloads.tatoeba.org), licensed **CC-BY 2.0 FR**
(https://creativecommons.org/licenses/by/2.0/fr/). Per-language export files
were sampled deterministically (seeded) and filtered, then indexed with
`tools/CorpusBuilder`. Keep this notice distributed with the `.bin` files.

The sampling seed is fixed, but the input is not: Tatoeba republishes the export
weekly, so following the recipe below reproduces the *pipeline*, not these exact
`.bin` files. Record the export date the shipped tries were built from — here,
and again on every regeneration — if byte-level reproducibility ever matters.

    Export date of the shipped tries: (unrecorded — fill in on next regeneration)

## Regenerating

From a checkout root, with the per-language Tatoeba exports downloaded
(three-letter codes: eng, fra, deu, spa, pol):

```bash
curl -sL -o eng.tsv.bz2 https://downloads.tatoeba.org/exports/per_language/eng/eng_sentences.tsv.bz2
bunzip2 eng.tsv.bz2          # prepare_tatoeba.py reads <code>.tsv, not the .bz2
# ...same for fra, deu, spa, pol into the same download dir

python tools/CorpusBuilder/prepare_tatoeba.py <download-dir> <prepared-dir> 15000
for lang in en fr de es pl; do
  dotnet run --project tools/CorpusBuilder -- "<prepared-dir>/$lang.txt" \
    "src/EyeCtrl.App/Resources/Completions/completions_$lang.bin" 3 12 true
done
```

The count-per-lang and 3–12 word window keep each trie around 3 MB and keep
completions short enough to be useful for gaze typing; the `true` shortness
bias ranks shorter completions above longer ones (frequency ties otherwise
make the order arbitrary, since sampled sentences are near-unique).
`prepare_tatoeba.py` holds the sampling seed; changing it changes every trie.
