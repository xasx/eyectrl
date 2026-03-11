# tools/gen_dicts.py
# Requires: pip install wordfreq
# Run from repo root: python tools/gen_dicts.py
import json
import os
from wordfreq import top_n_list

LANGS = ["fr", "es", "de", "pl"]
N = 5000
OUT_DIR = "src/EyeCtrl.App/Resources/Dictionaries"

for lang in LANGS:
    words = top_n_list(lang, N, wordlist="best")
    freq_dict = {}
    for i, word in enumerate(words):
        score = max(1, int(70000 / (i + 1)))
        freq_dict[word] = score
    out_path = os.path.join(OUT_DIR, f"{lang}.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(freq_dict, f, ensure_ascii=False, indent=2)
    print(f"Generated {out_path} ({len(freq_dict)} words)")
