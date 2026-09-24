# Accent Input — Design (spec revision 1)

**Date:** 2026-09-24 · **Branch:** `feat/accent-input` (from main `2e1d9078`)
**Competitive gap:** #11 "Dead-key / diacritic composition" in
`docs/COMPETITIVE_FEATURE_ANALYSIS.md` — closed by a different mechanism than its title
suggests (see D1).

## Context

EyeCtrl emits text as Unicode directly (`KeyboardViewModel.ActivateKey` → `KeyActivatedMessage`
→ `Win32InputSimulator.SendText`); the OS keyboard layout is not involved, so any character is
typable the moment a key for it exists. The three shipped layouts already carry the common
accents DIRECTLY on their letters sub-layouts — QWERTZ has `ö ä ü ß` (with capitals via
`shiftOutput`), AZERTY has `é è ç à ù`.

The actual gaps:

1. **The long tail.** On QWERTZ, French accents (`é ê à ç …`) — common in German text as
   loanwords and names — are untypable. On AZERTY, everything beyond the five direct accents
   (`ê â î ô û ë ï ù œ « »`) is untypable.
2. **AZERTY capitals.** The five AZERTY accent keys carry no `shiftOutput`, so
   `É È À Ç Ù` are untypable even with Shift.

A dead-key composition engine (arm `´`, then dwell `e` → `é`) was mockup-compared against a
per-layout accents sub-layout on 2026-09-24 and the owner chose the sub-layout (D1).

## Design

### 1. New `accents` sub-layout per language layout (data only)

Each supported layout gains a `subLayouts.accents` entry in its layout JSON — the same shape
as `numbers`/`symbols` (`label` + `rows` of key definitions with `output`/`shiftOutput`) —
plus one new boolean sub-layout property, `autoReturn` (D5). No schema change beyond that
property; `switchSub:accents` uses the existing `switchSub` special-action vocabulary.

**QWERTZ (`qwertz.json`) — French-in-German set** (lower + upper), as mocked:

```
é É è È ê Ê ë Ë à À â
Â ç Ç î Î ï Ï ô Ô ù Ù û
Û œ « »  [Bksp] [Space] [Enter]
footer: Ctrl ABC 123 #+= 😀 Fn Tab
```

**AZERTY (`azerty.json`) — tail beyond its direct keys:**

```
ê Ê â Â î Î ô Ô û Û ë
Ë ï Ï œ « »  [Bksp] [Space] [Enter]
footer: Ctrl ABC 123 #+= 😀 Fn Tab
```

(`é è ç à ù` are excluded — they are already one-dwell direct on AZERTY letters, and D6 gives
them their capitals.)

**QWERTY: none** (D4).

The accents sub-layout defines only its key rows. The prediction bar, dwell chrome, and window
geometry are per-window and unchanged. Any learned-word and prediction behavior flows through
`ActivateKey` exactly as it does for `ö` today — no predictor changes (D9).

### 2. Entry keys

- **QWERTZ:** a `1.0`-width `Acc` key (`switchSub:accents`) in the LETTERS footer, which has
  the spare budget (footer 11.7 vs row max 12.8).
- **AZERTY:** the letters footer is full (12.7/12.8), so the `Acc` key goes in the NUMBERS and
  SYMBOLS footers (each 11.7 vs 12.8). AZERTY's common accents are already direct on letters;
  the tail is rare enough to be worth one extra hop (D3).

### 3. Auto-return (the only engine change)

`KeyboardLayout` parses the new `autoReturn` flag; `KeyboardViewModel` honors it: after a key
on an `autoReturn` sub-layout EMITS TEXT — i.e. a key carrying `output`, through the ordinary
`ActivateKey` path — the active sub-layout switches back to the layout's default
(`DefaultSubLayout`, i.e. letters). `specialAction` keys (backspace, space, `switchSub`,
modifiers) never trigger the return (D5). Manual switching behaves exactly as today.

Typing `café` on QWERTZ: `c · a · f ·` dwell `Acc` `· é` → auto-return → continue typing.

### 4. Data fixes riding along

- AZERTY letters: the five accent keys gain `shiftOutput` (`é→É, è→È, à→À, ç→Ç, ù→Ù`) (D6).
- QWERTZ: `ß` gains `shiftOutput: ẞ` (U+1E9E, capital sharp S) (D7).

## Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|----------|-----------|----------|
| D1 | Per-layout accents sub-layout + auto-return, chosen via mockup 2026-09-24 | 1 extra dwell per accent; pure data + one small rule; no new interaction modes on a surface where the owner's documented mis-dwell rate argues against modes | Dead-key engine (2 extra dwells, pending-state UI, compose tables — revisit if pre-baked sets prove limiting); long-dwell popups on base letters (mis-dwell risk) |
| D2 | QWERTZ set = the 20-char French-in-German mockup set | Covers German-text loanwords/names; per-layout tables stay small and readable | Larger pan-European sets (YAGNI) |
| D3 | AZERTY entry via numbers/symbols footers | Letters footer has no width budget; its common accents are direct already | Resizing/shrinking existing letters-footer keys |
| D4 | QWERTY gets no accents sub-layout | English target; owner can request later | Shipping an unused sub-layout |
| D5 | Auto-return = sub-layout property `autoReturn`, text-emitting keys only, target = layout default | Self-documenting in data; no magic sub-layout name in the VM; backspace/switch stay predictable | Hardcoded `"accents"` id check; returning on every key including backspace |
| D6 | AZERTY shiftOutputs added | `É È À Ç Ù` currently untypable — a real gap found during exploration | Leaving capitals to a future sub-layout only |
| D7 | `ß` gains `ẞ` shiftOutput | One-attribute fix; official German capital since 2017 | Leaving ẞ untypable |
| D8 | Accents footer mirrors the standard switch row (ABC/123/#+=/😀/Fn/Tab + Bksp/Space/Enter) | Familiar shape; matches the mockup the owner approved | Minimal footer without Bksp/Space (users correct typos on any sub-layout) |
| D9 | No prediction changes | `ActivateKey`/`LearnWord` already flow for accented outputs; de/fr dictionaries carry accented forms | Accent-stripping prediction normalization (YAGNI, and lossy) |

## Testing

- **Loader (Core.Tests):** the accents sub-layout parses on QWERTZ/AZERTY; `autoReturn`
  round-trips; every accents key has non-empty `output`; every sub-layout's row widths stay ≤
  that layout's letters-row max (the width invariant that guards the footer fit).
- **ViewModel (ViewModels.Tests):** auto-return fires after a text-emitting key on an
  `autoReturn` sub-layout; does NOT fire after backspace or a `switchSub` key; manual switches
  unchanged; `IsShifted` + `é` yields `É` (the D6 data pin); `ß`+Shift yields `ẞ`.
- No App/UI test surface beyond what exists (no new windows, no settings rows).

## Out of scope

Dead-key composition engine (D1 revisit path) · QWERTY accents (D4) · more keyboard languages /
CJK/IME (per the competitive doc's own boundary) · settings UI · prediction changes ·
TD-style key popup variants.
