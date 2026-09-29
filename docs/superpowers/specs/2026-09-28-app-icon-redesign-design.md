# App Icon Redesign — Design

Date: 2026-09-28
Status: revised after external review r1 (merged Opus+Muse, CHANGES-REQUIRED — all 12 findings applied; tray decision taken as option (a), ledger L10)
Session: visual brainstorm via companion (motif rounds A–D → H1–H3 → C1–C3 → E1–E3 → F1/F2 → V1–V4 → W1–W3 → final)

## Goal

Replace `src/EyeCtrl.App/Assets/eyectrl.ico` (white eye on a generic blue disc) with a
distinctive icon in the app's own visual language: dark Midnight surface tile, cyan accents,
and an iris that tells the app's signature story — a dwell in progress, ¾ of the way to a click.

Owner-approved direction: filled almond body + amber pie iris + subtle dwell arc (W1), with a
more opaque body gradient.

## Scope: asset change + one code change

The review (r1, Finding 1) falsified the original "asset-only" premise: the tray loader picks
the large-icon frame, so the tuned small frames never reached the tray. The owner decision
(ledger L10) is to fix the loader rather than lower the design.

| Consumer | Reference | Change |
|---|---|---|
| Exe/Explorer/pinned-shortcut icon | `<ApplicationIcon>Assets\eyectrl.ico</ApplicationIcon>` in `EyeCtrl.App.csproj:71` | none (file swap) |
| Embedded WPF resource | `<Resource Include="Assets\eyectrl.ico" />` in `EyeCtrl.App.csproj:61` | none |
| System tray | `TrayIconManager.cs:85` — `_customIcon = new Icon(memoryStream);` | **one line:** `new Icon(memoryStream, SystemInformation.SmallIconSize)` so the tray request maps to the tuned small frame (32 px at 200 % DPI, 16 px at 100 %) instead of the large-icon frame (64 px at 200 %) that the shell then shrinks |
| Splash window | `SplashWindow.xaml:10-12` — 64×64 `<Image Source="/Assets/eyectrl.ico">` | none, but see splash note below |

**Splash frame selection (measured, r1):** WPF's `ImageSourceConverter` returns WIC's
first-enumerated frame, and WIC reorders frames based on the BMP/PNG mix, ignoring directory
order. With this spec's layout (BMP 16–128, PNG 256) the first-enumerated frame is **128**,
which the splash shows at 64×64. That is the intended outcome, but it must be pinned by a
headless test (acceptance A3) rather than assumed — today's all-PNG icon enumerates 16 first,
so the current splash actually shows a blown-up 16 px frame.

**No other consumers:** no installer, publish profile, CI step, or script references the
`.ico`; no test references its bytes; the Supervisor tray deliberately uses
`SystemIcons.Application` (`SupervisorTrayIcon.cs:55`) — out of scope (L9).

Note: the running app shows no taskbar button, title-bar icon, or Alt-Tab entry — every
overlay window carries `WS_EX_TOOLWINDOW` (`OverlayWindow.cs:76`, `:113`). Taskbar/title-bar
visibility is therefore not a verification surface; use the pinned taskbar/Start shortcut if one exists.

## Design specification (single master, 96×96 design grid)

Layer order matters; listed back to front.

1. **Tile** — rounded rect `x=4 y=4 w=88 h=88 rx=22`, fill = radial gradient
   `#252B4A` (center 50%,38%, r 85%) → `#141828`.
   Rim: same rect inset 1.5 (`x=5.5 y=5.5 w=85 h=85 rx=20.5`), stroke `#4A5578` at 55% opacity,
   width 2, no fill.
2. **Glow** — soft cyan bloom centred (48,47): full-strength ellipse rx 30, ry 21 peaking at
   10 % opacity `#A5F3FC`, fading Gaussian-style to nothing ~12 units (3σ) past the disc edge.
   **Mechanism (amended in execution, 2026-09-29):** shipped as a **multi-stop radial gradient**
   (plateau 0.102 → disc edge 0.085 at 0.714 → 0 at 1.0, drawn at rx 42 / ry 33) — the earlier
   `BlurEffect Radius = 12` prescription does not survive rendering: WPF effects do not render
   under `SoftwareOnly` (A1's determinism requirement), and the first implementation also
   composited the glow *beneath* the opaque tile, hiding it completely. Both defects were
   verified by pixel-ramp (green-channel lift above/below the almond) and fixed in the builder;
   the visual intent — the mockup's `feGaussianBlur` halo — is unchanged.
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
   fill = linear gradient (userSpace, StartPoint (60,48) = `#F6C77E` → EndPoint (36,44) = `#E8A33C`).
   Reads as a dwell ¾ complete: filled from 12 o'clock counter-clockwise (through 9, 6) ending at 3 o'clock.
   Direction note: the real `DwellArcButton` sweeps **clockwise** from 12
   (`UpdateArc`, DwellArcButton.cs:349-358 — 12 → 3 → 6 → 9; start point comes from
   `PathFigure.StartPoint`, :209/:231; same pattern in `ActivationButtonWindow.xaml:19-35`).
   The icon's pie is deliberately the mirror of that (CCW) so the fill head and gaze dot land
   on the right for rightward reading. Same ¾-to-a-click story, opposite rotation —
   intentional, not an error.
7. **Gaze dot** — circle center (60.5,48), r 4.5, fill `#F7FCFF` — riding the fill head.

### WPF gradient mapping (r1 Finding 8)

- Tile: `RadialGradientBrush` with `Center` = `GradientOrigin` = (0.5, 0.38) and
  `RadiusX` = `RadiusY` = 0.85 (the brush has no single "radius" property). The origin
  defaults differ: WPF's `GradientOrigin` defaults to (0.5,0.5) regardless of `Center`,
  while SVG's fx/fy default to cx/cy — set both explicitly.
- Body and pie: `MappingMode = BrushMappingMode.Absolute` (XAML `MappingMode="Absolute"`;
  userSpace-on-use — the enum is `BrushMappingMode`; `GradientMappingMode` does not exist
  in WPF and code copied from the earlier wording would not compile).
- Interpolation: sRGB — WPF's default already matches SVG.
- Blur: see layer 2 (Radius 12, own visual).

### Colors and their sources

Sourcing is stated honestly (r1 Finding 6): only three hexes are exact Midnight tokens; the
rest are hand-tuned values chosen to sit near one. The builder's inputs are hard-coded hexes —
a future palette change requires manually re-deriving them against `tools/palette_validate.py`
(and `MidnightTheme.cs`); nothing automates that.

| Hex | Role | Sourcing |
|---|---|---|
| `#A5F3FC` | whisper arc, glow | **exact** Midnight `Accent` |
| `#E8A33C` | pie gradient tail | **exact** Midnight `AccentWarning` |
| `#0A0A14` | iris hairline | **exact** Midnight `StrokeHalo` |
| `#252B4A` | tile gradient (light stop) | hand-tuned; perceptually nearest `SurfaceMedium` (ΔE00 2.7; `SurfaceKey #2A2A3E` next at 4.6) |
| `#141828` | tile gradient (dark stop), iris track fill | hand-tuned; perceptually nearest `_core #1A1A2E` (ΔE00 3.3) |
| `#4A5578` | tile rim | hand-tuned; perceptually nearest `SurfaceKeySwitch #38527F` (ΔE00 4.9); hue- and role-adjacent to `BorderSubtle #616BA0` — an earlier "nearest BorderSubtle" claim was false by the repo's own CIEDE2000 metric (r2) |
| `#AFF0FF` | body gradient top | hand-tuned, brightened `Accent` |
| `#4A7ECF` | body gradient bottom | hand-tuned, near `SurfaceKeyActive #5278B8` |
| `#F6C77E` | pie gradient head | hand-tuned, lightened `AccentWarning` |
| `#1A2038` | 16 px frame flat tile | hand-tuned (r1 Finding 5e) |
| `#F7FCFF` | gaze dot | hand-tuned near-white |

## Frame plan (6 frames in the .ico)

r1 Finding 5: replaced prose with an explicit layers × frames table. The owner's display runs
200 % DPI, where every needed size (16/32/64) exists; at 125 %/150 % Windows rescales
(r1 Finding 11 — accepted, no 20/24/40 frames).

| Layer | 256 / 128 / 64 / 48 (master) | 32 (tuned) | 16 (tuned) |
|---|---|---|---|
| Tile | radial gradient + rim | radial gradient, **no rim** | flat `#1A2038` |
| Glow | yes | **no** (L13 veto: back to the agreed plan) | **no** |
| Body | gradient | gradient | gradient |
| Iris track | r 13.5 + hairline | r 14, **no hairline** | r 15.5, **no hairline** (track ring ≈ 0.17 px — invisible, intended) |
| Whisper arc | r 11.5, width 2 | r 11.5, **width 3, opacity 0.60** (`M59.5,48 A11.5,11.5 0 0 0 48,36.5`) | **none** |
| Pie fill | r 12.5, gradient (`M48,48 L48,35.5 A12.5,12.5 0 1 0 60.5,48 Z`) | r 13, **gradient** (`M48,48 L48,35 A13,13 0 1 0 61,48 Z`) | r 14.5, **solid `#E8A33C`** (`M48,48 L48,33.5 A14.5,14.5 0 1 0 62.5,48 Z`) |
| Gaze dot | r 4.5 @ (60.5,48) | r 5 @ (61,48) | r 6 @ (62.5,48) |

32 px arc note (r1 Finding 9): r 11.5 width 3 puts the band at 10.0–13.0 — flush with the pie
edge (r 13) and clear of the track edge (r 14) — instead of the earlier r 12 band that poked
0.5 past the pie radius. 48 px renders the master directly.

## Build approach

Precedent for a solution-external tool is `tools/CorpusBuilder` (r1 Finding 10 corrected the
earlier benchmarks/ comparison — benchmarks additionally has a CI step, which this tool
deliberately does not; the committed `.ico` is the artifact).

**`tools/EyeCtrl.IconBuilder/`** — .NET 10 console project (not in the solution). WPF
(`UseWPF=true`, STA) so it can rasterize `DrawingVisual`s via `RenderTargetBitmap`; geometry
authored as the same path-data strings the spec uses, mirroring the repo's `IconGeometry`
idiom. Emits `src/EyeCtrl.App/Assets/eyectrl.ico`.

ICO container details (r1 Finding 7 — the spots implementers get wrong):

- Render each frame at scale `frameSize / 96`.
- Convert the WPF render from Pbgra32 to **Bgra32 (straight alpha)** before encoding, or the
  anti-aliased edges get a dark fringe.
- BMP frames (16/32/48/64/128): 32 bpp, `BI_RGB`, `biHeight = 2·h`, rows written bottom-up.
- AND mask: rows written bottom-up like the XOR rows, padded to 4 bytes; set the bit
  where α == 0.
- 256 frame: PNG-compressed; its directory entry carries width = height = 0.
- All directory entries: planes = 1, bit count = 32, colour count = 0.
- Directory entries in ascending size order (16 → 256). Never assume `Frames[i]` matches
  directory order when reading (WIC reorders by BMP/PNG mix — see splash note).
- Deterministic output: no timestamps; two consecutive runs must be byte-identical (A1).

## Headless verification (r1 Check 8)

Which surface gets which frame at the owner's 200 % DPI:

| Surface | Frame at 200 % |
|---|---|
| Tray (with L10 fix) | 32 (tuned) via `new Icon(stream, SystemInformation.SmallIconSize)` — measured by r1 |
| Splash | 128 (WIC first-enumerated with this layout) — pinned by A3 |
| Explorer | 32 small / 128→96 medium / 256→192 large (reasoned) |
| Pinned taskbar/Start shortcut | ~48 or 64 (reasoned) |
| Taskbar button / title bar / Alt-Tab | n/a — `WS_EX_TOOLWINDOW` overlays have none |

Startup-order note (r2-measured): the process is already system-aware at `Main` start,
before any WPF object exists — `SmallIconSize` = 32 and `IconSize` = 64 at 192 DPI
regardless of startup order; in any case the splash is shown at App.xaml.cs:135-136
before `TrayIconManager` runs at :268.

Headless checks (all runnable without display or input):

- Parse the ICONDIR bytes: 6 entries, ascending sizes, BMP 16–128 + PNG 256, header fields.
- Splash pin: `ImageSourceConverter`-produced source has `PixelWidth == 128` (A3).
- Tray pin: `new Icon(stream, new System.Drawing.Size(32,32))` and `(64,64)` return the
  expected frames (`System.Drawing.Size` spelled out — the bare `Size` is ambiguous in a
  test file that imports both System.Drawing and System.Windows and won't compile).
- Per-frame colour probe via `CopyPixels` (tile corner, iris centre, dot).
- Two builder runs byte-identical (A1).
- Splash render via RTB at 192 dpi for the preview sheet (A5); precedent
  `CornerRadiusClampTests.cs:38-54, :91`.

**DPI trap (r1):** a test that calls plain `new Icon(stream)` inside a DPI-unaware test
process reports 32 px at 200 % and would "prove" the tuned 32 reaches the tray while the real
app picks 64. Always pass an explicit size in tests.

**Only checkable by eye:** A5/A6 (see acceptance).

`OverlayResourceResolutionTests.cs:49-53, 69-98` constructs `SplashWindow` and therefore
parses the icon — not a frame gate, but the App test suite must stay green after the swap (A6).

## Acceptance criteria

1. **A1 Builder determinism:** two consecutive `EyeCtrl.IconBuilder` runs produce
   byte-identical `eyectrl.ico` files.
2. **A2 Container:** ICONDIR parses as 6 entries in ascending size order 16/32/48/64/128/256;
   BMP payloads for 16–128 (32 bpp, `biHeight = 2·h`), PNG for 256 (0/0 dimension entry).
3. **A3 Splash pin (new test):** decoding the resource the way `SplashWindow` does yields a
   source with `PixelWidth == 128`.
4. **A4a Tray frame check (the file):** explicit sizes only — `new Icon(stream, new
   System.Drawing.Size(32,32))` returns the 32 entry with a pixel probe matching the
   tuned render, and `(16,16)` the 16 entry. SmallIconSize must NOT key the test: it is
   32 at 192 DPI but 16 at 96 DPI (the CI runner), so a SmallIconSize-keyed test fails
   on CI or degenerates into a tautology. Never bare `new Icon(stream)` in tests (DPI
   trap above).
   **A4b Production-call pin (the code):** `TrayIconManager` cannot be constructed in a
   test (its ctor makes a visible `NotifyIcon`, TrayIconManager.cs:95-99), so pin the
   call instead — preferred: extract `internal static Icon LoadTrayIcon(Stream,
   System.Drawing.Size)`, tested directly, with the ctor passing
   `SystemInformation.SmallIconSize`; fallback: a source-scan test asserting
   `TrayIconManager` never calls the single-argument `Icon(Stream)` constructor.
   Guards regression of `TrayIconManager.cs:85` to the bare call, which A4a alone
   cannot catch.
5. **A5 Preview sheet:** the frames rendered on dark/light strips at the sizes each
   surface actually uses — tray 32, Explorer small 32, pinned shortcut 48/64, splash 128
   (drawn at 64 DIP), Explorer medium 96 (from the 128 frame) — plus the 16 frame at true
   size; eyeballed by the owner, 16/32 must read as tile + eye + amber iris + dot.
   Producer: the builder's `--preview` mode (emits the strips as PNGs next to the .ico);
   a scratch RTB script is acceptable.
6. **A6 Live swap:** Explorer file icon, tray, and splash show the new icon after an
   icon-cache refresh (`ie4uinit.exe -show` — no Explorer restart needed; the cache can
   keep showing the old icon, r1 Finding 4); `dotnet build EyeCtrl.slnx` succeeds;
   `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"` stays green — and
   `EYECTRL_UI_TESTS` is never set for this, because the gated FlaUI tests launch the
   real app and drive the OS cursor (AppFixture.cs:41-51, AppLaunchTests.cs:11).

## Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| L1 | Rounded-square tile, not a disc | Win11 icon idiom; crisper at small sizes; distance from the old disc | Keeping the disc (too close to the old icon) |
| L2 | Dark tile (F1), not the cyan-flip tile (F2) | matches the app's dark identity across all three themes | F2 Cyan Poster (parts from dark identity) |
| L3 | Amber pie iris (`AccentWarning` hue) | warm focal point on cool body; palette-native | cyan pie (insufficient separation from the body) |
| L4 | Pie reads ¾ filled; rotation deliberately mirrored from the control's clockwise sweep so the head-dot lands rightward | echoes the dwell story (¾ toward a click) while keeping the approved rightward composition | full pie (loses "in progress"); ring-only (weaker story); CW rotation (dot lands left, unapproved) |
| L5 | Gaze dot rides the fill head, not the centre | motion; owner chose V3 lineage (head dot) over V2 | centre dot ("Fixed Gaze") |
| L6 | Whisper arc inside the track (W1) | owner's pick among W1/W2/W3 | ghost ring (W2), outer halo (W3) |
| L7 | 16/32 px frames hand-tuned, not naive downscale | rim/hairline degrade below 48 px; the arc survives at 32 only thickened to width 3 and is dropped at 16; the glow survives at 32 boosted to a 22 % peak (L13); **with L10 the tray actually shows the tuned 32 at 200 %** — the premise r1 falsified now holds | single-geometry scaling for all frames |
| L13 | ~~Tray frame carries the bloom~~ **VETOED by the owner (2026-09-30, "deviates too much from what I agreed to")** — the 32 px frame is back to the agreed glow-free plan; the glow lives only at 48 px+, where it now renders correctly | tried and measured: 22 % peak is invisible at 32 physical px, 45 % reads but departs from the approved design — no bloom strength is both faithful and visible at tray size | 22 % peak (invisible); 45 % (visible but a deviation the owner rejected) |
| L8 | Builder = small C# WPF console tool | PIL/ImageMagick unavailable; WPF rasterizes path data natively; repo-consistent idiom | Python/Pillow pipeline, one-off hand-pixelled asset |
| L9 | Supervisor tray icon untouched | its generic `SystemIcons.Application` identity is deliberate | giving the supervisor the family icon |
| L10 | **Tray loader change (r1 Finding 1, option a):** `TrayIconManager.cs:85` → `new Icon(memoryStream, SystemInformation.SmallIconSize)` | the tuned frames only matter if the tray requests them; one line, measured safe by r1; without it the shell shrinks the 64 px frame | option (b): stay asset-only and accept the downscaled tray icon (undermines the whole frame plan) |
| L11 | 6-frame set (no 20/24/40) | the 200 % surfaces use 32, 48/64, 128 and 256 — all present (16 is the 100 % small size); 125/150 % rescaling accepted and documented (r1 Finding 11) | adding 20/24/40 frames (more tuned surfaces to maintain) |
| L12 | No CI step for the builder | the committed `.ico` is the artifact; builder runs are part of the one-off regeneration and its verification, not every build (r1 Finding 10) | CI-generating the icon on every push |
