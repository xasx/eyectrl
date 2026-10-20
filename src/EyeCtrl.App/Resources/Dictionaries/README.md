# Shipped prediction dictionaries

`PredictionStackFactory` loads `{language}.json` from this folder. The files
use lowercase keys with a frequency count, plus an optional `casing` object
(lowercase key to display form) and an `abbreviations` list.

## de.json, en.json

Adapted from ["google-books-ngram-frequency" by
orgtre](https://github.com/orgtre/google-books-ngram-frequency), licensed
**CC BY 3.0** (https://creativecommons.org/licenses/by/3.0/), derived from the
Google Books Ngram dataset. Merged with word counts computed from
[Tatoeba](https://tatoeba.org) sentences, licensed **CC BY 2.0 FR**
(https://creativecommons.org/licenses/by/2.0/fr/). Changes: re-scaled to parts
per million, filtered (names and single letters removed), merged, a curated
floor for conversational essentials, and display casing. Built with
`tools/DictionaryBuilder`; see its README for the exact inputs and commands.

## fr.json, es.json, pl.json, legacy/de.v1.json, legacy/en.v1.json

Adapted from [hermitdave/FrequencyWords](https://github.com/hermitdave/FrequencyWords),
whose content is licensed **CC BY-SA 4.0**
(https://creativecommons.org/licenses/by-sa/4.0/), derived from OpenSubtitles
(http://www.opensubtitles.org/). Changes: filtered and re-scaled. These adapted
files are therefore licensed **CC BY-SA 4.0**.

`legacy/{de,en}.v1.json` are the previous German and English dictionaries, kept
only so the user-dictionary repair can recognise an inflated file written
against that base. They are never used for prediction.
