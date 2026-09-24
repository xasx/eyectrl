# Accent Input — Design (spec revision 2)

**Date:** 2026-09-24 · **Branch:** `feat/accent-input` (from main `2e1d9078`)
**Competitive gap:** #11 "Dead-key / diacritic composition" in
`docs/COMPETITIVE_FEATURE_ANALYSIS.md` — closed by a different mechanism than its title
suggests (D1).
**Rev 2:** external review round 1 folded (2 Critical, 2 High, 4 Medium, 5 Low — all verified
against code; dispositions inline, marked ⟦Rn⟧).

## Context

EyeCtrl emits text as Unicode directly (`KeyboardViewModel.ActivateKey` → `KeyActivatedMessage`
→ `Win32InputSimulator.SendText` when no system modifier is armed); the OS keyboard layout is
not involved, so any character is typable the moment a key for it exists. The shipped layouts
already carry the common accents DIRECTLY: QWERTZ letters have `ö ä ü ß` (capitals via
`shiftOutput`); AZERTY letters have `é è ç à` — `ù` is NOT among them, it lives on AZERTY's
symbols sub-layout today ⟦R2⟧.

The actual gaps:

1. **The long tail.** On QWERTZ, French accents (`é ê à ç …` — loanwords and names in German
   text) are untypable. On AZERTY, everything beyond `é è ç à` and the symbols-row `ù` is
   untypable (`ê â î ô û ë ï œ « »` and all capitals).
2. **AZERTY capitals.** The four AZERTY letters accent keys carry no `shiftOutput`, so
   `É È À Ç` are untypable even with Shift (and `Ù` has no key at all).

A dead-key composition engine (arm `´`, then dwell `e` → `é`) was mockup-compared against a
per-layout accents sub-layout on 2026-09-24 and the owner chose the sub-layout (D1).

## Design

### 1. New `accents` sub-layout per language layout (data only)

Each supported layout gains a `subLayouts.accents` entry — the same shape as
`numbers`/`symbols` (`label` + `rows`) — plus one new boolean sub-layout property,
`autoReturn` (D5). No schema change beyond that property; `switchSub:accents` uses the
existing `switchSub` vocabulary.

**QWERTZ — French-in-German set (28 keys):**

```
row 0:  é É è È ê Ê ë Ë à À â Â
row 1:  ç Ç î Î ï Ï ô Ô ù Ù û Û
row 2:  œ Œ « »  [Bksp] [DelWd]
footer: ABC 123 #+= 😀 Fn Space Enter Tab      (no Ctrl — see D8)
```

**AZERTY — tail beyond its direct keys (18 keys):**

```
row 0:  ê Ê â Â î Î ô Ô û Û
row 1:  ë Ë ï Ï ù Ù œ Œ « »
row 2:  [padding/wider keys] [Bksp] [DelWd]
footer: ABC 123 #+= 😀 Fn Space Enter Tab
```

(`é è ç à` stay on AZERTY letters and get capitals via D6; `ù Ù` join the tail because `ù`
lives on symbols today ⟦R2⟧ — symbols is left untouched, so `ù` remains reachable both ways.
`Œ` pairs with `œ` ⟦R11⟧; German quotes `„ “` are declined for now — D12.)

**Shape rules (both layouts, mirroring the shipped `numbers`/`symbols` pattern) ⟦R3, R7⟧:**
the last content row carries `backspace` and `deleteWord` at its right end (required by
`ShippedLayoutFilesTests.EverySubLayout_CarriesBackspaceAndDeleteWord`); `Space` and `Enter`
live in the FOOTER, not in a content row; the widest row must equal the layout's standard
12.8 units so the `SizeToContent` window does not resize between sub-layouts (exact widths at
plan time, pinned by loader tests; QWERTY already ships numbers/symbols rows wider than its
letters rows, so this is an accents-specific invariant, not a new global one) ⟦R13⟧. Every
lower-case accent key also carries `shiftOutput` = its capital ⟦R6⟧; the explicit capital
keys stay (two roads to `É` — D11).

QWERTY gets no accents sub-layout (D4). The accents sub-layout defines only its key rows —
prediction bar, dwell chrome, and window chrome are unchanged. Prediction behavior flows
through `ActivateKey`/`LearnWord` exactly as for `ö` today; `de.json`/`fr.json` already carry
accented forms (D9).

### 2. Entry keys — positions pinned ⟦R4, R8⟧

- **QWERTZ:** `Acc` (`1.0`, `switchSub:accents`) in the LETTERS footer between `Fn` and
  `Space`, exactly as mocked. Footer grows 11.7 → 12.7 ≤ 12.8; nothing else moves. The
  accents-footer key under `Acc`'s x-range is its `Space` — named here because the resting
  gaze lands on it after dwelling `Acc`; D10 makes that harmless.
- **AZERTY:** `Acc` (`1.0`) at the right end of a shorter letters row (rows 0–1 are 11.2
  each → 12.2 ≤ 12.8; exact row/index at plan time). This keeps AZERTY at ONE extra dwell per
  accent — the rev-1 placement via the numbers/symbols footers cost two, and its "rare tail"
  rationale does not survive French (`ê` in *être*, `ô` in *côté*, `û` in *sûr*). The accents
  key landing under AZERTY's `Acc` is named at plan time once widths are pinned.

### 3. Auto-return (the only engine change)

`KeyboardLayout` parses `autoReturn`; `KeyboardViewModel` honors it: after a **non-special key
activation** — a key carrying `output`, whatever modifiers are armed ⟦R10⟧ — **while
`!IsEmojiMode`** ⟦R1⟧ on an `autoReturn` sub-layout, the active sub-layout switches to
`DefaultSubLayout` (letters). `specialAction` keys (backspace, deleteWord, space, switches,
modifiers) never trigger it. Rationale for the emoji guard: emoji keys carry `Output` and flow
through the same `ActivateKey` path while `IsEmojiMode` overlays the text sub-layout — without
the guard, an emoji dwell with `accents` underneath would swap the grid under the open emoji
mode (the `RebuildKeys`-while-emoji trap `KeyboardWindow.xaml.cs:362-369` documents).

Manual switching behaves exactly as today; hiding the keyboard already resets to
`DefaultSubLayout` (`KeyboardViewModel.cs:183`), so accents cannot trap a user across
hide/show.

**Post-return dwell suppression ⟦R5 — D10⟧:** after an auto-return swap, dwell arming on the
letters grid is suppressed for the normal cooldown PLUS one key-dwell duration (the gaze is
resting on the accent just typed; the letters key now under that spot — `Win`/`Caps`/`Alt` at
the row-0/1 left edge — must not latch). Manual `switchSub` dwells keep today's behavior; the
suppression is specific to the unrequested swap.

Typing `café` on QWERTZ: `c · a · f ·` dwell `Acc` `· é` → auto-return (suppression window) →
continue typing.

### 4. Data fixes riding along

- AZERTY letters: the FOUR accent keys gain `shiftOutput` (`é→É, è→È, à→À, ç→Ç`) (D6) ⟦R2⟧.
- QWERTZ: `ß` gains `shiftOutput: ẞ` (U+1E9E) — note in D7: with Caps Lock on (which counts as
  shifted), all-caps typing produces `STRAẞE`, unlike the German T1 layout's `STRASSE`
  behavior ⟦R12⟧.

## Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|----------|-----------|----------|
| D1 | Per-layout accents sub-layout + auto-return, chosen via mockup 2026-09-24 | ONE extra dwell per accent on every supported layout (rev 1 broke this promise for AZERTY — fixed in D3) | Dead-key engine (2 extra dwells, pending-state UI, compose tables — revisit if pre-baked sets prove limiting); long-dwell popups on base letters (mis-dwell risk) |
| D2 | QWERTZ set = 28 keys as listed (12 lower + 12 upper + `œ Œ « »`) | Covers German-text loanwords/names; per-layout tables stay small | Pan-European sets; German quotes `„ “` (D12) |
| D3 | AZERTY entry = `Acc` on a letters row's right end (rows 0–1 have 1.6u spare) ⟦R4⟧ | One extra dwell per accent; the French tail is not rare | Rev-1 numbers/symbols-footer entry (2 dwells); resizing letters-footer keys (the row budget is cheaper) |
| D4 | QWERTY gets no accents sub-layout | English target; owner can request later | Shipping an unused sub-layout |
| D5 | Auto-return = sub-layout property `autoReturn`, non-special keys only, target = layout default, gated on `!IsEmojiMode` ⟦R1, R10⟧ | Self-documenting in data; no magic sub-layout name in the VM; emoji mode never sees a grid swap it didn't ask for | Hardcoded `"accents"` id check; returning on specialAction keys; returning under emoji mode (the grid-wipe bug) |
| D6 | AZERTY shiftOutputs on its four letters accent keys | `É È À Ç` currently untypable — real gap | — |
| D7 | `ß` gains `ẞ` shiftOutput | One-attribute fix; official capital since 2017. KNOWN EFFECT: Caps-Lock typing yields `STRAẞE`, not T1's `STRASSE` — veto here if unwanted | Leaving ẞ untypable |
| D8 | Accents footer = `ABC 123 #+= 😀 Fn Space Enter Tab` (no Ctrl; Bksp/DelWd end the last content row) ⟦R7⟧ | Matches every shipped sub-layout's shape (spatial memory); Ctrl on accents is harmful (VK-path fallthrough types `é` while skipping `CurrentInput`, desyncing predictions) | Rev-1 mock shape (Space/Enter in a content row, no DelWd) |
| D9 | No prediction changes | `ActivateKey`/`LearnWord` already flow for accented outputs; `de.json`/`fr.json` carry accented forms | Accent-stripping prediction normalization (lossy) |
| D10 | Post-auto-return dwell suppression: cooldown + one key-dwell ⟦R5⟧ | The swap is unrequested; a resting gaze must not latch `Win`/`Caps`/`Alt`. Manual switches keep today's behavior | (b) require gaze to leave the landing key (more plumbing, same effect); (c) accept and ledger (mis-dwell-sensitive owner) |
| D11 | Keep explicit capital keys AND give lower-case accents `shiftOutput` ⟦R6⟧ | Caps Lock / armed Shift then behave as on letters (D6's own reasoning); capitals remain one dwell | Capitals only via Shift (breaks Caps-Lock typing) |
| D12 | `„ “` declined for the QWERTZ set | German quotes are a symbols-sub-layout concern, not accents; owner can add later | Including them now |

## Testing ⟦R3, R9⟧

- **Shipped-file assertions live in `ShippedLayoutFilesTests` (App.Tests**, which loads the
  real JSON via `SourceScanner.RepoRoot()`**): accents sub-layout present on QWERTZ/AZERTY;
  every accents key has non-empty `output`; lowercase keys carry their capital `shiftOutput`;
  widest accents row = the layout's 12.8 standard; the existing
  `EverySubLayout_CarriesBackspaceAndDeleteWord` theory and the special-action
  uniqueness/known-action tests then cover accents automatically — no edits needed there.
- **`KeyboardLayoutLoaderTests` (Core.Tests**, inline JSON**): `autoReturn` round-trips;
  absent flag defaults false.
- **ViewModels.Tests:** auto-return fires after a non-special key on an `autoReturn`
  sub-layout; does NOT fire after backspace/deleteWord/space/switch keys; does NOT fire while
  `IsEmojiMode` (emoji entered from accents, emoji dwell, grid state intact, sub-layout still
  `accents`); manual switches unchanged; target is `DefaultSubLayout`, not "previous
  sub-layout"; `IsShifted`+`é` yields `É` (D6/D11 pin); `ß`+Shift yields `ẞ`. Mutation
  checks: remove the `!IsEmojiMode` guard → emoji test fails; swap the target to previous
  sub-layout → a manual-switch-then-accent sequence fails.
- **Real window (App.Tests STA, cheap):** shipped-QWERTZ round trip at scale 2.0 (existing
  probe tests cover QWERTY only): letters → `Acc` → `é` → back on letters, plus the emoji
  case from R1.

## Out of scope

Dead-key composition engine (D1 revisit path) · QWERTY accents (D4) · more keyboard languages
/ CJK/IME (the competitive doc's own boundary) · settings UI · prediction changes ·
symbols-sub-layout redesign (including German quotes — D12).
