# EyeCtrl — Design Document

## Overview

EyeCtrl is a WPF application that enables full PC control via a Tobii Dynavox PCEye 5 eye tracker. It simulates keyboard and mouse input, serving as a daily-driver input method for hands-free operation.

## Technology Stack

- **Language:** C#
- **UI Framework:** WPF (transparent, always-on-top, non-activatable overlay windows)
- **Eye Tracker SDK:** Tobii.Interaction NuGet package
- **Input Simulation:** Win32 `SendInput` via P/Invoke (`user32.dll`)
- **Settings Storage:** JSON file
- **Prediction Data:** Local JSON or SQLite

## Architecture

```
┌─────────────────────────────────────────────────┐
│                  EyeCtrl (WPF)                  │
├─────────────┬───────────────┬───────────────────┤
│  UI Layer   │  Core Logic   │  Platform Layer   │
│             │               │                   │
│ ToolbarView │ GazeProcessor │ TobiiGazeProvider │
│ KeyboardView│ DwellDetector │ InputSimulator    │
│ SettingsView│ ModeManager   │ (SendInput/P/Inv) │
│ ActionMenu  │ WordPredictor │                   │
│ ZoomOverlay │               │                   │
└─────────────┴───────────────┴───────────────────┘
```

### Three Layers

1. **UI Layer** — WPF overlay windows (toolbar, keyboard, action menu, zoom overlay, settings). Transparent, always-on-top, non-activatable so they don't steal focus from the controlled application.

2. **Core Logic** — Framework-agnostic C# classes with no dependency on WPF or Tobii. Testable in isolation.
   - `GazeProcessor` — applies smoothing/snapping filters to raw gaze data
   - `DwellDetector` — tracks gaze fixation and triggers actions
   - `ModeManager` — manages interaction state (free look, fixation, action selection, zoom)
   - `WordPredictor` — handles text prediction and user learning

3. **Platform Layer** — Abstractions over hardware and OS.
   - `IGazeProvider` interface wrapping Tobii SDK (swappable for other trackers)
   - `InputSimulator` wrapping Win32 `SendInput` for mouse and keyboard events

## Gaze Processing Pipeline

### Cursor Movement

Raw gaze data from the PCEye 5 arrives at ~33Hz. It passes through a configurable filter chain:

- **Direct** — Pass-through, no filtering
- **Smoothed** — Exponential moving average. Configurable smoothing factor (0.0–1.0)
- **Smoothed + Snapping** — Same smoothing plus snap radius. When gaze is within N pixels of a UI element (detected via UI Automation API), cursor snaps to element center

The filtered coordinates drive the real Windows system cursor via `SendInput` with `MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE`.

### Cursor Circle

A configurable circle overlay always surrounds the cursor, showing the area that would be magnified in zoom mode. Default size: 40px diameter. Configurable properties: color, opacity, thickness, size, contrast mode.

### Interaction Flow (Without Zoom)

```
Cursor + circle follow gaze freely (no actions triggered)
         │
   Gaze stabilizes (velocity drops below threshold for fixation detection time)
         │
   Activation button fades in at cursor
         │
   Gaze moves away? → Button hides, back to free look
         │
   Dwell on activation button (activation button dwell time)
         │
   Radial action menu appears at fixation point
   ┌──────────────────┐
   │    Scroll ↑      │
   │ LC   ●   RC      │
   │  DC  Drag  ...   │
   └──────────────────┘
         │
   Dwell on action (action menu dwell time) → Fire at fixation point
   Gaze away from menu → Cancel, dismiss menu
```

### Interaction Flow (With Zoom)

```
Cursor + circle follow gaze freely
         │
   Gaze stabilizes → Activation button fades in
         │
   Dwell on activation button
         │
   Circle area magnifies into zoomed overlay
         │
   Free look within zoomed view (no activation button)
         │
   Gaze stabilizes in zoomed view
         │
   Action menu + Back button appear at fixation point
         │
   Dwell on action → Fire at precise real-screen coordinate, dismiss zoom
   Dwell on Back → Dismiss zoom, no action, return to free look
```

### Available Actions

- Left click
- Right click
- Double click
- Drag (start/end)
- Scroll (up/down/left/right)

The radial action menu is configurable: which actions appear and their positions. Buttons display Unicode symbols (☝ click, ☰ right-click, ✌ double, ✊ drag, ▲▼◀▶ scroll) with 100px diameter dwell targets and green arc progress indicators. Toolbar buttons (80px) also use Unicode symbols (⏸⊕⌨⚙) with bottom progress bars.

## On-Screen Keyboard

### Overlay

- WPF transparent always-on-top window
- Toggled via toolbar button
- Slides in from configurable screen edge (top/bottom/left/right)
- Keys are directly dwellable (no activation button needed)
- Dwell on key → sends keystroke via `SendInput`

### Layouts

- Layout definitions stored as JSON files (key positions, sizes, labels, output values)
- Built-in layouts: QWERTY, QWERTZ, AZERTY, frequency-optimized
- Users can create custom layouts by editing JSON files
- Layout switcher button on the keyboard to cycle layouts
- Support for shift/caps states, symbols layer, special keys (Enter, Tab, Backspace, arrows, modifiers)

### Word Prediction

- Prediction bar above the keyboard showing 3–5 suggestions
- Suggestions update in real-time as letters are typed
- Dwell on a suggestion to auto-complete (simulated backspaces + full word typed)
- After completion, next-word predictions appear immediately

### Prediction Engine

- Dictionary-based prediction using frequency-ranked word lists
- Learns from user input — personal word frequencies stored locally (JSON/SQLite)
- No cloud connectivity, fully local
- Language packs as configurable word list files

### Keyboard Sizing

- Key sizes configurable (larger = easier dwell, fewer visible)
- Overall keyboard dimensions as percentage of screen
- Compact and full layout options

## Toolbar

A thin utility panel with 4 dwellable buttons:

- **Pause / Resume** — toggles gaze control on/off with clear visual indicator
- **Zoom on / off** — toggles two-phase precision mode
- **Keyboard** — opens/closes the on-screen keyboard
- **Settings** — opens the configuration panel

Available as docked (pinned to screen edge) or floating (repositionable). User chooses in settings.

## Settings

All settings persisted as JSON. Profile support for saving/loading named configurations.

### Cursor Settings
- Filter mode: direct / smoothed / smoothed+snapping
- Smoothing factor (0.0–1.0)
- Snap radius (pixels)
- Circle: color, opacity, thickness, contrast mode

### Dwell Time Settings (each independently configurable)
- Fixation detection time
- Activation button dwell time
- Action menu dwell time
- Zoom-mode action menu dwell time
- Toolbar button dwell time
- Keyboard key dwell time
- Keyboard prediction suggestion dwell time

### Zoom Settings
- Enable / disable
- Circle radius
- Magnification level

### Action Menu Settings
- Visible actions
- Radial positions

### Keyboard Settings
- Active layout
- Key size
- Prediction on/off
- Language pack
- Keyboard screen position

### Toolbar Settings
- Docked vs floating
- Screen edge
- Size

### Theme Settings
- Light / dark / follow system
- Overlay opacity
- High-contrast mode

### General Settings
- Start on boot
- Start paused
- Profile import / export
