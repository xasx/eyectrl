# EyeCtrl — User Guide

EyeCtrl lets you control a Windows PC entirely with your eyes. A colored ring follows your
gaze, and *dwelling* — resting your gaze on one spot — activates buttons, clicks, drags,
scrolls, and types on an on-screen keyboard. It is built for the Tobii Dynavox PCEye 5 and
compatible Tobii eye trackers, and it can also run in a mouse mode for testing or for users
who still have some hand control.

This guide describes how to use EyeCtrl as an end user — for yourself or as a caregiver
setting it up for someone. Values marked **default** are what you get after a fresh install
or after pressing a reset button; almost everything can be changed in Settings.

> **About the images:** photographs are real screenshots of the app (default Midnight
> theme); graphics labeled *schematic* are drawn diagrams of the same interfaces.

---

## Contents

1. [What you need](#1-what-you-need)
2. [Starting and quitting](#2-starting-and-quitting)
3. [Gaze control basics](#3-gaze-control-basics)
4. [The toolbar](#4-the-toolbar)
5. [Clicking with the action menu](#5-clicking-with-the-action-menu)
6. [Dragging and dropping](#6-dragging-and-dropping)
7. [Scrolling](#7-scrolling)
8. [Repeat clicks (Modes menu)](#8-repeat-clicks-modes-menu)
9. [Precise aiming: Manual Selection](#9-precise-aiming-manual-selection)
10. [Precise aiming: Auto Selection](#10-precise-aiming-auto-selection)
11. [Pinned notes](#11-pinned-notes)
12. [The task switcher](#12-the-task-switcher)
13. [The on-screen keyboard](#13-the-on-screen-keyboard)
14. [Emoji](#14-emoji)
15. [Word prediction](#15-word-prediction)
16. [Quick Actions](#16-quick-actions)
17. [Calibration](#17-calibration)
18. [Eye care](#18-eye-care)
19. [Adaptation](#19-adaptation)
20. [Themes and appearance](#20-themes-and-appearance)
21. [The Settings window](#21-the-settings-window)
22. [Settings reference](#22-settings-reference)
23. [Language](#23-language)
24. [Crash recovery and the Supervisor](#24-crash-recovery-and-the-supervisor)
25. [Where your data is stored](#25-where-your-data-is-stored)
26. [Troubleshooting](#26-troubleshooting)
27. [Caregiver quick reference](#27-caregiver-quick-reference)

---

## 1. What you need

- A PC with **Windows 10 or 11**. EyeCtrl is designed for a single monitor.
- A **Tobii Dynavox PCEye 5** or another eye tracker that works with the Tobii Stream
  Engine runtime. The tracker software from Tobii must be installed and the tracker
  connected.
- **No tracker? No problem.** EyeCtrl automatically falls back to *mouse mode*, where the
  position of the mouse pointer is treated as your gaze point. Mouse mode is ideal for
  trying EyeCtrl, demonstrating it, or using it with a caregiver moving the mouse.

If you use the published single-file build, keep the `Resources` folder (keyboard layouts,
dictionaries, emoji data, sounds) next to `EyeCtrl.exe` — the app needs those files beside it.

## 2. Starting and quitting

**Start:** double-click `EyeCtrl.exe`. A splash card appears briefly while the overlays get
ready, then the gaze ring starts following your eyes. If **Start on Windows Boot** is on
(Settings → General), Windows starts EyeCtrl automatically when you sign in.

Only one copy of EyeCtrl runs at a time — starting it again does nothing.

**Quit:** there is no Exit button on the toolbar — quitting goes through the Windows tray
menu, which is a normal Windows menu, not an EyeCtrl dwell surface. By gaze: look at the
EyeCtrl tray icon, complete an activation there, and dwell **Right click** in the action
menu; then fixate **Exit EyeCtrl** in the tray menu that opens and complete another
activation with **Left click**. By mouse: a caregiver right-clicks the tray icon and picks
**Exit EyeCtrl**. Quitting gives your mouse and keyboard back to Windows.

**System tray:** the EyeCtrl icon sits in the Windows notification area. Right-click (or have
a caregiver right-click) it for:

| Tray item | What it does |
|---|---|
| **Pause / Resume** | Toggles gaze tracking (see [Basics](#3-gaze-control-basics)) |
| **Settings** | Opens the Settings window (double-clicking the tray icon does the same) |
| **Language / Sprache** | Jumps straight to the language setting |
| **Reset adaptation** | Clears everything Adaptation has learned |
| **Show toolbar** | Force-shows the auto-hidden toolbar (only offered when the toolbar auto-hides) |
| **Exit EyeCtrl** | Quits the app |

**Command line** (mainly for caregivers and testing): `--test-mode` starts in mouse mode
without the splash screen, `--paused` starts paused for this run only, and
`--ui-language de|en|system` switches the interface language.

## 3. Gaze control basics

**The gaze ring.** A colored ring follows your gaze everywhere. Its size, color, thickness,
and opacity are yours to choose (Settings → Cursor). Three ring styles keep it visible on
any background: *Weighted sandwich* (default), *Bi-tonal segments*, and *Achromatic
reticle*.

**Dwell = rest your gaze.** To press anything in EyeCtrl — a toolbar button, a keyboard key,
a settings row — just look at it and hold still. A progress indicator fills while you dwell;
when it completes, the control activates. Look away at any time to cancel.

![A dwell progress arc filling around a toolbar button](images/dwell-arc.png)
*A dwell in progress: the arc fills around the button as you hold your gaze on it.*

**How a click happens.** Rest your gaze where you want to act:

1. After about **300 ms** (default) your gaze registers as a *fixation* and a round
   activation halo appears at that spot (Settings → Dwell Times calls this the **Activation
   Button**).
2. Keep resting (**700 ms** default) until the halo completes — the **action menu** opens
   there (see [section 5](#5-clicking-with-the-action-menu)).
3. Dwell the action you want, for example **Left click**.

<img src="images/dwell-flow.svg" width="720" alt="Schematic: rest your gaze, hold the activation halo, the action menu ring opens">

**Dwell times** (defaults; every one is adjustable in Settings → Dwell Times):

| Where | Default dwell |
|---|---|
| Fixation detection | 300 ms |
| Activation button (opens the action menu) | 700 ms |
| Action menu items | 600 ms |
| Toolbar buttons | 700 ms |
| Keyboard keys | 450 ms — keys that can destroy work or leave the app use 600 ms |
| Prediction chips | 500 ms |

After any activation there is a short **cooldown** (**400 ms** default) during which the same
target cannot fire again — so one long look is one activation, not several.

**Sound feedback.** An optional **Dwell Completion Sound** (off by default) plays a soft
tone whenever a dwell activates on EyeCtrl's own buttons, menus, and settings. The keyboard
has its own key click sound, on by default.

**Pause.** Dwelling **Pause** on the toolbar (or the tray item) suspends gaze control: the
ring disappears and a thin indicator bar along a screen edge shows EyeCtrl is paused.
Nothing can be activated by gaze except the toolbar and its
menus — they deliberately stay dwellable so a gaze-only user can always come back by
dwelling **Resume**. Pause is also handy when
you want to rest your eyes or let a caregiver use the mouse.

**Your mouse and keyboard keep working.** The gaze ring is an overlay of its own — the
Windows pointer jumps to your gaze point only in the instant an action fires. Between
actions, a physical mouse (a caregiver's, or your own hand) and keyboard work normally
alongside gaze. The one exception is mouse mode, where the mouse pointer *is* the gaze
point.

### Getting unstuck

No mode can trap you — each has several ways out:

| If you are… | Ways out |
|---|---|
| Scrolling | Look back at the anchor · dwell the ⏹ Stop button (right screen edge) · press Esc · look away from the screen for ~2 s |
| Mid-drag | Hold still ~½ s to drop where you look · dwell the release button (right screen edge) · losing gaze for ~2 s cancels safely back at the grab point |
| In a repeat-click mode | Dwell the ⏹ Stop button (right screen edge) · dwell the same mode again · press Esc · losing gaze for ~2 s |
| Action menu open | Dwell ✕ (right screen edge) · press Esc |
| Inside the magnifier | Dwell ✕ or **Back** (right screen edge) · press Esc |
| Paused | The toolbar stays dwellable while paused — dwell **Resume** |

And two backstops that always apply: the **physical mouse and keyboard never stop
working**, and a physical **Esc** key press leaves the action menu, manual selection,
scroll mode, and repeat mode from anywhere in the system.

## 4. The toolbar

The toolbar is the band of large dwell buttons you reach for everything. By default it
**docks to the top edge** and **auto-hides**: with a real eye tracker connected, it slides
out of view after a moment and slides back in when you look at its docked edge. You can also
summon it from the tray (**Show toolbar**), turn docking off entirely, or dock it to the
bottom, left, or right edge (Settings → Toolbar). Button size is adjustable (default 120).

![The EyeCtrl toolbar with its seven buttons](images/toolbar.png)
*The toolbar, docked at the top edge: Pause/Resume, Selection mode, Keyboard, Settings, Task
switcher, Modes, and Reset adaptation.*

| Button | What it does |
|---|---|
| **Pause / Resume** | Toggles gaze tracking (icon flips between pause and play) |
| **Selection mode** | Cycles *None → Manual Selection → Auto Selection → None* (see [sections 9–10](#9-precise-aiming-manual-selection)); off by default |
| **Keyboard** | Shows / hides the on-screen keyboard |
| **Settings** | Opens the Settings window |
| **Task switcher** | Shows open windows as full-screen tiles (see [section 12](#12-the-task-switcher)) |
| **Modes** | Opens the repeat-clicks menu (see [section 8](#8-repeat-clicks-modes-menu)); its icon changes while a repeat mode is active |
| **Reset adaptation** | Clears everything Adaptation has learned (no-op when Adaptation is off) |

While the keyboard is open, the toolbar makes room for it — and while other overlays own the
screen (settings, calibration), the toolbar steps back.

## 5. Clicking with the action menu

When you complete an activation (see [Basics](#3-gaze-control-basics)), the **action menu**
opens — a ring of action buttons (a different ring from your gaze ring), placed by default
at the **screen center**, or right at your gaze point if you set Position to *At Gaze*
(Settings → Layout). Each dwell on the ring activates that action at the point you were
looking at.

**First ring layer** (defaults): **Left click, Right click, Double click, Scroll Mode**, plus
a **More** button. **More** flips the ring to the **second layer**: **Drag, Scroll Up,
Scroll Down, Pin note, Keyboard**, plus a **Back** button to return. Which actions appear,
and which sit on the first layer (up to four fit), is fully configurable in
Settings → Action Menu.

<img src="images/action-menu.svg" width="720" alt="Schematic: the action menu ring with its two layers, modifier edge buttons, and close/help buttons">

**While the ring is open:**

- **Right screen edge** shows a **✕ Close** button (dismisses the menu) and a **? Help**
  button that toggles the ring's labels on and off — labels are normally shown only while
  you dwell an item.
- **Left screen edge** shows **Shift, Ctrl, Alt** buttons. Dwelling one latches it for the
  next click — for example latch **Shift** then **Left click** to Shift+click. The latch
  clears when the menu closes.

The default dwell for ring items is 600 ms (the manual-selection variant uses its own
setting, also 600 ms by default).

## 6. Dragging and dropping

1. Open the action menu where you want to **grab**, and dwell **Drag**.
2. An amber **drag trail** follows your gaze as you move — you are now holding the item.
3. To **drop**, hold your gaze still: resting within a small radius (default 40 px) for
   about half a second (default 500 ms) releases the drag. Alternatively, dwell the
   **release button** that appears at the right screen edge while dragging.

If the tracker loses your eyes for more than about two seconds mid-drag, the drag is
**cancelled** — the item is released back where you grabbed it, so nothing gets dropped in
the wrong place while you cannot see the trail.
Drag trail color and opacity, and the settle time and radius, are in Settings → Advanced.

## 7. Scrolling

**Scroll Mode** (on the ring's first layer by default) turns your gaze into a joystick:

- The point where you opened the menu becomes the **anchor**, marked with a crosshair and a
  circle.
- **Look back at the anchor to stop.** Look away from it in any direction to scroll that
  way — the farther you look, the faster it scrolls. A small dot shows your joystick
  position, and the accent ring thickens with speed.
- **To leave scroll mode:** dwell the **⏹ Stop button** at the right screen edge, press the
  physical **Esc** key, or simply look away from the screen for about two seconds.

Scroll sensitivity (default 1.0, range 0.5–2.0) is in Settings → Advanced. The ring's second
layer also offers one-shot **Scroll Up / Scroll Down** actions (and optional Scroll Left /
Scroll Right, off by default) for a single flick without entering scroll mode.

<img src="images/scroll-mode.svg" width="680" alt="Schematic: scroll mode anchor with dead zone, gaze dot offset, and stop button">

## 8. Repeat clicks (Modes menu)

When you need the *same* click several times in a row — paging through a list, ticking many
checkboxes — lock a repeat mode:

1. Dwell **Modes** on the toolbar. Four icon buttons appear under it: left click, right
   click, double click, middle click.
2. Dwell the click you want. Every activation now performs that click directly — no action
   menu in between. The toolbar's Modes icon shows which mode is locked.
3. To unlock: dwell the **⏹ stop button** at the right screen edge, dwell the same mode
   again (or pick a different one), or press Esc — and if the tracker loses your eyes for
   about two seconds, the mode unlocks by itself.

While a repeat mode is locked, a slightly smaller companion ring follows your gaze as the
repeat cursor; every activation performs the locked click right where you were looking.

## 9. Precise aiming: Manual Selection

Small targets are hard to hit with gaze alone. **Manual Selection** (toolbar → Selection
mode, or Settings → Selection) magnifies before you commit:

1. Activate (fixate + dwell the halo) **near** your target.
2. A **magnifier** appears — a circular window showing a zoomed picture of that screen
   region (default zoom 2.0×, radius 150 px; adjustable).
3. Look at the exact spot inside the zoom. After a short settling moment a small **anchor**
   crosshair marks the spot; dwell it (default 600 ms) to commit.
4. The action menu opens **centered on your precise point**, with the zoomed preview still
   visible in the middle so you can confirm. Dwell the action you want.

Two helpers make this forgiving: a **grace period** (default 500 ms) ignores your first
glance so you can orient, and **hold tolerance** (default 72 px) lets your gaze wobble
without resetting the dwell — because tolerance is measured on screen, not in the zoom,
wobbling looks 2× smaller to EyeCtrl at 2× zoom. That is where the precision comes from.

At the right edge: **✕ Close** leaves manual selection, and the **recapture** button takes a
fresh screenshot (e.g. if the screen content changed). A **Back** button on the action menu
returns to the magnifier without committing.

## 10. Precise aiming: Auto Selection

**Auto Selection** (toolbar → Selection mode) snaps to controls for you. When you activate,
EyeCtrl asks Windows what *interactive control* is under that point — a button, link, text
box, menu item, checkbox, list item, and so on, in any application that exposes them
(including non-Microsoft apps and web browsers). If one is found:

- a **highlight rectangle** marks the control, and
- the action menu opens centered on it, and your chosen action is performed at the
  control's center — not at your raw, slightly-off gaze point.

If nothing interactive is exactly under the point, EyeCtrl searches outward in rings
(default 48 px) and can finally fall back to the whole window or the raw pixel (adjustable
in Settings → Selection).

## 11. Pinned notes

**Pin note** (action menu, second layer) drops a sticky note at the point you were looking
at — up to 8 at a time. A note can be edited with the on-screen keyboard and stays on top
until you close it. Use it to leave yourself (or a caregiver) a message right where it
matters.

## 12. The task switcher

Dwell **Task switcher** on the toolbar (or configure it as a Quick Action) and every open
window appears as a large tile with its app's icon. Dwell a tile to switch to that window.
The switcher covers the screen, so nothing small needs to be hit.

## 13. The on-screen keyboard

Dwell **Keyboard** on the toolbar (or the ring's **Keyboard** action) to show or hide it. By
default it sits along the **bottom** of the screen; the **flip button** at the right edge
moves it to the top and back, and the **✕** button hides it. You can also flip it permanently
in Settings (Screen Edge: Bottom / Top).

**Layouts and pages.** Three physical layouts ship: **QWERTY** (default), **QWERTZ**, and
**AZERTY**. Each has pages along the bottom row:

![The letters page of the on-screen keyboard](images/keyboard-letters.png)
*The letters page — QWERTY here, shown docked at the top edge with the toolbar above it
(the default dock is the bottom edge; the flip button swaps them).*

- **ABC** — letters, with **Shift, Ctrl, Alt, Win** and **Caps** latching modifiers on the
  sides. QWERTZ and AZERTY add an **àé** page with accented characters.
- **123** — numbers; **#+=** — symbols.
- **Fn** — Esc, F1–F12, Print Screen, Home / End / PgUp / PgDn / Ins / Del, the arrow keys,
  and one-dwell shortcuts: **Copy, Paste, Cut, Undo, Redo, Select All**, and **Add Word**
  (teaches your current text to the prediction dictionary, see below).
- **😀** — the emoji keyboard (next section).

![The Fn page of the on-screen keyboard](images/keyboard-fn.png)
*The Fn page: function and navigation keys, one-dwell shortcuts, and the Add Word key.*

Handy special keys on the main pages: **Bksp** (backspace) and **DelWd** (delete whole
word). Modifier keys stay latched until you dwell them again — exactly like holding
Ctrl or Shift on a physical keyboard.

**Sizing and sound.** Keys scale from 0.5× to 2.0× (default 1.0×, Settings → Keyboard), and
a click sound plays on every key (on by default).

**Blinks are safe.** If the tracker briefly loses your eyes over a key, those moments
don't count toward its dwell — a blink can never complete a key press you didn't intend.

## 14. Emoji

Dwell the **😀** key. The keyboard switches to an emoji grid with a category bar on top:

- **🕒 Recents** comes first (your last 40 emoji), then eight categories — Smileys & Emotion,
  People & Body, Animals & Nature, Food & Drink, Activities, Travel & Places, Objects,
  Symbols — and a **🎨 skin-tone** picker at the end.
- The grid shows 40 emoji per page; ◀ / ▶ page through.
- Dwelling an emoji types it immediately and stays in emoji mode, so you can type several
  in a row. Dwell the wide **ABC** button to return to letters.

![The emoji page of the on-screen keyboard](images/keyboard-emoji.png)
*Emoji mode: the category bar across the top (Recents first), the emoji grid, and the ABC
button to return to letters.*

The 🎨 picker offers six skin tones; your choice is remembered and applied to all emoji
that support tones.

## 15. Word prediction

With prediction on (default), a **row of suggestion chips** sits across the top of the
keyboard. Dwell a chip (default 500 ms) and it replaces what you typed so far — including
whole sentences — and adds a space.

<img src="images/prediction-chips.svg" width="700" alt="Schematic: the prediction chip row with source badges and a legend">

Up to **5 chips** (1–5, default 5) merge everything EyeCtrl knows:

- the **frequency dictionary** for your language — English, German, Spanish, French, or
  Polish (detected automatically by default, or fixed in Settings → Keyboard),
- **words you typed or accepted** (your personal dictionary; manage or clear it in
  Settings → Keyboard, or add instantly with the Fn-page **Add Word** key),
- **next-word** and **phrase** suggestions learned from what you type,
- typo-tolerant matches for when the gaze keyboard hit a neighboring key, and
- **whole-sentence completions** and, if you download one, suggestions from a **language
  model** (Settings → Models: a compact ~270 MB model and a larger ~725 MB model; downloads
  are checksum-verified). Model suggestions arrive asynchronously — the keyboard never waits
  for them — and choosing **Off** simply returns you to dictionary-only prediction.

## 16. Quick Actions

Quick Actions are one-dwell shortcuts you summon from a screen edge — no toolbar trip
needed.

**Summon:** look at your **left screen edge** (default) and hold for about 400 ms. A grid of
large tiles slides out. **Dismiss:** look away, blink, dwell its ✕ button, or simply wait
(default 5 s).

**What goes in the grid** is up to you (Settings → Quick Actions): keyboard shortcuts
(Copy, Cut, Paste, Undo, Redo, Select All, Save, Find, New, Print, Show Desktop, Lock…),
media controls (play/pause, stop, next, previous, volume, mute), launching installed apps,
and window actions (minimize, maximize/restore, close, switch app). Each tile can be
enabled, disabled, or deleted; the grid is 2–5 columns wide (default 3).

## 17. Calibration

Gaze trackers drift — head position, glasses, and lighting all shift where the tracker
thinks you are looking. If the ring consistently lands slightly off where you actually look,
calibrate.

**Settings → Calibration → Calibrate Now** runs a short guided session: dots appear at
points around the screen (default 5 points); simply look at each one until it moves on.
A report shows the date and the remaining error in pixels, and the correction is stored
per monitor. **Clear Calibration** removes it again.

Also on that page:

- **Measure Gaze Offset** — a five-point probe that only *reports* how far off your gaze
  lands; it changes nothing.
- **Points, samples, hold time, settle window, target color** — tuning knobs if the standard
  session is too fast, too slow, or hard to see.

You don't need to hunt for drift yourself: when EyeCtrl notices a growing offset it shows a
brief **drift notice** at the bottom of the screen suggesting a re-calibration.

## 18. Eye care

Looking is your only input — EyeCtrl helps you protect it. Eye care is **off by default**
(Settings → Eye Care):

- **Break reminders:** every 20 minutes (default) a banner offers **Start** (begin a guided
  exercise) or **Snooze** (5 minutes by default). Breaks can also end naturally after the
  natural break length (default 30 s).
- **Guided exercises** (45 s default) move a dot for your eyes to follow — gaze exercises
  such as 20-20-20 distance breaks, directional sweeps, near–far focus, saccades, and smooth
  pursuit. Each ends with a short scored summary (accuracy, steadiness, reaction time).
- **Fatigue escalation** (experimental, off): watches for tiring gaze patterns and can
  escalate reminders when fatigue is detected (threshold default 0.75).

## 19. Adaptation

Adaptation (off by default) tunes EyeCtrl to you while you work. When it detects that you
are struggling — missed targets, abandoned dwells — it gently adjusts within limits you
control: it can lengthen or shorten dwell times, widen targeting tolerance, ease smoothing,
and enlarge dwell targets. A small cue can show when adaptation steps in, and adaptation is
automatically suppressed in sensitive contexts (like the keyboard's destructive keys).

Settings → Adaptation offers three postures — **Conservative** (default), **Balanced**,
**Responsive** — per-dimension on/off switches and safety limits, plus:

- **Reset Learned Offsets** — clears what adaptation has learned (also on the tray menu),
- **Open Adaptation Log** — a diary of what adaptation did and when,
- **Caregiver alerts** — can notify a caregiver when adaptation keeps having to intervene
  (minimum interval 10 min by default).

## 20. Themes and appearance

Four themes ship (Settings → General; a restart applies a new theme):

- **Midnight** (default) — dark, cool, cyan accents.
- **Ember** — dark and warm.
- **Daylight** — light theme for bright rooms.
- **Contrast** — maximum-contrast black and white.

Colors that follow the theme can be personalized with four swatches each (Accent, Green,
Warning, Text): the **gaze ring color** (Cursor tab), the **auto-selection highlight**
(Selection), the **calibration target** (Calibration), and the **drag trail** (Advanced).
The darkened **backdrop** behind the keyboard and menus (Acrylic by default; also Mica or
Solid Darken with its own opacity) is in Settings → Advanced.

## 21. The Settings window

Open it from the toolbar or tray. Everything is gaze-operable: rows dwell like any button,
the window fills most of the screen, and long pages **paginate** with Previous / Next page
buttons instead of scrolling — pages always show `n / m` so you know where you are.
Changes apply immediately and are saved automatically.

At the top, the mode toggle switches between:

- **Basic** — four tabs with just the everyday options (Cursor, Dwell Times, Keyboard,
  General), and
- **All settings** — every tab, for full control.

The choice is remembered. Each tab ends with a **Reset ⟨tab⟩ Settings** button, and General
additionally has **Reset ALL Settings** (advanced mode only). The window title is
"EyeCtrl Settings"; its ✕ closes it.

## 22. Settings reference

Fifteen tabs in All-settings mode. Defaults in parentheses.

### Cursor — smoothing and the ring
Filter mode (One Euro) · Smoothing factor (0.5) · Circle color (theme accent) · Ring style
(Weighted sandwich) · Circle opacity (1.0) · Circle thickness (1) · Gap interpolation
(150 ms — bridges tracker dropouts) · Position indicator (Crosshair) · Circle size
(250 px) · One Euro min cutoff (1.5) / beta (20) — the adaptive filter's responsiveness ·
Reset.

### Dwell Times
Per-surface dwell times (see [section 3](#3-gaze-control-basics) for the list) · Fixation
radius (75 px — how far gaze may drift within one fixation) · Dwell cooldown (400 ms) ·
Dwell completion sound (Off) · Reset.

### Selection
Selection mode (None) · Manual selection: circle radius (150 px), magnification (2.0×),
grace period (500 ms), hold tolerance (72 px) · Auto selection: snap search radius (48 px),
fallback to pixel (on) · Highlight color (accent) and thickness (3) · Gaze-ring appearance
inside the magnifier · Reset animation (100 ms).

### Action Menu
Ring preview · Per-action visibility: Left / Right / Double / Middle click, Drag, Scroll
Up / Down / Left / Right, Scroll Mode, Pin note, Keyboard (Middle, Scroll Left, Scroll
Right off by default) · Which actions sit on the first layer (max 4; default Left, Right,
Double, Scroll Mode) · Reset.

### Layout
Where the action menu and magnifier open: **At Gaze** or **At Center** (default At Center) ·
Reset.

### Keyboard
Active layout (QWERTY) · Key size (1.0×) · Word prediction (On) · Max predictions (5) ·
Dictionary language (auto-detect; EN/DE/ES/FR/PL) · Click sound (On) · Screen edge
(Bottom) · Learned-words list with per-word remove and Remove ALL · Reset.

### Models
Language models for prediction: download the compact (~270 MB) or large (~725 MB) model
with progress and integrity check, add a custom model by URL, delete models, switch the
active model, or turn models **Off** (dictionary-only prediction). A broken model offers a
repair re-download.

### Toolbar
Dock to screen edge (On) · Docked edge (Top) · Button size (120) · Reset.

### Calibration
Calibration points (5) · Samples per point (15) · Target hold (1000 ms) · Settle window
(300 ms) · Target color (accent) · Last-calibration status · **Calibrate Now** ·
**Measure Gaze Offset** · **Clear Calibration**.

### Eye Care
Enable (Off) · Break interval (20 min) · Natural break length (30 s) · Exercise duration
(45 s) · Banner auto-snooze (15 s) · Snooze length (5 min) · Fatigue escalation (Off) ·
Fatigue threshold (0.75) · Reset.

### Adaptation
Enable (Off) · Posture (Conservative) · Adapt dwell time / targeting tolerance / smoothing /
target size (all On) · Show adaptation cue (On) · Suppress in sensitive contexts (On) ·
Caregiver alerts (Off, min interval 10 min) · Per-dimension limits (dwell 0.40 / 0.50,
tolerance 0.50, smoothing 0.50, target size 0.50 with 100 px growth cap) · Reset settings ·
**Reset Learned Offsets** · **Open Adaptation Log**.

### General
**Sprache / Language** (System — always the first row) · Theme (Midnight; restart to apply) ·
Start on Windows Boot (Off) · Start Paused (Off) · Show Track Status (Off — a small corner
box with live tracker status) · Automatic Crash Recovery (Off; see
[section 24](#24-crash-recovery-and-the-supervisor)) · Restart Now · Reset General Settings ·
Reset ALL Settings (advanced mode only).

### Quick Actions
Enable (On) · Summon edge (Left) · Summon threshold (400 ms) · Dismiss timeout (5000 ms) ·
Grid columns (3) · Your list of actions: add (keyboard shortcut / media control / launch
app / window action), enable/disable, delete.

### Advanced
Backdrop (On, Acrylic; opacity 0.3 for Solid Darken) · Drag settle dwell (500 ms) / radius
(40 px) · Drag trail color (amber) / opacity (0.7) · Scroll sensitivity (1.0) · Repeat dwell
(600 ms) · External dwell (On, 800 ms, 400 ms cooldown — for other apps that opt into
EyeCtrl dwell) · Reset.

### Diagnostics
Record raw gaze (Off — opt-in trace of where you looked; stays on this PC) · Measure UI
timing (Off) · Open recordings folder · Reset.

## 23. Language

EyeCtrl speaks **English and German**. The language setting (Settings → General, first row;
tray; or the Supervisor window) offers **System** (follows Windows — the default),
**Deutsch**, and **English**. A restart applies a change; until then a small bilingual note
reminds you.

German currently covers the settings window, the app tray, the Supervisor, caregiver
alerts, the drift notice, track status, and startup error messages. The live overlays
(gaze ring, keyboard, action menu, quick actions) and the calibration window are still
English. The keyboard's dictionaries and sentence completions are separate from the UI
language and cover English, German, Spanish, French, and Polish.

## 24. Crash recovery and the Supervisor

For a user whose only input is gaze, an app crash can mean being stranded. The optional
**Supervisor** (a small second program, off by default) prevents that. Turn it on with
**Automatic Crash Recovery** in Settings → General (if EyeCtrl reports the Supervisor is
not installed on this PC, that toggle explains it).

While enabled:

- The app checks in with the Supervisor every few seconds.
- If EyeCtrl **crashes or hangs**, the Supervisor relaunches it automatically —
  immediately, then after 5, 15, and 60 seconds if it keeps failing (up to 3 retries
  within 2 minutes).
- If relaunching keeps failing, the Supervisor starts the **first fallback assistive
  tool it finds**, in your configured order: Tobii Dynavox **TD Control** (Computer
  Control), **Snap Core First**, or Windows built-in **Eye Control** settings — so you are
  never left without a way to communicate.
- It then shows a large dialog with **Retry** (restarts EyeCtrl, starting paused for
  safety) and **Dismiss** (stay with the fallback). A caregiver or the user can operate it.

The Supervisor has its own tray icon (tooltip shows its state) and a **caregiver setup
window** (tray → Settings, or double-click) with dwell-friendly controls: language, enable
supervision, start-on-logon, fallback order (read-only), retry and hang-threshold tuning,
and a **Launch EyeCtrl now** button.

## 25. Where your data is stored

Everything stays on this PC, under `%APPDATA%\EyeCtrl` (usually
`C:\Users\<name>\AppData\Roaming\EyeCtrl`):

| File / folder | What it holds |
|---|---|
| `settings.json` | All your settings |
| `calibration.json` | Calibration per monitor |
| `actions.json` | Your Quick Actions |
| `models\` | Downloaded prediction models |
| `logs\` | App and Supervisor logs (kept 7 days) |
| | Learned words, recent emoji, adaptation state and log |

The optional **raw gaze recording** (Diagnostics tab, off by default) writes a CSV trace of
where you looked and when — a privacy-sensitive file that never leaves the computer; delete
it at any time from the recordings folder.

## 26. Troubleshooting

Stuck inside a mode right now? Jump to [Getting unstuck](#getting-unstuck) in section 3.

| Symptom | What to do |
|---|---|
| No ring, nothing follows my eyes | EyeCtrl is in mouse mode (no tracker found) — the mouse pointer *is* the gaze point. Check that the tracker is connected and its Tobii software runs, then restart EyeCtrl. |
| The ring sits slightly off where I look | Run **Calibrate Now** (Settings → Calibration). Recurring? The drift notice will tell you too. |
| The ring lags or stutters | Increase smoothing (Cursor tab) — e.g. lower the One Euro min cutoff — and check tracker placement/lighting. |
| EyeCtrl starts paused | The **Start Paused** setting is on, or it was relaunched by the Supervisor's safe retry. Dwell **Resume** on the toolbar. |
| Second launch does nothing | By design — one instance at a time. |
| Toolbar is hidden and I can't find it | Look at its docked screen edge to summon it, or use tray → **Show toolbar** (mouse mode never auto-hides it). |
| Published exe reports missing files | Keep the `Resources` folder beside `EyeCtrl.exe`. |
| A tray toggle says the Supervisor is not installed | Crash recovery needs the Supervisor program on the PC; the toggle degrades gracefully without it. |
| Something needs investigating | Logs live in `%APPDATA%\EyeCtrl\logs` (`eyectrl-*.log`, `supervisor-*.log`), kept 7 days — attach the newest to a support request. |

Settings that misbehave can be rolled back per tab (each tab's Reset button) or entirely
(General → Reset ALL Settings), which keeps only your language and Basic/All choice.

## 27. Caregiver quick reference

| Task | Fastest path |
|---|---|
| First setup | Settings → General: language, theme, Start on Boot; Settings → Calibration: Calibrate Now |
| Everyday tuning | Settings mode toggle → **Basic** (Cursor, Dwell Times, Keyboard, General only) |
| Too many accidental clicks | Dwell Times: raise keyboard/toolbar dwells; Calibration for accuracy |
| User gets stranded on crashes | General → Automatic Crash Recovery on; configure retries in the Supervisor tray window |
| Slow typing | Keyboard: prediction on, consider a model from the Models tab; Adaptation → Responsive |
| Eye strain | Eye Care on; adjust break interval and exercise length |
| Wipe personal data | Delete the files listed in [section 25](#25-where-your-data-is-stored) |
| Try without a tracker | Launch with `--test-mode` — mouse mode, no splash |

---

*EyeCtrl is a private project. Interface labels, defaults, and features described here
match the current `main` branch of the source repository.*
