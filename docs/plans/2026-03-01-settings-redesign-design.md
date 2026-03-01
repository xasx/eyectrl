# Settings Window Redesign — Design Document

**Date:** 2026-03-01
**Status:** Approved

## Goals

- Make the settings window a large, full-overlay experience suitable for eye-tracker use
- Eliminate save/cancel workflow — all changes apply immediately
- Add eye-tracker-friendly navigation (paginated content, dwell-enabled everything)
- Provide reset functionality (per-tab and global) with confirmation
- Relax default dwell times to reduce accidental activations for new users

## Window & Layout

- **Borderless overlay**, ~90% of primary screen size, centered
- **Background**: dark theme `#1E1E1E`, consistent with existing overlays
- **Topmost**: yes, like all other app overlays
- **Grid layout**:
  - Left column: **220px sidebar** for tab navigation
  - Right column: **content area** (remaining width)
  - Bottom of content area: **pagination bar**
- **Close button** (✕): top-right corner, 80×80px, dwell-enabled, dismisses overlay

## Sidebar (220px)

- **7 tab buttons** stacked vertically
  - ~80px tall each, full 220px width
  - Icon + label format using Unicode symbols (e.g. ◎ Cursor, ⏱ Dwell, 🔍 Zoom, ⌨ Keyboard, ▣ Toolbar, 🎨 Theme, ⚙ General)
  - Dwell-enabled with color blend progress (gray → blue)
  - Active tab: highlighted accent color, visually distinct
- **Close button** (✕) at the top of the sidebar
- No other buttons in the sidebar — Reset All lives on the General tab

## Content Area & Pagination

- Each tab's settings split into **pages that fit the visible content area** without scrolling
- Most tabs fit in 1 page; longer tabs (e.g. Dwell with 7 sliders) may need 2 pages
- **Pagination bar** at bottom of content area:
  - ◀ (previous) and ▶ (next) buttons, 80×64px each, dwell-enabled
  - Page indicator between them (e.g. "1 / 2")
  - Hidden entirely when tab has only 1 page
- **Per-tab Reset button**: last item on the last page of each tab (e.g. "Reset Cursor Settings")
  - 160×64px, distinct orange color (`#805020`)
- **"Reset All" button**: on the General tab page, resets all settings to defaults

## Controls Sizing

All controls scaled up for the larger overlay:

| Control | Old Size | New Size |
|---|---|---|
| Slider buttons (–/+) | 56×48px | 80×64px |
| Slider value display | 100px | 140px |
| Combo buttons (◀/▶) | 56×48px | 80×64px |
| Toggle buttons | 48px height | 64px height, full width |
| Color swatches | 52×44px | 72×60px |
| Per-tab Reset | N/A | 160×64px |
| Pagination buttons | N/A | 80×64px |
| Close button | N/A | 80×80px |
| Section label font | ~14px | ~18-20px |
| Control label font | ~14px | ~16px |

Dwell radii scale proportionally with larger button sizes.

## Live-Apply & Persistence

- **Immediate apply**: every control change writes directly to the live `EyeCtrlSettings` object and fires callbacks to update all overlays in real-time
- **No working copy / deep clone** — the JSON clone save/cancel pattern is removed
- **Auto-save**: `SettingsService.SaveAsync()` called after each change, debounced ~500ms to avoid excessive disk writes during rapid slider adjustments
- Settings file: `%APPDATA%\EyeCtrl\settings.json` (unchanged)

## Reset Confirmation Modal

- Centered dwell-enabled modal overlay on top of settings window
- Semi-transparent dark backdrop
- Message text: "Reset [Tab] settings to defaults?" or "Reset ALL settings to defaults?"
- Two buttons: **✓ Yes** (green) and **✕ No** (red), ~100px each, dwell-enabled
- On confirm: overwrite relevant settings with defaults, apply immediately, rebuild current tab content
- On cancel: dismiss modal, no changes

## Relaxed Default Dwell Times

| Setting | Old Default | New Default |
|---|---|---|
| FixationDetectionMs | 200 | 300 |
| ActivationButtonDwellMs | 500 | 700 |
| ActionMenuDwellMs | 400 | 600 |
| ZoomActionMenuDwellMs | 400 | 600 |
| ToolbarButtonDwellMs | 500 | 700 |
| KeyboardKeyDwellMs | 300 | 450 |
| KeyboardPredictionDwellMs | 350 | 500 |

~40-50% increase across the board. Experienced users can tune down; new users are protected from accidental activations.

## What's Not Changing

- Tab set: all 7 tabs remain (Cursor, Dwell, Zoom, Keyboard, Toolbar, Theme, General)
- Settings model classes: `EyeCtrlSettings` and sub-classes stay the same
- Control types: sliders, combos, toggles, color swatches — same interaction patterns, just bigger
- Gaze input: `UpdateGaze()` called from `App.xaml.cs` (unchanged)
- Three `DwellDetector` instances pattern (tabs, actions, controls)
- System tray integration for opening settings
