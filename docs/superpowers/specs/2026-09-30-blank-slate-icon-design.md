# Blank-Slate App Icon — Design (C1 "Amber Gaze")

Date: 2026-09-30
Status: implemented; owner accepted the rim-tight design for now (2026-09-30, "keep this for now")
Supersedes: the design sections of `2026-09-28-app-icon-redesign-design.md` (pie-iris eye).
Carried over unchanged from that spec: the L10 tray-loader change (`LoadTrayIcon` +
`SmallIconSize`), the ICO container recipe (ascending 16→256, BMP 16–128 with `biHeight = 2·h`
+ bottom-up AND mask, PNG 256 entry 0/0, Bgra32 straight-alpha conversion), A1–A4 acceptance
structure, and the headless-verification rules (explicit `System.Drawing.Size`, never
`EYECTRL_UI_TESTS`, never launch the app).

## Why the restart

The owner rejected the rendered pie-iris eye live ("I don't like the new icon"), then rejected
the improvised tray-bloom compensation ("deviates too much from what I agreed to") and called a
blank-slate restart. Root lesson recorded in the ledger: the 09-28 design was approved in a
browser at ~2× the tray's physical resolution; whatever is approved at that scale does not
survive at 32 device pixels.

## Process rule (binding for this design)

**Tray-first judging.** Every candidate is presented at the tray's true physical size (a
32-device-pixel icon = 16 CSS px on the 200 % development screen) inside a mock taskbar with
neighbour icons. Large renders are reference only. The owner picked concept "A — Amber Gaze"
from such a strip, then directed the additions (dwell arc ≤ full ring → settled at 120° from
12 o'clock; cyan colouring; dot bloom) through the same strip-first loop, choosing variant C1.

## Design specification (single master, 96×96 grid, one geometry for all sizes)

Layer order, back to front:

1. **Tile** — rounded rect `0 0 96 96 rx=24`, flat fill `#141828`, **full-bleed** (L23: other
   tray icons draw edge-to-edge; the old 4-unit transparent border made the whole icon look
   smaller than its neighbours). No rim, no gradient.
2. **Bloom** — radial gradient disc centred on the gaze point (48,48), radius 44, amber
   `#E8A33C` with stops: offset 0 → α 0.38, offset 0.55 → α 0.22, offset 1 → 0.
   Peak is 38 % — deliberately stronger than the old tile-glow's 10 %, because 32 px eats
   subtlety (measured: 10 % was invisible at tray size).
3. **Trail** — one fading motion dot toward the upper-left corner: (12,13) r 7.5 α 0.5, amber.
   (Two faint dots were invisible at tray size — one bold dot carries the motion.)
4. **Dwell arc** — cyan `#A5F3FC`, radius 41.5 centred on the dot, width 11, α 0.85, round caps;
   **starts at 12 o'clock, sweeps 120° clockwise** — the same start and direction as the real
   `DwellArcButton` (`UpdateArc`, DwellArcButton.cs:349-358). Endpoints (48,6.5) → (84,68.25);
   a ~1-unit rim margin at the arc's widest (L24).
5. **Gaze point** — amber disc, centre (48,48), r 28 — 58 % of the tile width. The history:
   r 14 at (54,54) → 1.35× content scale (L19) → r 26 (L20) → r 30 (L21) → r 32 at the rim
   (L22, "a bit big") → r 28 on the full-bleed tile (L23).

Rendered via `StreamGeometry.ArcTo` (no path strings — no locale formatting traps).

## Frame plan

One geometry, rendered at 16/32/48/64/128/256. No per-frame tuning: the composition is bold
masses by design, and the L13 episode showed per-frame "improvements" drift from the approved
look. If a size ever needs special-casing, that is a new owner decision, not a builder default.

## Acceptance criteria

A1–A3, A4b, A5, A6 as in the 09-28 spec (determinism, container layout, splash 128 px pin,
loader pin, preview strips, live swap). **A4a probes rewritten for this design** (sample-centre
math: 32 px = 96-grid/3):

- 32 px: dark tile (4,4); amber dot (18,18); cyan arc (18,11); sizes asserted.
- 16 px: dark tile (2,2); amber dot (9,9).
- 128 px: warm bloom (90,90) — the bloom lift is ~6 R-points and sits against the dot's AA
  fringe at small sizes, where GDI+ rounding swamps it (measured 49,49); at 128 px the band is
  wide and clean, so the bloom is probed there instead.
- The old pie/body probes were deleted with the old design.

## Decision ledger (owner-vetoable; continues the 09-28 numbering)

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| L14 | Blank-slate restart; pie-iris eye retired | owner verdict after the live render: the approved-in-browser design did not survive at real sizes | iterating the old design with fidelity fixes (tried; owner: "start over") |
| L15 | Tray-first judging is binding for every round | the 09-28 approval was made at ~2× tray resolution — the failure mode of the whole first design | approving from large renders |
| L16 | Single geometry for all six frames | the composition is bold masses by design; per-frame tuning caused the L13 deviation episode | per-frame tuned variants (L7/L13 lineage — retired) |
| L17 | Concept = amber gaze point + trail + cyan 120° arc from 12 o'clock on near-black tile | owner's pick from the blank-slate strip, then directed arc angle/colour/glow through strip rounds | eye concepts, letter monogram, cursor concepts (same spread) |
| L18 | Bloom peak 38 %, probed at 128 px only | 10 % invisible at tray size (measured); 32/16 px probes sit in AA fringes where GDI+ rounding masks the signal | probing bloom at 32 px |
| L13 | (struck) tray bloom at 22 % | vetoed by the owner — deviated from the agreed design; see 09-28 spec ledger | — |
| L19 | (superseded) content scale 1.35× | first answer to "too small"; still too small at tray size — the proportions were the problem, not the scale | more scaling (arc/trail already at tile edges) |
| L20 | Tray-bold proportions: dot r 26 (~54 % of tile width) at (49,52), arc r 35 w 8, one trail dot r 7, bloom r 38; geometry authored at final size, no content scaling | second "too small" verdict; scaling was maxed (arc/trail at tile edges), so the internal proportions had to change | keeping the 1.35-scaled thin look; global scale above 1.35 (clips trail past the tile) |
| L21 | Margins tightened: dot r 30 (~68 % of tile width) at (44.5,52), arc r 38 w 9, trail (16.5,23.5) | third verdict, "the margin is too large" — ~5-unit borders all around; the arc's top now sits ~1.5 units off the tile edge by design | uniform margin ≥ 8 (that was the complaint) |
| L22 | Outer margin tightened to the rim: dot r 32 (~73 % of tile width) centred (47.5,49.5), arc r 39.5 w 9 (~1.5 units off the rim), trail into the corner; bloom probe switched to a relative lift (bloom pixel vs pure-tile pixel, +10 R) | fourth verdict, "the outer margin" — the glyph now nearly touches the rim; the absolute bloom lift at 128 px is a few R-points, so the relative form is rounding-proof | keeping any visible outer band |
| L23 | Full-bleed tile (0 0 96 96 rx 24) — no transparent border; dot eased to r 28 on the bigger canvas | fifth verdict pair: "the gaze dot is a bit big" + "other tray icons do not have that big margin and fill the available space" — the 4-unit transparent border was the real space-waster; the full-bleed tile makes every element effectively larger while the dot shrinks a step | keeping the transparent border; r 32 on the full-bleed tile (would re-overshoot) |
| L24 | Outer margin to ~1 unit: arc r 41.5 w 11, trail (12,13), bloom r 44 | sixth verdict, "can we decrease the outer margin further" — the composition now touches the rim at the arc's widest point | a visible outer band (retired by L21-L24 progression) |
