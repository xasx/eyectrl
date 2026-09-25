# Accent Input — Design (spec revision 5)

**Date:** 2026-09-24 · **Branch:** `feat/accent-input` (from main `2e1d9078`)
**Competitive gap:** #11 "Dead-key / diacritic composition" in
`docs/COMPETITIVE_FEATURE_ANALYSIS.md` — closed by a different mechanism than its title
suggests (D1).
**Rev 3:** external round 2 folded (verdict FIX-THEN-SHIP: R8 re-opened, I1–I6, M1–M8;
all verified against code — dispositions inline, marked ⟦Rn⟧/⟦In⟧/⟦Mn⟧).
**Rev 4:** plan-review round folded (B1–B3, H1–H3, M1–M4, L1–L6). D13 is now a SPATIAL rule
(plan review B1/M1: the post-swap centres are unarranged at the first samples, so no
post-swap key resolution at all; the clear test is a distance from the anchor, immune to the
boundary-flicker the review measured). D16 is SUPERSEDED by the row-0 zero-move placement
(plan review M4, owner-defaulted): QWERTZ row 0 has the same 11.2 shape as AZERTY's, so the
`Win`-widening trick fits both layouts and `Space` keeps its width. M7 is implemented as an
unconditional `ActiveModifiers` raise AFTER the swap (plan review H1).
**Rev 5:** plan-review round 2 folded — the QWERTZ grid is now the plan's 10/10/8 (bigger
dwell targets, ⟦I1⟧); D13's radius semantics and accepted costs made explicit (quarter-to-
half width; blocked landing key; P/`Acc` boundary overshoot); Testing-section drift fixed
(row-count equality, exact-capital pins, QWERTY included in the switchSub check); the resize
claim softened to sub-DIP rounding.

## Context

EyeCtrl emits text as Unicode directly (`KeyboardViewModel.ActivateKey` → `KeyActivatedMessage`
→ `Win32InputSimulator.SendText` when no system modifier is armed); the OS keyboard layout is
not involved. Key rows render as centred StackPanels (`KeyboardWindow.xaml.cs:218`) inside a
`SizeToContent` window whose width is set by each layout's widest row (the 12.8-unit standard)
— so a row that GROWS shifts every key in it by half the growth ⟦I1⟧, and a resting gaze
after a sub-layout swap lands on whatever key is nearest in 2-D, which is not necessarily the
key at the same x ⟦R8⟧.

The shipped layouts already carry the common accents DIRECTLY: QWERTZ letters have
`ö ä ü ß` (capitals via `shiftOutput`); AZERTY letters have `é è ç à` — `ù` lives on AZERTY's
symbols sub-layout ⟦R2⟧.

Gaps: (1) the long tail — French accents on QWERTZ, everything beyond AZERTY's direct keys;
(2) AZERTY capitals (`É È À Ç`; `Ù` has no key). A dead-key engine was mockup-compared
against a per-layout accents sub-layout on 2026-09-24 and the owner chose the sub-layout (D1).

## Design

### 1. New `accents` sub-layout per language layout (data only)

Each supported layout gains a `subLayouts.accents` entry (same shape as `numbers`/`symbols`)
plus one new boolean sub-layout property, `autoReturn` (D5). `switchSub:accents` uses the
existing vocabulary.

**QWERTZ — French-in-German set (28 keys; 10/10/8 grid — the 1.28/1.2-unit keys are ~20 %
larger targets than a 12-wide row would allow, which matters for a gaze typist ⟦plan-r2 I1⟧):**

```
row 0:  é É è È ê Ê ë Ë à À             (10 × 1.28)
row 1:  â Â ç Ç î Î ï Ï ô Ô             (10 × 1.28)
row 2:  ù Ù û Û œ Œ « » [Bksp] [DelWd]  (8 × 1.2 + 2 × 1.6 = 12.8)
footer: ABC 123 #+= 😀 Fn Space Enter Tab      (no Ctrl — see D8)
```

**AZERTY — tail beyond its direct keys (20 keys ⟦M1⟧, 3 content rows ⟦plan H2⟧):**

```
row 0:  ê Ê â Â î Î ô Ô                  (8 × 1.6)
row 1:  û Û ë Ë ï Ï ù Ù                  (8 × 1.6)
row 2:  œ Œ « » [Bksp] [DelWd]           (4 × 1.6 + 2 × 3.2 = 12.8)
footer: ABC 123 #+= 😀 Fn Space Enter Tab
```

(`é è ç à` stay on AZERTY letters with capitals via D6; `ù Ù` join the tail because `ù` lives
on symbols today — symbols stays untouched ⟦R2⟧. `Œ` pairs with `œ` ⟦R11⟧; German quotes are
declined — D12.)

**Shape rules:** last content row carries `backspace` + `deleteWord` at its right end
(`ShippedLayoutFilesTests.EverySubLayout_CarriesBackspaceAndDeleteWord` requires both);
`Space`/`Enter` in the footer (shipped Space keys carry `"output": " "`); the widest row
equals the 12.8 standard so the window's width is stable across sub-layout swaps to within
sub-DIP rounding (the widened letters row 0 can differ from other 12.8-unit rows by under a
DIP after layout rounding — a ≤0.8-DIP resize per swap, harmless and unavoidable with
non-integer key widths) ⟦plan-r2⟧; NO padding/dummy
keys — width is made up by widening real keys (dummy keys would register as dwell targets
that do nothing ⟦M3⟧); **the row count EQUALS the letters sub-layout's row count** (both
layouts: 3 content rows + footer) so the bottom-docked `SizeToContent` window's height never
changes and no row shifts under the gaze ⟦plan H2⟧; every NON-SPECIAL accents key has
non-empty `output` ⟦M2⟧ and every lower-case key carries `shiftOutput` = its capital
⟦R6, D11⟧; explicit capital keys stay.

QWERTY gets no accents sub-layout (D4). Prediction bar, dwell chrome, window chrome unchanged.
Prediction behavior flows through `ActivateKey`/`LearnWord` as for `ö` today; `de.json` and
`fr.json` carry accented forms (`café`; `être côté sûr cœur œuvre`) (D9).

### 2. Entry keys — zero-movement placements ⟦I1, R8⟧

- **AZERTY (zero movement, no decision needed):** row 0 (`Win 1.2 + A..P` = 11.2 centred in
  the 12.8 container) becomes `Win 2.0 + A..P + Acc 0.8` = 12.8: the widened `Win` consumes
  the 0.8 left margin, `Acc` consumes the right margin, and every letter keeps its exact
  position.
- **QWERTZ (D16 as superseded by D17, owner-defaulted — plan review M4):** row 0 is the same
  11.2 shape (`Win 1.2 + Q..P`), so the identical trick applies: `Win 2.0 + Q..P + Acc 0.8`
  = 12.8, nothing moves, `Space` keeps its full width in the footer. (D16's footer placement
  — `Space` halved, `Acc` after `Tab` — was decided before this option was known; it is
  rejected in favor of layout consistency with AZERTY and an untouched `Space`.)
- **Landing spots ⟦R8, plan M2⟧:** made harmless by D13's spatial suppression while the gaze
  rests; the exact post-swap key under each `Acc` is pinned by the plan's probe tests
  (QWERTZ: accents row 0's right end; AZERTY likewise).

### 3. Auto-return + swap suppression (the engine changes)

`KeyboardLayout` parses `autoReturn`. The trigger, in `KeyboardViewModel`: after a **non-special
key activation** (a key carrying `output`, whatever modifiers are armed ⟦R10⟧) **or a
prediction selection** (`HandlePredictionSelected`, which emits text on its own path — without
this, a user who picks a prediction while on accents stays there and their next "letter" types
an accent ⟦I5, D14⟧) **while `!IsEmojiMode`** ⟦R1⟧ on an `autoReturn` sub-layout, switch to
`DefaultSubLayout` (letters). `specialAction` keys never trigger it. Emoji-mode safety was
re-verified by round 2: `IsEmojiMode` is set before the emoji surface is built and only
cleared through the flag, so the guard closes every path.

**Swap suppression — D13 (spatial; owner-defaulted from the options the plan review raised,
vetoable):** after ANY sub-layout swap — auto-return AND manual `switchSub` — while the gaze
has not moved beyond roughly half a key width (in practice a quarter to a half — the radius
is half the anchor's distance to its second-nearest OLD centre) of its PRE-SWAP position, no
key dwells. The radius is computed once at swap time from the OLD grid's still-valid centres
(post-swap centres are unarranged at the first samples and must never be consulted, plan
review B1); fewer than two positioned old centres means no suppression. The anchor is set
only when the gaze was actually on the key area at swap time — a prediction pick happens
with the gaze on the bar, so its auto-return swap suppresses nothing (plan review H3).
Samples outside the key area and during the post-activation cooldown do not probe the
radius (a glance at the typed text and back leaves the suppression armed). Hiding the
keyboard clears the suppression. KNOWN, accepted costs, ledgered: the landing key itself is
blocked too — typing the edge accent (`À` on QWERTZ, `Ô` on AZERTY) immediately after
dwelling `Acc` requires one gaze flick away and back; and a rightward overshoot at the
P/`Acc` boundary now opens accents instead of typing P (the boundary sits ~1.5 DIP inside
P). The same applies to EVERY sub-layout's same-slot switch pair (letters `123` ↔ numbers
`ABC` lead their footers at the same position): an immediate dwell of the back key at the
spot the swap happened under is suppressed until the gaze moves away — on ALL layouts,
including QWERTY, which receives this behavior with the branch but without accents. A
deliberate switch always involves looking at what was switched to first, and the resting
bounce-back D13 removes was precisely the ~850 ms unintended misfire; the residual cost is
the rare mistaken-switch-then-immediate-return costing one gaze flick. Comparison the
decision rests on (defaults: cooldown 400 ms, key dwell 450 ms):

| | next intended key (anywhere else) | resting gaze on the landing spot |
|---|---|---|
| today (no rule) | 850 ms | misfires at ~850 ms |
| time-based rule (rev 2's D10) | **1300 ms** (+450 ms after EVERY accent, no progress shown) | protected only ~1300 ms, then misfires |
| **D13 spatial (chosen)** | **850 ms (unchanged)** | **never misfires while resting; clears when the gaze moves ~half a key width away** |

D13 also covers the manual `Acc` entry exposure (plan review I2) and the same pre-existing
exposure on every shipped `switchSub` key. Known residual, declined: leaving emoji mode also
swaps the grid under a resting gaze and is NOT covered (pre-existing behavior, unchanged).

**Modifiers on entry — D15 ⟦I4⟧:** switching INTO an `autoReturn` sub-layout clears armed
one-shot SYSTEM modifiers (Ctrl/Alt/Win). Rationale: the accents footer has no modifier keys,
so an armed modifier is invisible there, and with the OS layout as a French keyboard a stray
Win plus `é` becomes `Win+2` (launches a taskbar app) — dropping Ctrl from the footer (D8)
does not close the Win/Alt paths. Shift/Caps Lock are NOT cleared (D11 needs them). On EVERY
swap, after the new grid is built, an `ActiveModifiers` change notification forces a
modifier-visuals repaint of the fresh keys — `RebuildKeys` draws static labels, so without
this an armed Shift/Caps would show lowercase labels that type capitals, and the letters Caps
key would draw inactive while Caps is on ⟦plan H1⟧.

Manual switching otherwise unchanged; hiding the keyboard already resets to `DefaultSubLayout`
(`KeyboardViewModel.cs:183`).

Typing `café` on QWERTZ: `c · a · f ·` dwell `Acc` `· é` → auto-return (landing key inert
until the gaze moves) → continue typing.

### 4. Data fixes riding along

- AZERTY letters: the FOUR accent keys gain `shiftOutput` (`é→É, è→È, à→À, ç→Ç`) (D6) ⟦R2⟧.
- QWERTZ: `ß` gains `shiftOutput: ẞ` (U+1E9E). D7 caveat stands: Caps-Lock typing yields
  `STRAẞE`, not T1's `STRASSE` ⟦R12⟧.

## Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|----------|-----------|----------|
| D1 | Per-layout accents sub-layout + auto-return (mockup-chosen) | ONE extra dwell per accent on every supported layout | Dead-key engine (2 extra dwells, pending-state UI — revisit path); long-dwell popups (mis-dwell risk) |
| D2 | QWERTZ set = 28 keys as listed | Covers German-text loanwords/names | Pan-European sets; German quotes (D12) |
| D3 | AZERTY entry = `Acc` on letters row 0, zero-movement placement (§2) | One extra dwell; nothing moves | Numbers/symbols-footer entry (2 dwells); footer resize (full) |
| D4 | QWERTY: no accents sub-layout | English target | Unused sub-layout |
| D5 | Auto-return = `autoReturn` property, non-special keys + prediction selections, `!IsEmojiMode` gate, target = layout default ⟦R1, R10, I5⟧ | Self-documenting; emoji never sees a grid swap; prediction select can't strand the user on accents | Magic id check; specialAction triggers; emoji-path returns |
| D6 | AZERTY shiftOutputs on its four letters accent keys | `É È À Ç` untypable today | — |
| D7 | `ß` → `ẞ` shiftOutput | Official capital since 2017. KNOWN EFFECT: Caps yields `STRAẞE` — veto here if unwanted | Leaving ẞ untypable |
| D8 | Accents footer = `ABC 123 #+= 😀 Fn Space Enter Tab` (no Ctrl) | Standard shape; Ctrl on accents desyncs `CurrentInput` via the VK path | Rev-1 mock shape |
| D9 | No prediction changes | Accented forms already in `de.json`/`fr.json` | Accent-stripping normalization |
| D10 | *(superseded by D13)* rev-2 time-based suppression | — | — |
| D11 | Lowercase accents carry `shiftOutput`; explicit capitals stay ⟦R6⟧ | Caps/Shift behave as on letters | Capitals only via Shift |
| D12 | German quotes `„ “` declined | Symbols-sub-layout concern, not accents | Including now |
| **D13** | **Spatial swap suppression on EVERY sub-layout swap** (auto-return + manual): while the gaze stays within ~half a key width (radius from the OLD grid's second-nearest centre) of its pre-swap position, nothing dwells; the anchor is only set when the gaze was on the key area; hide clears it ⟦plan B1, H3, M1, L5⟧ | See the table in §3: unchanged cadence for intended keys, unbounded protection for a resting gaze, no post-swap key resolution (immune to unarranged centres and boundary flicker) | Key-identity clearing (flickers at key boundaries — plan M1's measurement); D10 time-based rule (slows every post-accent key by 450 ms); doing nothing |
| **D14** | **Prediction selection auto-returns** from an `autoReturn` sub-layout ⟦I5⟧ | Otherwise the next "letter" after a picked word types an accent | Hiding the prediction bar on accents (loses function); leaving as-is |
| **D15** | **Entering accents clears armed Ctrl/Alt/Win one-shots** (Shift/Caps preserved) ⟦I4⟧ | Accents has no modifier keys to show or clear them; stray Win+`é` = `Win+2` on a French OS layout | Keeping them armed invisibly; re-adding Ctrl to the footer |
| **D16** | *(superseded by D17 — the plan review found row 0 was never considered; the footer-only premise was wrong)* | — | — |
| **D17** | **QWERTZ `Acc` = row-0 zero-move** (`Win` 1.2→2.0, `Acc` 0.8 after `P`; footer and `Space` untouched) — owner-defaulted, vetoable ⟦plan M4⟧ | Same verified trick as AZERTY, nothing moves, `Space` (the most-dwelled key for a fixation-hard user) keeps its width, both layouts consistent | D16's footer placement (`Space` halved) |

## Testing ⟦R3, R9, I6⟧

- **ShippedLayoutFilesTests (App.Tests, real JSON):** accents present on QWERTZ/AZERTY; every
  NON-SPECIAL accents key has non-empty `output` ⟦M2⟧; lowercase keys carry their capital
  `shiftOutput` — **pinned against the JSON data itself with the EXACT expected capital**
  (`é`'s shiftOutput is `"É"` in the file — a wrong-but-non-empty capital fails), not via
  view-model echo, so deleting or corrupting the data fails the suite ⟦I6, plan-r2⟧; widest
  accents row = 12.8; **accents row count EQUALS the letters sub-layout's row count**
  ⟦plan H2⟧; QWERTZ `ß` → `ẞ` and AZERTY's four shiftOutputs pinned the same way ⟦I6⟧; the
  `switchSub:`-targets-exist check runs over ALL THREE shipped files including QWERTY
  ⟦plan-r2⟧; existing every-sub-layout Bksp/DelWd and special-action tests cover accents
  automatically.
- **Loader tests (Core.Tests, inline JSON):** `autoReturn` round-trips, default false.
- **ViewModels.Tests:** auto-return after a non-special key and after
  `HandlePredictionSelected` ⟦D14⟧; not after backspace/deleteWord/space/switch keys; not
  while `IsEmojiMode`; target is `DefaultSubLayout` not "previous"; D13's landing suppression
  (the pre-swap nearest key cannot start dwelling until the gaze moves — via the existing
  nearest-key test seams); D15's modifier clearing (Ctrl/Alt/Win cleared on entry, Shift/Caps
  kept). Mutation checks: remove the `!IsEmojiMode` guard → emoji test fails; remove the
  prediction trigger → D14 test fails; remove the D13 rule → landing test fails; swap the
  target → sequence test fails.
- **Real window (App.Tests STA):** shipped-QWERTZ round trip at scale 2.0: letters → `Acc` →
  `é` → back on letters, plus the emoji-entered-from-accents case. **The fixture must move the
  gaze position between the accent activation and the next dwell** — the probe harness uses
  cooldown 0 and re-dwells ~1 ms later, which lands inside any suppression window and would
  fail for the wrong reason ⟦I6⟧.

## Out of scope

Dead-key composition engine (D1 revisit path) · QWERTY accents (D4) · more languages / CJK/IME
· settings UI · prediction changes · symbols-sub-layout redesign (D12).
