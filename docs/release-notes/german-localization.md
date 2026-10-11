# German localization — release notes

One file for the whole localization work; each ticket appends its section under its ticket id.

## T02 — literal ratchet, explicit number formats, host-supplied control text

Two user-visible changes land with T02, the foundation step. Both are corrections: an English
screen used to borrow the machine's locale for its numbers.

- **Numbers no longer follow a German PC's locale.** An English screen on a de-DE machine shows
  **"12.3 px"** where it showed **"12,3 px"**. The same applies to the calibration error/quality
  figures, the model download sizes, the eye-care summary and the track-status distance. A German
  screen keeps German formatting, and switches to it only when its surface's German is switched on.

- **The "Last calibration" date is unambiguous while Settings renders English.** It reads
  **"2026-10-08 14:03"** (year-month-day), so a German reader cannot mistake it for the 10th of
  August. Once the Settings surface renders German it shows **"08.10.2026 14:03"**, that culture's
  own general date/time order. The user chose the unambiguous English form on 2026-10-08.

## T10 — hand-off note from the Daylight theme merge (2026-10-11)

The Daylight theme branch renamed and added Settings keys in `strings.json`. The T10 German
draft (`l10n/t10-german-draft` @ `21f1677b`) predates that merge, so before T10 lands:

- **Re-key the swatch captions**: `settings.control.swatch.amber` ("Bernstein") →
  `settings.control.swatch.warning`, and `swatch.light` ("Hell") → `swatch.text`. The English
  captions became role names because Daylight's warning swatch is a brick red and its text
  swatch is near-black — colour names were wrong on the light theme (Daylight ledger D6).
  Fresh German for both needs a new sign-off.
- **Add `settings.general.theme.option.daylight`** (the fourth theme option; a proper noun —
  "Daylight" is expected to stay untranslated, matching how the other theme names are handled).
- **Re-translate `settings.general.theme.description`**: the draft's signed-off
  "Alle drei sind dunkel; „Kontrast" ist das kräftigste." states a falsehood once Daylight
  ships. The English now reads "Daylight is light, for bright rooms; Contrast is the boldest."
- **Decide on German "Text"**: the natural caption for `swatch.text` is identical to the
  English, which the identical-to-English gate flags. Either allowlist it in
  `localization/strings.identical-allowlist.json` with a reason, or pick a wording such as
  "Textfarbe".

Safety net, for the record (round-5 review measured this): merging the draft with the theme
branch raises no textual conflicts; the orphaned `swatch.amber`/`swatch.light` keys fail
`tools/strings_validate.py` immediately, so the overlap cannot land silently. The stale
description and missing keys surface only once the `settings` surface enters the German
manifest, where the stale-`src` and missing-key gates fire.
