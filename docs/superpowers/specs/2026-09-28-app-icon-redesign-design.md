# App Icon Redesign — Design

Date: 2026-09-28
Status: awaiting owner review
Session: visual brainstorm via companion (motif rounds A–D → H1–H3 → C1–C3 → E1–E3 → F1/F2 → V1–V4 → W1–W3 → final)

## Goal

Replace `src/EyeCtrl.App/Assets/eyectrl.ico` (white eye on a generic blue disc) with a
distinctive icon in the app's own visual language: dark Midnight surface tile, cyan accents,
and an iris that tells the app's signature story — a dwell in progress, ¾ of the way to a click.

Owner-approved direction: filled almond body + amber pie iris + subtle dwell arc (W1), with a
more opaque body gradient.

## Where the icon is consumed (no code changes needed)

| Consumer | Reference |
|---|---|
| Exe/taskbar/Explorer icon | `<ApplicationIcon>Assets\eyectrl.ico</ApplicationIcon>` in `EyeCtrl.App.csproj` |
| Embedded WPF resource | `<Resource Include="Assets\eyectrl.ico" />` in `EyeCtrl.App.csproj` |
| System tray | `TrayIconManager` loads `pack://application:,,,/Assets/eyectrl.ico` |
| Splash window | `SplashWindow.xaml` `<Image Source="/Assets/eyectrl.ico" ...>` |

All four consume the same file, so this is an asset-only change: swap `eyectrl.ico`,
rebuild, done. The splash `<Image>` renders the largest frame; it already works with
multi-frame ICOs via WPF's ICO decoder.

**Out of scope (deliberate):** `EyeCtrl.Supervisor`'s tray uses `SystemIcons.Application` —
a generic identity for a watchdog process, chosen independently. Not touched.

## Design specification (single master, 96×96 design grid)

Layer order matters; listed back to front.

1. **Tile** — rounded rect `x=4 y=4 w=88 h=88 rx=22`, fill = radial gradient
   `#252B4A` (center 50%,38%, r 85%) → `#141828`.
   Rim: same rect inset 1.5 (`x=5.5 y=5.5 w=85 h=85 rx=20.5`), stroke `#4A5578` at 55% opacity,
   width 2, no fill.
2. **Glow** — ellipse center (48,47), rx 30, ry 21, fill `#A5F3FC`, opacity 0.10,
   Gaussian blur σ=4.
3. **Body (almond)** — path
   `M12,48 C26,26 70,26 84,48 C70,70 26,70 12,48 Z`,
   fill = linear gradient (userSpace, y 25→71): `#AFF0FF` → `#4A7ECF`.
   Owner asked for "a bit more opacity" here — this gradient is the punchier revision
   (earlier drafts used `#9FE9FB` → `#4F7BC2`).
4. **Iris track** — circle center (48,48), r 13.5, fill `#141828`, hairline stroke `#0A0A14` width 1.
5. **Whisper arc** — the subtle dwell arc:
   `M59.5,48 A11.5,11.5 0 0 0 48,36.5`, stroke `#A5F3FC`, opacity 0.55, width 2, round caps.
   It traces the still-unfilled quarter inside the track (from 3 o'clock counter-clockwise to 12).
6. **Pie fill** — `M48,48 L48,35.5 A12.5,12.5 0 1 0 60.5,48 Z`,
   fill = linear gradient (userSpace, from (60,48) toward (36,44)): `#F6C77E` → `#E8A33C`.
   Reads as a dwell ¾ complete: filled from 12 o'clock counter-clockwise (through 9, 6) ending at 3 o'clock.
7. **Gaze dot** — circle center (60.5,48), r 4.5, fill `#F7FCFF` — riding the fill head.

### Colors and their sources

| Hex | Role | Source |
|---|---|---|
| `#252B4A` / `#141828` | tile gradient | derived from Midnight `SurfaceKey #2A2A3E` / `StrokeHalo #0A0A14` |
| `#4A5578` | tile rim | Midnight `SurfaceKeyModifier #3E3E64` family |
| `#A5F3FC` | whisper arc, glow | Midnight `Accent` |
| `#AFF0FF` → `#4A7ECF` | body gradient | brightened `Accent` → deep blue (Midnight `SurfaceKeyActive #5278B8` family) |
| `#F6C77E` → `#E8A33C` | pie gradient | Midnight `AccentWarning #E8A33C` (+ lighter head) |
| `#141828` / `#0A0A14` | iris track, hairline | `StrokeHalo` family |
| `#F7FCFF` | gaze dot | near-white cyan |

All hues are the app's own palette (cyan accent family + the amber warning accent);
no foreign colors introduced.

## Frame plan (6 frames in the .ico)

| Size | Treatment |
|---|---|
| 256 | master spec rendered directly |
| 128 | master spec rendered directly |
| 64 | master spec rendered directly |
| 48 | master spec rendered directly |
| 32 | tuned: tile flat gradient (no rim), no glow, no hairline; iris r14; whisper arc width 3 @ 60% opacity, `M60,48 A12,12 0 0 0 48,36`; pie r13 (`M48,48 L48,35 A13,13 0 1 0 61,48 Z`); dot (61,48) r5 |
| 16 | tuned: tile flat `#1A2038`; body gradient kept; iris r15.5 (track ring effectively invisible — intended); pie solid `#E8A33C` r14.5 (`M48,48 L48,33.5 A14.5,14.5 0 1 0 62.5,48 Z`); dot (62.5,48) r6; no whisper arc, no rim, no glow |

Rationale: the 16 px tray frame cannot carry the rim, glow, hairline, or whisper arc — they
degrade to noise. The tuned frames keep: tile, almond, dark iris, amber pie, white dot.

## Build approach

No rasterizer is available on the dev box (no ImageMagick, Inkscape, or PIL), and icon work
should be reproducible. Build a small console tool:

- **`tools/EyeCtrl.IconBuilder/`** — .NET 10 console project (not added to the solution, same
  pattern as `benchmarks/`). WPF (`UseWPF=true`, STA) so it can rasterize `DrawingVisual`s via
  `RenderTargetBitmap` — the geometry is authored as the same path-data strings the spec uses,
  mirroring the repo's `IconGeometry` idiom.
- Emits `src/EyeCtrl.App/Assets/eyectrl.ico`: classic BMP (BGRA, bottom-up, with AND mask)
  frames for 16/32/48/64/128 + a PNG-compressed 256 frame (the standard Vista+ layout; the
  256-as-PNG convention keeps the file small).
- Deterministic output (no timestamps) so re-runs diff cleanly.
- The master's Gaussian blur (σ=4) is approximated with WPF's `BlurEffect` (radius ≈ 8);
  acceptance is visual similarity to the approved mockup, not bit-exactness.
- Committed alongside the regenerated `.ico` so the asset can be rebuilt when the palette or
  framing changes.

## Acceptance criteria

1. `tools/EyeCtrl.IconBuilder` regenerates an `eyectrl.ico` containing all six frames at the
   exact sizes 16/32/48/64/128/256.
2. Visual check on the live box: Explorer file icon, taskbar (running app), tray, and splash
   window all show the new icon; nothing shows the old blue disc.
3. Frames 16 and 32 read cleanly at 100% zoom (tile + eye + amber iris + dot distinguishable).
4. `dotnet build EyeCtrl.slnx` succeeds; no test expectations reference the old icon's bytes
   (asset-only change; the existing suites are unaffected).
5. Committed: new `eyectrl.ico`, the builder project, and this spec.

## Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| L1 | Rounded-square tile, not a disc | Win11 icon idiom; crisper at small sizes; distance from the old disc | Keeping the disc (too close to the old icon) |
| L2 | Dark tile (F1), not the cyan-flip tile (F2) | matches the app's dark identity across all three themes | F2 Cyan Poster (parts from dark identity) |
| L3 | Amber pie iris (`AccentWarning` hue) | warm focal point on cool body; palette-native | cyan pie (insufficient separation from the body) |
| L4 | Pie reads ¾ filled, CCW from 12 o'clock | mirrors the app's dwell fill toward a click | full pie (loses "in progress"); ring-only (weaker story) |
| L5 | Gaze dot rides the fill head, not the centre | motion; owner chose V3 lineage (head dot) over V2 | centre dot ("Fixed Gaze") |
| L6 | Whisper arc inside the track (W1) | owner's pick among W1/W2/W3 | ghost ring (W2), outer halo (W3) |
| L7 | 16/32 px frames hand-tuned, not naive downscale | rim/glow/hairline/arc degrade to noise below 48 px | single-geometry scaling for all frames |
| L8 | Builder = small C# WPF console tool | PIL/ImageMagick unavailable; WPF rasterizes path data natively; repo-consistent idiom | Python/Pillow pipeline, one-off hand-pixelled asset |
| L9 | Supervisor tray icon untouched | its generic `SystemIcons.Application` identity is deliberate | giving the supervisor the family icon |
