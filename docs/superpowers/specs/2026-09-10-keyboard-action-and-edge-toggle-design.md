# Keyboard action-menu action + quick Top/Bottom switch — design

Date: 2026-09-10
Status: awaiting owner review
Brainstorm session: owner dismissed the clarifying-question round; every open choice is
encoded in the decision ledger (§5) and is vetoable. Point review comments at ledger
numbers.

## 1. Problem

Two gaps reported by the owner:

1. The action-menu ring cannot open the gaze keyboard. The only live path to the keyboard
   is the toolbar's Keyboard button (`ToolbarViewModel.OnTargetActivated` case
   `"Keyboard"` → `KeyboardToggledMessage`). `ShowKeyboardRequestMessage` /
   `HideKeyboardRequestMessage` exist in `KeyboardCoordinator` but have no production
   senders (reserved text-field seam).
2. Moving the keyboard between screen edges requires Settings → Keyboard → Screen Edge —
   several dwells each way, and the round trip is repeated whenever the keyboard occludes
   what the user is reading or typing into.

### 1.1 Constraints discovered in exploration

- **C1 — ring chain is fully tooled.** Adding an `InteractionAction` member automatically
  gets: a Settings visibility toggle and a "on first layer" toggle
  (`ActionMenuPageSource` enumerates the enum), the icon-map no-default throw in
  `ActionMenuViewModel.GetActionIcon` (and `ActionMenuViewModelSymbolTests.
  EveryAction_MapsToADistinctIcon`), and the dispatch switch in
  `KeyboardInteractionDispatcher.HandleActionRequested`.
- **C2 — one-shot migration pattern is established.** `ActionMenuSettings.DefaultsMigrated`
  / `PrimaryMigrated` and `ToolbarSettings.SizeMigrated` all migrate a pre-existing
  settings file exactly once; the migration adds things that did not exist when the file
  was written (so no "re-add what the user removed" hazard).
- **C3 — toolbar is force-shown while the keyboard is up.**
  `KeyboardCoordinator.ApplyModalKeyboardState` suspends auto-hide;
  `ToolbarAutoHideController.SetSuspended(true)` force-recovers from hidden "so the user
  isn't stranded". So a toolbar-hosted toggle is reachable exactly when keyboard position
  matters.
- **C4 — Top-edge collision strands the user.** Keyboard and toolbar both position via
  `OverlayPositioning.PlaceAtEdge(workArea, edge, w, h)`; at `"top"` both start at
  `workArea.Top`. A keyboard docked Top covers the force-shown toolbar strip — and the
  toolbar holds the keyboard's only hide button. Default config (toolbar Top, keyboard
  Bottom) avoids this today; a naive edge toggle reintroduces it.
- **C5 — snapshot discipline.** `KeyboardStackSnapshot` includes `ScreenEdge`
  (`SettingsSnapshots.cs`). Mutating `KeyboardSettings.ScreenEdge` without refreshing
  `KeyboardCoordinator._keyboardSnapshot` plants a phantom diff: the next Settings apply
  rebuilds the whole keyboard + prediction stack for nothing (the exact bug class the
  `ApplyEmojiSkinTone` remarks document).
- **C6 — settings saves don't notify.** `SettingsService.SaveDebounced` writes the file;
  only the Settings window's own `SettingsChanged` event reaches
  `SettingsUiCoordinator.OnSettingsChanged`. A runtime toggle that saves debounced and
  applies live itself triggers no rebuild path.
- **C7 — toolbar button wiring is drift-guarded.** `ToolbarButtonDriftTests` forces every
  view-model button tuple to be rendered by `ToolbarBand` (or listed in
  `ReachableElsewhere`) and to have an activation `case`. Adding a button is safe against
  half-wiring.

## 2. Goals / non-goals

**Goals**

- G1: Open the gaze keyboard from the action-menu ring (one activation, any state the ring
  can be opened from).
- G2: Flip the keyboard Top ↔ Bottom in one dwell, from a surface that is reachable while
  the keyboard is visible.
- G3: Both changes persist and live-apply without restart and without a keyboard rebuild.

**Non-goals**

- NG1: Left/Right keyboard edges (the setting is a validated Top/Bottom string enum;
  widening it is a separate question).
- NG2: A per-window/situational automatic edge chooser.
- NG3: Moving the toolbar's Left/Right dock options anywhere — Settings keeps them.
- NG4: Any change to `InteractionManager`'s state machine. The keyboard action needs no
  Core state, exactly like PinNote.

## 3. Feature A — action-menu "Keyboard" action

### 3.1 What the user sees

A "Keyboard" target on the ring, behind **More** by default (layer 2), with a drawn
keyboard glyph. Activating it closes the menu and shows the gaze keyboard at its current
edge, with the same modal semantics as the toolbar toggle (fixation suppression, toolbar
auto-hide suspended, quick-actions summon suspended). If the keyboard is already visible,
the action is an idempotent no-op (it never hides).

### 3.2 Changes, in dependency order

1. **`InteractionAction.Keyboard`** (`src/EyeCtrl.Core/Models/InteractionAction.cs`):
   new member, summary "Show the on-screen gaze keyboard."
2. **`ActionIcon.Keyboard`** (`src/EyeCtrl.Core/Models/ActionIcon.cs`) + drawn path in
   `EyeCtrl.Controls/Icons/ActionIcons.cs`: rounded-rect keyboard outline, two dot rows,
   space bar — same hand-drawn path style as the rest of the set.
3. **`ActionMenuViewModel`**: `GetActionIcon` maps `Keyboard → ActionIcon.Keyboard`
   (no-default throw until done; symbol test enforces), `FriendlyName` → `"Keyboard"`.
4. **`ActionMenuSettings`**: add `Keyboard` to the `VisibleActions` initializer and to the
   `EnsureDefaults` array. New one-shot flag `_keyboardActionMigrated` +
   `EnsureKeyboardActionOnce()` (C2 pattern): adds `Keyboard` to `VisibleActions` if
   absent, once per settings file. **Not** added to `PrimaryActions` — the four-slot cap
   (`MaxPrimaryActions`, ring aimability) is full by default and Validate() rejects five.
   Startup caller wires `EnsureKeyboardActionOnce()` next to the existing
   `EnsureDefaultsOnce()` / `MigratePrimaryOnce()` calls.
5. **`KeyboardShowRequestedMessage`** (new record, `EyeCtrl.ViewModels/Messages/
   InteractionMessages.cs`, beside `KeyboardToggledMessage`; no payload).
6. **`KeyboardCoordinator.OnInitialize`** registers it:
   `SetKeyboardVisible(true, manageSuppression: true)` guarded by an
   `if (_modal.KeyboardVisible) return;` early-out (idempotent show).
   `manageSuppression: true` is load-bearing: a show that leaves free-look active lets the
   next fixation pop an action menu over the keyboard. This deliberately does NOT reuse
   `ShowKeyboardRequestMessage`, whose handler is `manageSuppression: false` (reserved
   text-field seam — see ledger #2).
7. **`KeyboardInteractionDispatcher.HandleActionRequested`**: intercept
   `msg.Action == InteractionAction.Keyboard` before the click switch (PinNote
   precedent), `Messenger.Send(new KeyboardShowRequestedMessage())`, then fall through to
   the shared target-highlight cleanup at the bottom of the method (unlike PinNote, which
   duplicates it before `return`). Core `InteractionManager.OnActionSelected` needs no
   change: `Keyboard` flows through `ActionRequested` like a click; the point is unused.
8. **`ActionMenuPageSource`**: add
   `InteractionAction.Keyboard => "Opens the gaze keyboard for typing."` to
   `ActionDescription` — `SettingsRowDescriptionCompletenessTests` fails on the null arm.
   Add the explicit `FriendlyName` case ("Keyboard") for symmetry with the other ten
   (the `_ => action.ToString()` arm already yields the same string).

### 3.3 Rejected surface variants (see ledger #1)

Toggle semantics (would hide when already visible); reusing `KeyboardToggledMessage`;
Core-side special-casing like `ScrollMode` (needs no gaze anchor, so no state).

## 4. Feature B — quick keyboard Top/Bottom switch

### 4.1 What the user sees

A new toolbar button directly beside Keyboard: a keyboard glyph with up/down swap arrows.
One dwell flips the keyboard between Top and Bottom immediately. The toolbar relocates to
the opposite vertical edge **only when it would otherwise be covered** (C4). Works whether
the keyboard is currently visible or hidden (pre-positioning); works in emoji mode (the
toggle is window-level).

### 4.2 Changes, in dependency order

1. **`KeyboardEdgeToggledMessage`** (new record, no payload, same file as above).
2. **`ToolbarViewModel`**: button tuple `("KeyboardEdge", "KeyboardEdge")` immediately
   after `("Keyboard", "Keyboard")`; activation `case "KeyboardEdge": Messenger.Send(new
   KeyboardEdgeToggledMessage()); break;`.
3. **`ToolbarIconKey.KeyboardEdge`** + `ToolbarIcons` path: keyboard outline with up/down
   arrows (swap motif; distinct from the plain `Keyboard` glyph).
4. **`ToolbarBand`**: render tuple in the matching position (C7 guard forces VM/band/case
   consistency).
5. **`KeyboardCoordinator`** registers `KeyboardEdgeToggledMessage` →
   `HandleKeyboardEdgeToggled`, which:
   a. Computes the plan via a pure, headless-testable function:
      ```csharp
      internal static (string NewKeyboardEdge, string? NewToolbarEdge) KeyboardEdgeTogglePlan(
          string currentKeyboardEdge, bool toolbarDocked, string toolbarDockedEdge)
      ```
      Flip `"Top"` ↔ `"Bottom"` (anything other than `"Top"` reads as Bottom — same
      normalization as `KeyboardWindow.RepositionOnScreen`). Relocate the toolbar iff
      `toolbarDocked && toolbarDockedEdge` equals the keyboard's NEW edge; the new
      toolbar edge is the opposite vertical edge. Undocked / Left / Right / already-clear
      toolbars return `null` and are untouched.
   b. Applies: set `settings.Keyboard.ScreenEdge` (the observable property —
      `KeyboardWindow.SizeChanged → RepositionOnScreen` (KeyboardWindow.xaml.cs:86) plus
      `SetScreenEdge` handles placement; if the keyboard has never been shown this
      session the field updates and the first layout places it correctly). If the plan
      relocates the toolbar: set `settings.Toolbar.DockedEdge` (the observable setter
      auto-notifies `QuickActionsCoordinator`'s DockedEdge watcher, which re-aims the
      hot-edge summon) and mirror `SettingsUiCoordinator.OnSettingsChanged`'s live-apply
      trio — `Toolbar.SetMode(IsDocked, DockedEdge)`, `ToolbarLifecycleCoordinator.
      UpdateEdge`, `PositionIndicator`.
   c. Persists: `Context.SettingsService.SaveDebounced(settings)` (C6 — no notification
      path fires, so no rebuild).
   d. Refreshes `_keyboardSnapshot = KeyboardStackSnapshot.Capture(settings)` (C5 —
      kills the phantom rebuild on the next Settings apply; same discipline as
      `ApplyEmojiSkinTone`, whose remarks document the failure mode).

### 4.3 Rejected variants (see ledger #5–#6)

On-keyboard frame button (the keyboard has no button chrome today — no close button, no
control row — so this needs new non-key dwell plumbing inside `KeyboardWindow`); a ring
action (position is a global UI concern, not point-targeted); always-swapping keyboard +
toolbar together (moves the toolbar when nothing collides); nudging the keyboard below
the toolbar (couples keyboard geometry to toolbar Size, and the keyboard then isn't
really at the edge).

## 5. Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|----------|-----------|----------|
| 1 | Ring action semantics: **show, idempotent** (never hides) | "click and open the keyboard"; an accidental hide from the ring strands the typing flow; hiding already costs one dwell on the toolbar | Toggle semantics; reuse of `KeyboardToggledMessage` |
| 2 | New `KeyboardShowRequestedMessage` handled with `manageSuppression: true` | Must match the toolbar toggle's modal semantics (fixation suppressed, summon suspended) or the next fixation opens a menu over the keyboard | Reusing `ShowKeyboardRequestMessage` — its handler is deliberately `manageSuppression: false` for the text-field seam |
| 3 | Default **on** (layer 2, behind More) + one-shot migration `KeyboardActionMigrated` | The action never existed, so no user could have removed it — the one-shot add is the established safe pattern (C2); layer 2 because the primary cap (4) is a validated aimability constraint and is full | Opt-in only (feature ships invisible to existing users); primary-layer default (Validate rejects 5) |
| 4 | Dispatch intercepts in `KeyboardInteractionDispatcher` before the click switch | PinNote precedent exactly; zero Core state-machine changes; the target point is genuinely unused | A Core special-case like ScrollMode (which exists because scroll needs an anchor point) |
| 5 | Quick switch scope: **keyboard flips; toolbar relocates only on collision** (same vertical edge, docked) | C4: without relocation, keyboard-at-Top covers the toolbar that holds the keyboard's only hide button — a strand-the-user bug, not a cosmetic overlap | Always-swap-both (needless surprise); keyboard-only (stranding); nudge-below-toolbar (geometry coupling) |
| 6 | Quick switch surface: **toolbar button beside Keyboard** | C3: the toolbar is force-shown while the keyboard is up, so the toggle is reachable exactly when it matters; the button system is drift-guarded (C7) and costs one tuple + case + icon | On-keyboard button (new dwell plumbing + chrome the keyboard lacks); ring action (not point-targeted); a new QuickActionType (heavier editor + preset surface) |
| 7 | Persistence: mutate the live settings object + `SaveDebounced` + live apply + snapshot refresh | C5/C6: correct across restart, no rebuild now, no phantom rebuild later | Settings-window round trip (the thing being removed); direct window calls without persisting (reverts on restart) |
| 8 | Undocked toolbar: **no relocation**, overlap accepted | Undocked is a deliberate floating placement the user owns; docking it as a side effect of a keyboard flip is a bigger surprise than an overlap in a rare mode | Relocating/docking an undocked toolbar |

## 6. Testing

- **Core (`EyeCtrl.Core.Tests`)**
  - `ActionMenuSettings.EnsureKeyboardActionOnce`: adds `Keyboard` when absent; skips when
    already present (no duplicate); never re-runs after the flag sets; default
    `VisibleActions` contains `Keyboard`; default `PrimaryActions` does not.
  - `Validate()` unchanged behavior (4-cap still enforced).
- **ViewModels (`EyeCtrl.ViewModels.Tests`)**
  - Existing `ActionMenuViewModelSymbolTests.EveryAction_MapsToADistinctIcon` covers the
    new mapping automatically (fails until the icon case exists).
  - `FriendlyName(Keyboard)` = "Keyboard".
- **App (`EyeCtrl.App.Tests`)**
  - `KeyboardEdgeTogglePlan` truth table (headless, pure): Bottom→Top and Top→Bottom;
    invalid/non-"Top" string reads as Bottom; toolbar docked-same-edge → relocated to the
    opposite; docked-other / Left / Right / undocked → `null`.
  - `HandleKeyboardEdgeToggled` thin-apply seam: assert settings mutation + save + snapshot
    refresh via the same headless style as `ApplyEmojiSkinTone` tests (real window objects
    stay out of reach — the plan function carries the logic).
  - `KeyboardShowRequestedMessage` handler: idempotent early-out when
    `ModalInteractionState.KeyboardVisible` already true.
  - `ToolbarButtonDriftTests` (existing) enforces the new button's VM/band/case wiring.
  - `ActionMenuPageSource` description completeness gate (existing) fails until the
    description string lands.
- **Manual / UI-gated (owner-run)**: ring → Keyboard opens the keyboard with suppression;
  toolbar button flips the visible keyboard; toolbar jumps only on collision.

## 7. Risks

- **R1 — icon quality.** Two new hand-drawn paths (ring + toolbar). Both sets are
  hand-authored geometry; the plan should render-check at real size (the repo's
  established visual-review step).
- **R2 — toolbar button count.** The band gains one button (currently ~6 rendered). At
  default Size 120 with 12-px margins this fits the band; extreme Size + narrow screens
  were already tight before. No action taken (YAGNI) beyond noting it.
- **R3 — settings window staleness.** If the Settings window is open while the toolbar
  toggle fires, its Screen Edge / Docked Edge rows show stale values until rebuilt. Same
  staleness class as every other runtime mutation (e.g. skin tone); accepted.
- **R4 — `KeyboardEdgeTogglePlan` normalization.** "anything not Top is Bottom" mirrors
  `KeyboardWindow.RepositionOnScreen`; the plan function must not invent a third state.
