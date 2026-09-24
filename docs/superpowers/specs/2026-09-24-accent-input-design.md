# Accent Input — Design (spec revision 3)

**Date:** 2026-09-24 · **Branch:** `feat/accent-input` (from main `2e1d9078`)
**Competitive gap:** #11 "Dead-key / diacritic composition" in
`docs/COMPETITIVE_FEATURE_ANALYSIS.md` — closed by a different mechanism than its title
suggests (D1).
**Rev 3:** external round 2 folded (verdict FIX-THEN-SHIP: R8 re-opened, I1–I6, M1–M8;
all verified against code — dispositions inline, marked ⟦Rn⟧/⟦In⟧/⟦Mn⟧). **D13–D16 are new
owner decisions** (round 2's explicit asks).

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

**QWERTZ — French-in-German set (28 keys):**

```
row 0:  é É è È ê Ê ë Ë à À â Â
row 1:  ç Ç î Î ï Ï ô Ô ù Ù û Û
row 2:  œ Œ « » [Bksp] [DelWd]            (Bksp/DelWd widened to reach 12.8 — no filler keys ⟦M3⟧)
footer: ABC 123 #+= 😀 Fn Space Enter Tab   (no Ctrl — D8/D15)
```

**AZERTY — tail beyond its direct keys (20 keys ⟦M1⟧):**

```
row 0:  ê Ê â Â î Î ô Ô û Û
row 1:  ë Ë ï Ï ù Ù œ Œ « » [Bksp] [DelWd]   (Bksp/DelWd widened to reach 12.8 ⟦M3⟧)
footer: ABC 123 #+= 😀 Fn Space Enter Tab
```

(`é è ç à` stay on AZERTY letters with capitals via D6; `ù Ù` join the tail because `ù` lives
on symbols today — symbols stays untouched ⟦R2⟧. `Œ` pairs with `œ` ⟦R11⟧; German quotes are
declined — D12.)

**Shape rules:** last content row carries `backspace` + `deleteWord` at its right end
(`ShippedLayoutFilesTests.EverySubLayout_CarriesBackspaceAndDeleteWord` requires both);
`Space`/`Enter` in the footer; the widest row equals the 12.8 standard so the window never
resizes between sub-layouts; NO padding/dummy keys — width is made up by widening real keys
(dummy keys would register as dwell targets that do nothing ⟦M3⟧); content-row count ≤ 3 plus
one footer row, matching the letters height so the window height never changes either ⟦M4⟧;
every NON-SPECIAL accents key has non-empty `output` ⟦M2⟧ and every lower-case key carries
`shiftOutput` = its capital ⟦R6, D11⟧; explicit capital keys stay.

QWERTY gets no accents sub-layout (D4). Prediction bar, dwell chrome, window chrome unchanged.
Prediction behavior flows through `ActivateKey`/`LearnWord` as for `ö` today; `de.json` and
`fr.json` carry accented forms (`café`; `être côté sûr cœur œuvre`) (D9).

### 2. Entry keys — zero-movement placements ⟦I1, R8⟧

- **AZERTY (zero movement, no decision needed):** row 0 (`Win 1.2 + A..P` = 11.2 centred in
  the 12.8 container) becomes `Win 2.0 + A..P + Acc 0.8` = 12.8: the widened `Win` consumes
  the 0.8 left margin, `Acc` consumes the right margin, and every letter keeps its exact
  position ⟦I1's verified trick⟧.
- **QWERTZ (D16, owner decision):** the letters footer (11.7 of 12.8) has no split that fits
  a 1.0 key without movement. **Recommended:** keep the footer total at 11.7 by narrowing
  `Space` 2.0 → 1.0 and appending `Acc 1.0` at the far right (after `Tab`) — nothing moves;
  `Space` becomes letter-key width. **Alternative:** insert `Acc 1.0` between `Fn` and
  `Space` as mocked — every footer key shifts 0.5 units left, `Space` keeps its width.
- **Landing keys ⟦R8⟧ (2-D nearest-centre after a swap, not x-range):** QWERTZ (recommended
  placement) — the content row's `Bksp`/`DelWd` region; AZERTY — `Acc` sits at a row-0 end,
  so the landing key is accents row 0's right end (`Û`); QWERTZ alternative placement —
  accents-footer `Space`, with `Bksp` winning once the gaze drifts 0.29 rows up. All of these
  are made harmless by D13; the spec names them so the plan can pin the exact geometry.

### 3. Auto-return + swap suppression (the engine changes)

`KeyboardLayout` parses `autoReturn`. The trigger, in `KeyboardViewModel`: after a **non-special
key activation** (a key carrying `output`, whatever modifiers are armed ⟦R10⟧) **or a
prediction selection** (`HandlePredictionSelected`, which emits text on its own path — without
this, a user who picks a prediction while on accents stays there and their next "letter" types
an accent ⟦I5, D14⟧) **while `!IsEmojiMode`** ⟦R1⟧ on an `autoReturn` sub-layout, switch to
`DefaultSubLayout` (letters). `specialAction` keys never trigger it. Emoji-mode safety was
re-verified by round 2: `IsEmojiMode` is set before the emoji surface is built and only
cleared through the flag, so the guard closes every path.

**Swap suppression — D13 (owner decision, replaces D10-as-rev-2 ⟦I2, I3, R8⟧):** after ANY
sub-layout swap — auto-return AND manual `switchSub` — the key nearest the pre-swap gaze
position cannot START dwelling until the gaze leaves it (position-based "leave the landing
key" rule). Everything else dwells normally. Comparison the decision rests on
(defaults: cooldown 400 ms, key dwell 450 ms):

| | next intended key (anywhere else) | resting gaze on the landing key |
|---|---|---|
| today (no rule) | 850 ms | misfires at ~850 ms |
| D10 as rev 2 (time-based: suppress all keys cooldown + 1 dwell) | **1300 ms** (+450 ms after EVERY accent, no progress shown) | protected only ~1300 ms, then misfires |
| **D13 (position-based, recommended)** | **850 ms (unchanged)** | **never misfires while resting** |

D13 also covers the round-2 finding that manual `Acc` entry otherwise fires the landing key
after 850 ms (QWERTZ: a space that saves the half-typed word; AZERTY: `Û`) ⟦I2⟧, and the same
pre-existing exposure on every shipped `switchSub` key. Cost: after a deliberate switch, a
user who wants to dwell the exact spot they're resting on must flick the gaze once. Manual
switches keep today's 850 ms cadence everywhere else. Round 2 confirmed the cooldown is
VM-side (`_keyCooldownEndTimestamp`, `KeyboardViewModel.cs:22/213/265`), so either rule is
implementable there; D13 needs the window's nearest-key tracking, which exists.

**Modifiers on entry — D15 ⟦I4⟧:** switching INTO an `autoReturn` sub-layout clears armed
one-shot SYSTEM modifiers (Ctrl/Alt/Win). Rationale: the accents footer has no modifier keys,
so an armed modifier is invisible there, and with the OS layout as a French keyboard a stray
Win plus `é` becomes `Win+2` (launches a taskbar app) — dropping Ctrl from the footer (D8)
does not close the Win/Alt paths. Shift/Caps Lock are NOT cleared (D11 needs them). The swap
must apply modifier visuals after clearing so the Caps indicator and key labels don't go
stale ⟦M7⟧.

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
| **D13** | **Position-based swap suppression on EVERY sub-layout swap** (auto-return + manual): the key nearest the pre-swap gaze can't start dwelling until the gaze leaves it ⟦I2, I3, R8⟧ | See the table in §3: unchanged cadence for intended keys, unbounded protection for resting gaze; also closes the pre-existing manual-switch exposure | D10 time-based rule (slows every post-accent key by 450 ms, bounded protection); doing nothing (850 ms misfires on a mis-dwell-prone owner) |
| **D14** | **Prediction selection auto-returns** from an `autoReturn` sub-layout ⟦I5⟧ | Otherwise the next "letter" after a picked word types an accent | Hiding the prediction bar on accents (loses function); leaving as-is |
| **D15** | **Entering accents clears armed Ctrl/Alt/Win one-shots** (Shift/Caps preserved) ⟦I4⟧ | Accents has no modifier keys to show or clear them; stray Win+`é` = `Win+2` on a French OS layout | Keeping them armed invisibly; re-adding Ctrl to the footer |
| **D16** | **QWERTZ `Acc` placement: zero-move — `Space` 2.0→1.0, `Acc` appended after `Tab`** ⟦I1⟧ | Nothing on the footer moves (muscle memory); landing key (Bksp/DelWd region) is inert under D13; `Space` stays letter-width | Inserting `Acc` between Fn and Space as mocked (all footer keys shift 0.5 units; `Space` keeps width) — say so to override |

## Testing ⟦R3, R9, I6⟧

- **ShippedLayoutFilesTests (App.Tests, real JSON):** accents present on QWERTZ/AZERTY; every
  NON-SPECIAL accents key has non-empty `output` ⟦M2⟧; lowercase keys carry their capital
  `shiftOutput` — **pinned against the JSON data itself** (`é`'s shiftOutput is `"É"` in the
  file), not via view-model echo, so deleting the data fails the suite ⟦I6⟧; widest accents
  row = 12.8; content-row count ≤ 3 + footer ⟦M4⟧; QWERTZ `ß` → `ẞ` and AZERTY's four
  shiftOutputs pinned the same way ⟦I6⟧; existing every-sub-layout Bksp/DelWd and
  special-action tests cover accents automatically.
- **Loader tests (Core.Tests, inline JSON):** `autoReturn` round-trips, default false; every
  `switchSub:` target names a sub-layout that exists in the same layout ⟦M5⟧.
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
