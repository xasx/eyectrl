#!/usr/bin/env python3
"""Validates the EyeCtrl overlay palette (docs/design/overlay-unification-spec.md §4.1).

Every number in §9 of the spec is produced by this script. Re-run it after any
palette edit; the spec's tables are only trustworthy if this exits 0.

    python tools/palette_validate.py

Implements WCAG 2.1 relative-luminance contrast, CIEDE2000 perceptual distance,
and Vienot-1999 dichromat simulation. No third-party dependencies.
"""

from __future__ import annotations

import math
import sys

# --- Themes. Each entry is a complete token set validated independently.
# --- ARGB (8 digits) where the token is composited over the desktop;
# --- RGB (6 digits) where it only ever sits on another token.
# --- "Midnight" is the shipping palette (spec §4.1). "Ember" is the warm
# --- low-blue-light alternative (spec §10) for a future theme system.
THEMES = {
    "Midnight": {
        "SurfaceDark":        "#FA1A1A2E",
        "SurfaceMedium":      "#FA2A2A44",
        "SurfaceLight":       "#FF3A3A5E",
        "SurfaceKey":         "#2A2A3E",
        "SurfaceKeyModifier": "#3E3E64",
        "SurfaceKeySwitch":   "#38527F",
        "SurfaceKeyActive":   "#5278B8",
        "SurfacePrediction":  "#2F5340",
        "BorderSubtle":       "#616BA0",
        "BorderFocus":        "#A5F3FC",
        "TextPrimary":        "#D6DEF5",
        "TextSecondary":      "#9AA4C8",
        "TextDim":            "#868FB5",
        "MarkerMove":         "#A8A0D0",
        "Accent":             "#A5F3FC",
        "StrokeHalo":         "#0A0A14",
        "AccentSuccess":      "#7EE787",
        "AccentWarning":      "#E8A33C",
        "AccentDanger":       "#FF5C7A",
        "ToggleOn":           "#4A8F5C",
        "ToggleOff":          "#2E2E42",
        "ModalBackdrop":      "#B3000000",
        "BorderThicknessStandard": "1",
        "_core":              "#1A1A2E",
    },
    "Ember": {
        "SurfaceDark":        "#FA211B14",
        "SurfaceMedium":      "#FA342B20",
        "SurfaceLight":       "#FF473A2C",
        "SurfaceKey":         "#2E2619",
        "SurfaceKeyModifier": "#4E4029",
        "SurfaceKeySwitch":   "#63382A",
        "SurfaceKeyActive":   "#9A6840",
        "SurfacePrediction":  "#33422A",
        "BorderSubtle":       "#7A6C51",
        "BorderFocus":        "#9FE8FF",
        "TextPrimary":        "#F2EEE8",
        "TextSecondary":      "#BFB4A2",
        "TextDim":            "#A0917C",
        "MarkerMove":         "#D8CBB0",
        "Accent":             "#9FE8FF",
        "StrokeHalo":         "#0E0A06",
        "AccentSuccess":      "#7EE787",
        "AccentWarning":      "#E8A33C",
        "AccentDanger":       "#FF5C7A",
        "ToggleOn":           "#67874C",
        "ToggleOff":          "#342C21",
        "ModalBackdrop":      "#B3000000",
        "BorderThicknessStandard": "1",
        "_core":              "#211B14",
    },
    # Situational theme for glare, bright rooms or tired eyes — not an all-day
    # palette (pure white at 21:1 is what Midnight's TextPrimary deliberately
    # avoids). Surfaces are OPAQUE: translucency and a contrast guarantee are
    # incompatible. Structure is carried by a 21:1 border, not by fill steps.
    "Contrast": {
        "SurfaceDark":        "#FF000000",
        "SurfaceMedium":      "#FF141414",
        "SurfaceLight":       "#FF262626",
        "SurfaceKey":         "#0F0F0F",
        "SurfaceKeyModifier": "#123A63",
        "SurfaceKeySwitch":   "#4A4A4A",
        "SurfaceKeyActive":   "#FF8800",
        "SurfacePrediction":  "#004D22",
        "BorderSubtle":       "#FFFFFF",
        "BorderFocus":        "#00FFFF",
        "TextPrimary":        "#FFFFFF",
        "TextSecondary":      "#E8E8E8",
        "TextDim":            "#C8C8C8",
        "MarkerMove":         "#FFFFFF",
        # Fully saturated, not a brighter #66FFFF: HC's pure-white border is a
        # structural colour and sits only dE 24.6 from the paler cyan.
        "Accent":             "#00FFFF",
        "StrokeHalo":         "#000000",
        "AccentSuccess":      "#00FF00",
        "AccentWarning":      "#FFFF00",
        # Red is inherently dark: #FF0000 is only 5.25:1 on black. Pushing past
        # 10:1 drops chroma under the gate-9 floor (#FF9999 is C* 41.5). This is
        # the ceiling for "high contrast and still reads red" — 8.16:1 at C* 57.6,
        # still the strongest AccentDanger of the three themes.
        "AccentDanger":       "#FF7777",
        "ToggleOn":           "#00FF00",
        "ToggleOff":          "#000000",
        "ModalBackdrop":      "#E6000000",
        "BorderThicknessStandard": "2",
        "_core":              "#000000",
    },
}

# Absolute floor for the dwell accent in every vision type. Set by what the
# outgoing Lime achieved at its worst (9.66 under tritanopia): any theme's
# accent must be at least as visible as the colour it replaces. Stated as an
# absolute so it is theme-independent — "must beat Lime" only made sense while
# Lime was still the incumbent.
ACCENT_FLOOR = 9.5

# Gate 9: minimum Lab chroma for a SEMANTIC token, so it still reads as its own
# hue. Without this the other gates can be satisfied by desaturated pastels that
# are numerically fine and semantically meaningless (a near-white "warning" at
# C* 10, a pastel-pink "danger" at C* 41). The real triad sits at C* 62-66.
# Deliberately NOT applied to Accent: the accent is a high-luminance signal and
# is correctly low-chroma (C* ~25 in both themes).
SEMANTIC_CHROMA_FLOOR = 55.0

# Tokens that are not colours. Theme-varying but parsed as numbers, so they are
# excluded from every colour gate. BorderThicknessStandard is the only one:
# high-contrast convention is a 2px outline because structure there is carried by
# the border rather than by fill steps (see gate 1), while the tuned palettes use
# 1px. Any future non-colour token must be added here or parse() will reject it.
NON_COLOUR_TOKENS = {"BorderThicknessStandard"}

# Worst realistic desktop behind a translucent overlay: a white document.
WORST_DESKTOP = "#FFFFFF"
# Backdrops a Background="Transparent" overlay may be drawn over.
BACKDROPS = ["#000000", "#404040", "#808080", "#C0C0C0", "#FFFFFF",
             "#66D9E8", "#A0E060", "#FF5C7A", "#1A1A2E"]


def parse(h: str) -> tuple[int, int, int, int]:
    """Parses #RGB, #RRGGBB or #AARRGGBB into (r, g, b, a)."""
    h = h.lstrip("#")
    if len(h) == 3:
        return (int(h[0] * 2, 16), int(h[1] * 2, 16), int(h[2] * 2, 16), 255)
    if len(h) == 6:
        return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), 255)
    if len(h) == 8:
        return (int(h[2:4], 16), int(h[4:6], 16), int(h[6:8], 16), int(h[0:2], 16))
    raise ValueError(f"bad colour literal: {h}")


def _lin(c: int) -> float:
    c /= 255.0
    return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4


def luminance(c) -> float:
    r, g, b = c[:3]
    return 0.2126 * _lin(r) + 0.7152 * _lin(g) + 0.0722 * _lin(b)


def contrast(a, b) -> float:
    """WCAG 2.1 contrast ratio."""
    la, lb = luminance(a), luminance(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)


def over(fg, bg):
    """Source-over composite of a translucent fg onto an opaque bg."""
    a = fg[3] / 255.0
    return tuple(round(fg[i] * a + bg[i] * (1 - a)) for i in range(3)) + (255,)


def _to_lab(c):
    r, g, b = (_lin(v) for v in c[:3])
    x = r * 0.4124 + g * 0.3576 + b * 0.1805
    y = r * 0.2126 + g * 0.7152 + b * 0.0722
    z = r * 0.0193 + g * 0.1192 + b * 0.9505

    def f(t):
        return t ** (1 / 3) if t > 0.008856 else 7.787 * t + 16 / 116

    fx, fy, fz = f(x / 0.95047), f(y / 1.0), f(z / 1.08883)
    return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz))


def lstar(c) -> float:
    return _to_lab(c)[0]


def chroma(c) -> float:
    """Lab chroma C* = hypot(a*, b*). How saturated the colour is."""
    _, a, b = _to_lab(c)
    return math.hypot(a, b)


def delta_e(c1, c2) -> float:
    """CIEDE2000 perceptual distance. JND is approximately 2.3."""
    l1, a1, b1 = _to_lab(c1)
    l2, a2, b2 = _to_lab(c2)
    c1c, c2c = math.hypot(a1, b1), math.hypot(a2, b2)
    cbar = (c1c + c2c) / 2
    g = 0.5 * (1 - math.sqrt(cbar ** 7 / (cbar ** 7 + 25 ** 7))) if cbar > 0 else 0
    a1p, a2p = (1 + g) * a1, (1 + g) * a2
    c1p, c2p = math.hypot(a1p, b1), math.hypot(a2p, b2)
    h1 = math.degrees(math.atan2(b1, a1p)) % 360
    h2 = math.degrees(math.atan2(b2, a2p)) % 360
    dlp, dcp = l2 - l1, c2p - c1p
    if c1p * c2p == 0:
        dhp = 0.0
    elif abs(h2 - h1) <= 180:
        dhp = h2 - h1
    else:
        dhp = h2 - h1 - 360 if h2 - h1 > 180 else h2 - h1 + 360
    dhp2 = 2 * math.sqrt(c1p * c2p) * math.sin(math.radians(dhp / 2))
    lbar, cbarp = (l1 + l2) / 2, (c1p + c2p) / 2
    if c1p * c2p == 0:
        hbar = h1 + h2
    elif abs(h1 - h2) <= 180:
        hbar = (h1 + h2) / 2
    else:
        hbar = (h1 + h2 + 360) / 2 if h1 + h2 < 360 else (h1 + h2 - 360) / 2
    t = (1 - 0.17 * math.cos(math.radians(hbar - 30))
         + 0.24 * math.cos(math.radians(2 * hbar))
         + 0.32 * math.cos(math.radians(3 * hbar + 6))
         - 0.20 * math.cos(math.radians(4 * hbar - 63)))
    dtheta = 30 * math.exp(-(((hbar - 275) / 25) ** 2))
    rc = 2 * math.sqrt(cbarp ** 7 / (cbarp ** 7 + 25 ** 7)) if cbarp > 0 else 0
    sl = 1 + (0.015 * (lbar - 50) ** 2) / math.sqrt(20 + (lbar - 50) ** 2)
    sc, sh = 1 + 0.045 * cbarp, 1 + 0.015 * cbarp * t
    rt = -math.sin(math.radians(2 * dtheta)) * rc
    return math.sqrt((dlp / sl) ** 2 + (dcp / sc) ** 2 + (dhp2 / sh) ** 2
                     + rt * (dcp / sc) * (dhp2 / sh))


def simulate(c, kind: str):
    """Vienot 1999 dichromat simulation: 'prot', 'deut' or 'trit'."""
    r, g, b = (_lin(v) for v in c[:3])
    l = 17.8824 * r + 43.5161 * g + 4.11935 * b
    m = 3.45565 * r + 27.1554 * g + 3.86714 * b
    s = 0.0299566 * r + 0.184309 * g + 1.46709 * b
    if kind == "prot":
        l = 2.02344 * m - 2.52581 * s
    elif kind == "deut":
        m = 0.494207 * l + 1.24827 * s
    elif kind == "trit":
        s = -0.395913 * l + 0.801109 * m
    else:
        raise ValueError(kind)
    out = (0.080944 * l - 0.130504 * m + 0.116721 * s,
           -0.010248 * l + 0.054019 * m - 0.113615 * s,
           -0.000365 * l - 0.004121 * m + 0.693513 * s)

    def delin(v):
        v = max(0.0, min(1.0, v))
        return round(255 * (12.92 * v if v <= 0.0031308
                            else 1.055 * v ** (1 / 2.4) - 0.055))

    return tuple(delin(v) for v in out) + (255,)


# --------------------------------------------------------------------------
_failures: list[str] = []


def check(label: str, value: float, floor: float, note: str = "") -> None:
    ok = value >= floor
    if not ok:
        _failures.append(f"{label}: {value:.2f} < {floor}")
    print(f"  {'PASS' if ok else 'FAIL'}  {label:44}{value:7.2f}  (>= {floor})  {note}")


def validate_theme(name: str, palette: dict) -> None:
    """Runs all eight gates against one theme. Appends to the module failure list."""
    tok = {k: parse(v) for k, v in palette.items()
           if not k.startswith("_") and k not in NON_COLOUR_TOKENS}
    core = parse(palette["_core"])
    # Worst-case SurfaceDark: translucent token over a white document.
    worst = over(tok["SurfaceDark"], parse(WORST_DESKTOP))
    accent = tok["Accent"]

    print("=" * 78)
    print(f"THEME: {name}")
    print("=" * 78)
    print(f"Opaque surface core     : {palette['_core']}")
    print(f"Worst composited surface: #{worst[0]:02X}{worst[1]:02X}{worst[2]:02X} "
          f"(SurfaceDark over {WORST_DESKTOP})")
    print()

    print("GATE 1 - layering: fill step present, border carries the boundary")
    for a, b in [("SurfaceDark", "SurfaceMedium"), ("SurfaceMedium", "SurfaceLight")]:
        ca, cb = over(tok[a], core), over(tok[b], core)
        print(f"        {a:14}-> {b:14} contrast {contrast(ca, cb):5.2f}"
              f"  dE {delta_e(ca, cb):5.1f}")
    check(f"[{name}] BorderSubtle on worst surface",
          contrast(tok["BorderSubtle"], worst), 3.0, "WCAG 1.4.11 non-text")
    check(f"[{name}] elevation raises opacity",
          tok["SurfaceLight"][3] - tok["SurfaceDark"][3], 0,
          f"{tok['SurfaceDark'][3]} -> {tok['SurfaceLight'][3]}")
    thickness = float(palette["BorderThicknessStandard"])
    check(f"[{name}] BorderThicknessStandard", thickness, 1.0,
          f"px — border carries the boundary at "
          f"{contrast(tok['BorderSubtle'], worst):.2f}:1")

    print()
    print("GATE 2 - text legibility against the WORST composited surface")
    check(f"[{name}] TextPrimary", contrast(tok["TextPrimary"], worst), 7.0, "AAA")
    check(f"[{name}] TextSecondary", contrast(tok["TextSecondary"], worst), 4.5, "AA normal")
    check(f"[{name}] TextDim", contrast(tok["TextDim"], worst), 4.5, "AA normal")

    print()
    print(f"GATE 3 - dwell accent >= {ACCENT_FLOOR} in every vision type")
    for kind, label in [(None, "normal"), ("prot", "protanopia"),
                        ("deut", "deuteranopia"), ("trit", "tritanopia")]:
        c = accent if kind is None else simulate(accent, kind)
        check(f"[{name}] Accent ({label})", contrast(c, core), ACCENT_FLOOR)

    print()
    print("GATE 4 - accent reserved (>=25 vs meaning-bearing, >=15 vs text)")
    for key, value in tok.items():
        if key in ("Accent", "BorderFocus", "StrokeHalo", "ModalBackdrop"):
            continue
        floor = 15 if key.startswith("Text") else 25
        d = delta_e(value, accent)
        if d < floor:
            _failures.append(f"[{name}] Accent ~ {key}: dE {d:.1f} < {floor}")
        print(f"        Accent ~ {key:20} dE {d:5.1f}  (>= {floor})"
              f"{'  <-- TOO CLOSE' if d < floor else ''}")

    print()
    print("GATE 5 - semantic triad separated by lightness, not hue alone")
    triad = ["AccentSuccess", "AccentWarning", "AccentDanger"]
    for key in triad:
        print(f"        {key:15}{palette[key]}  L* {lstar(tok[key]):5.1f}"
              f"  contrast {contrast(tok[key], core):5.2f}")
    for i in range(len(triad)):
        for j in range(i + 1, len(triad)):
            x, y = tok[triad[i]], tok[triad[j]]
            ds = [delta_e(simulate(x, k), simulate(y, k))
                  for k in ("prot", "deut", "trit")]
            check(f"[{name}] {triad[i][6:]}/{triad[j][6:]} dL*",
                  abs(lstar(x) - lstar(y)), 9.0,
                  f"dE prot/deut/trit {ds[0]:.1f}/{ds[1]:.1f}/{ds[2]:.1f}")

    print()
    print("GATE 6 - toggle state readable without hue")
    on, off = tok["ToggleOn"], tok["ToggleOff"]
    check(f"[{name}] ToggleOn / ToggleOff", contrast(on, off), 3.0, "luminance-carried")
    for kind, label in [("prot", "protanopia"), ("deut", "deuteranopia"),
                        ("trit", "tritanopia")]:
        check(f"[{name}] ToggleOn / ToggleOff ({label})",
              contrast(simulate(on, kind), simulate(off, kind)), 3.0)

    print()
    print("GATE 7 - transparent-overlay edge holds on any backdrop")
    halo = tok["StrokeHalo"]
    worst_edge = min(max(contrast(accent, halo), contrast(accent, parse(d)),
                         contrast(halo, parse(d))) for d in BACKDROPS)
    check(f"[{name}] min best-edge over {len(BACKDROPS)} backdrops", worst_edge, 7.0,
          "halo makes it backdrop-independent")

    print()
    print("GATE 8 - keyboard key ramp mutually distinct")
    keys = ["SurfaceKey", "SurfaceKeyModifier", "SurfaceKeySwitch", "SurfaceKeyActive",
            "SurfacePrediction"]
    for i in range(len(keys)):
        for j in range(i + 1, len(keys)):
            check(f"[{name}] {keys[i][7:]}/{keys[j][7:]}",
                  delta_e(tok[keys[i]], tok[keys[j]]), 8.0)

    print()
    print("GATE 9 - semantic colours saturated enough to read as their own hue")
    for key in triad:
        check(f"[{name}] {key} chroma", chroma(tok[key]), SEMANTIC_CHROMA_FLOOR,
              "C* — guards against pastels that pass every other gate")
    print()


def main() -> int:
    names = sys.argv[1:] or list(THEMES)
    unknown = [n for n in names if n not in THEMES]
    if unknown:
        print(f"unknown theme(s): {', '.join(unknown)}")
        print(f"available: {', '.join(THEMES)}")
        return 2

    for name in names:
        validate_theme(name, THEMES[name])

    print("=" * 78)
    if _failures:
        print("FAILURES:")
        for f in _failures:
            print(f"  - {f}")
        return 1
    print(f"ALL GATES PASS - {len(names)} theme(s): {', '.join(names)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
