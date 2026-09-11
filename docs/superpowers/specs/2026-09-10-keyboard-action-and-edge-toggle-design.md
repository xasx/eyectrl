# Keyboard action-menu action + quick Top/Bottom switch — design

Date: 2026-09-10
Status: owner-approved design, no open items (round 5 — mockup rounds settled: shared
right-edge bar for Close + Flip, bar tracks the keyboard's edge, ledger #15 mechanism B)
Brainstorm session: owner dismissed both clarifying-question rounds; every choice is
encoded in the decision ledger (§5) and is vetoable. Point review comments at ledger
numbers.

Owner feedback rounds: (1) 2026-09-10 the flip must not rely on the toolbar being
force-pinned — either the button lives elsewhere, or the toolbar stays auto-hide and the
keyboard slides to give a summoned toolbar room. (2) Mockup round: both functions belong
in a toolbar, Close on the right side — not on the keyboard ("in the keyboard mis dwells
are possible"), not the main toolbar: "a separate toolbar on the right side, like in
action view". This revision: B4 = the shared right-edge bar (Close + Flip), B2/B3
unchanged (toolbar un-pins, keyboard yields).

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
  settings file exactly once. Of the three, only `EnsureDefaultsOnce` is adds-only;
  `MigratePrimaryOnce` replaces the whole list and `MigrateSizeOnce` rewrites an existing
  value. The new flag follows the adds-only shape, which is what makes it safe: the
  action did not exist when any existing file was written, so there is no "re-add what
  the user removed" hazard.
- **C3 — toolbar is force-pinned while the keyboard is up (behavior this design REMOVES),
  and the keyboard currently has no exit of its own.** Showing the keyboard sets
  `GazeEngine.SuppressFixation` (`KeyboardCoordinator` show path), which gates the
  fixation detector (`GazeEngine.cs:538`); `OnFixationStarted` is the only trigger of
  `ShowActivationButton` (`InteractionManager.cs:66`), so **while the keyboard is up the
  action-menu ring cannot be opened** — and nothing else can hide the keyboard:
  `ShowKeyboardRequestMessage`/`HideKeyboardRequestMessage` have no senders, the toolbar
  Keyboard button is the only toggle, and no layout declares a hide key. The pin's
  "stranded" comment is therefore half-right: with the pin removed, the summoned toolbar
  becomes the sole exit, reachable only via off-edge gaze with valid samples
  (`ToolbarAutoHideController.IsPastDockedEdge` rejects invalid samples). The design
  removes the pin anyway — safely — by giving the keyboard session its own edge bar with
  Close (B4).
  The `Feed` subscription that makes summoning work at all is wired whenever a real
  tracker is active, independent of the modal flags (`ToolbarLifecycleCoordinator.cs:202`,
  gated on `providerIsRealTracker` at :168 — under the Mouse provider there is no
  subscription, so summon behavior is unobservable on a dev box). The settings-window
  suspension (`SettingsUiCoordinator.cs:117`) is a separate caller and stays.
- **C4 — same-edge collision is handled by the keyboard yielding, not by moving the
  toolbar.** Keyboard and toolbar both position via `OverlayPositioning.PlaceAtEdge`;
  at the same vertical edge they overlap. With the pin removed, the toolbar is only on
  screen while gaze-summoned; when it appears onto the keyboard's edge the keyboard
  slides toward screen center by the toolbar's extent and slides back when the toolbar
  hides (owner's explicit direction). The toolbar never relocates.
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
- G2: Flip the keyboard Top ↔ Bottom in one dwell, and dismiss it in one dwell, both from
  a dedicated right-edge bar that is visible exactly while the keyboard is (owner:
  "a separate toolbar on the right side, like in action view") — never from anything
  dwellable on the keyboard itself.
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
edge, with the same modal semantics as the toolbar toggle (fixation suppression,
quick-actions summon suspended). If the keyboard is already visible, the action is an
idempotent no-op (it never hides).

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

The keyboard itself is untouched — no new chrome, nothing dwellable added on or beside
the keys. While the keyboard is up, the app's **shared right-edge bar** (the same strip
that shows Close/Help beside the action menu, Cancel during drags, Stop during scroll
mode) shows a keyboard pair instead: **Close** and **Top/Bottom Flip**, right screen
edge, vertically centered, labels always on. The bar appears when the keyboard appears
and disappears when it hides, so Close is always exactly one dwell away — no toolbar
summon, nothing in the typing field (mis-dwell concern). The toolbar gets **no new
button** and is **no longer force-pinned** while the keyboard is up: it auto-hides and
edge-summons on demand. When the toolbar is summoned onto the keyboard's edge, the
keyboard slides toward screen center to make room and slides back when the toolbar
hides. The edge bar works in emoji mode too (it is not part of the keyboard window).

### 4.2 Changes, in dependency order

**B1 — the flip itself**

1. **`KeyboardEdgeToggledMessage`** (new record, no payload, `InteractionMessages.cs`
   beside `KeyboardToggledMessage`). Its sender is the edge bar's Flip button (B4).
2. **`KeyboardCoordinator`** registers it → `HandleKeyboardEdgeToggled`:
   a. Pure, headless-testable flip: `internal static string FlipScreenEdge(string edge)`
      — comparison case-insensitive (mirroring `RepositionOnScreen`), but the return
      value is always exactly `"Top"` or `"Bottom"`: validation is a case-sensitive
      membership test (`KeyboardSettings.cs:51`), `SettingsService.Save` throws on
      validation failure, and `App.OnExit`'s final save is unguarded — a lowercase write
      would crash the app on exit.
   b. Applies, imperatively: set `settings.Keyboard.ScreenEdge = flipped` **and call
      `Context.OverlayManager.Keyboard.SetScreenEdge(flipped)`**. The call is
      load-bearing: `SetScreenEdge` has exactly one caller today (`LoadKeyboard`,
      KeyboardCoordinator.cs:125) and nothing observes
      `KeyboardSettings.PropertyChanged`, so the settings write alone moves nothing —
      and the `SizeChanged → RepositionOnScreen` hook only re-applies whatever
      `SetScreenEdge` last stored, so it cannot substitute either. (If the keyboard has
      never been shown this session, `RepositionOnScreen` no-ops on zero size, but
      `_screenEdge` is stored and the first layout places the window correctly.)
   c. `Context.SettingsService.SaveDebounced(settings)` (C6 — no notification path fires,
      so no rebuild).
   d. Refreshes `_keyboardSnapshot = KeyboardStackSnapshot.Capture(settings)` (C5 —
      kills the phantom rebuild on the next Settings apply; note this also closes the
      `ReloadIfChanged` route that would otherwise have applied the edge on the next
      Settings apply, which is exactly why step (b) must apply it directly).

**B2 — un-pin the toolbar while the keyboard is up** (owner direction)

3. **`KeyboardCoordinator.ApplyModalKeyboardState`**: delete the
   `_toolbar.SetAutoHideSuspended(visible)` call. The method keeps the quick-actions
   summon suspension (`_quickActions.SetSummonSuspended(visible)` — the hot-edge grid
   must not pop over the keyboard) and the `_modal.KeyboardVisible` flag. Incidentally
   removes a pre-existing interleaving: both Keyboard and Settings coordinators write
   the same un-refcounted suspension bool, so closing the keyboard while the Settings
   window was open used to un-pin the toolbar out from under it. The Settings-window
   suspension (`SettingsUiCoordinator.cs:117`) is untouched.
   Toolbar buttons remain dwellable while summoned — but via the messenger path, not
   the direct route: `GazeEngine.CursorMoved` fires unconditionally →
   `GazeCursorMovedMessage` → `ToolbarViewModel.Receive` feeds the dwell detector only
   when `_dwellingActive`, which the show transition sets
   (`ToolbarVisibilityAdapter.ShowToolbar` → `_toolbarVm.SetActive(true)`).
   `ToolbarWindow.UpdateGaze` feeds no detector (it only initializes target positions).
4. **Comments and docs this change invalidates** (required edits, not optional):
   - `KeyboardCoordinator.cs:177-187` — `ApplyModalKeyboardState` doc: "the two
     cross-cluster consumers: the toolbar auto-hide and quick-actions summon suspend
     seams" becomes one consumer; the "Order matters — suspend the controllers BEFORE
     publishing the flag" rationale (written about the auto-hide race) must be
     re-evaluated against the surviving quick-actions call.
   - `KeyboardCoordinator.cs:41-42` — ctor rationale "All three are dereferenced
     unconditionally by ApplyModalKeyboardState": `_toolbar` is dereferenced only at
     :190 today; after B2 its KeyboardCoordinator uses shift to the B3 subscription.
   - `ToolbarAutoHideController.cs:109` — "Recover from hidden so the user isn't
     stranded while a modal is up": after B2 this covers only the Settings modal; the
     comment must say so (the keyboard's own escape is B4, not the pin).
   - `docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md:140,:166` — the
     shipped design states "Keyboard or settings window open — auto-hide is suspended
     while either is open … force-shown so the user is not stranded". Add a
     supersession note pointing here (the §13-supersession pattern from the
     struggle-adaptation spec).
   - `src/EyeCtrl.Controls/Icons/ToolbarIconKey.cs` class doc — "that set is
     deliberately all mice" becomes false when Feature A adds `ActionIcon.Keyboard`.

**B3 — the keyboard yields to a summoned toolbar** (owner direction)

5. **Toolbar visibility transitions become observable — via an event, not a messenger,
   and edge-detected.** `ToolbarVisibilityAdapter` is stateless and is also called
   non-transitionally (re-asserts dominate: `SetPaused`/`ReassertVisibility` call
   `HideToolbar`/`SetActive` repeatedly). Give it a `bool _lastVisible` edge-detect and
   raise `event Action<bool>? VisibilityChanged` **inside each method's dispatcher
   lambda, after the window call** — firing there keeps the signal on the UI thread
   (the controller calls from the gaze thread) and deduplicates re-asserts. An event
   rather than a constructor-injected messenger keeps the 3-arg ctor (three
   `ToolbarVisibilityAdapterTests` sites construct it; a required 4th parameter is a
   compile break in the test assembly).
   `ToolbarLifecycleCoordinator` re-exposes the event. NOTE: the adapter is NOT a
   single funnel — `App.ShowOverlays` shows the toolbar directly
   (`App.xaml.cs:478 toolbar.Show()`) and never passes through it.
6. **`KeyboardWindow.SetToolbarOccupancy(bool occupied, string edge, double
   extentDip)`**: stores the occupancy; `RepositionOnScreen` applies a yield offset when
   `occupied && edge` equals the keyboard's effective edge — `Top += extent` for the top
   case, `Top -= extent` for bottom. Extent = the toolbar window's `ActualHeight` in
   DIP: both windows position in DIP through
   `PlaceAtEdge(SystemParameters.WorkArea, …)`, so DIP is the consistent measure — an
   implementer must NOT reach for the coordinator's cached toolbar rect, which is
   physical pixels. Only Top/Bottom edges yield (a Left/Right dock is orthogonal —
   ledger #8). Clamp the extent defensively so a pathological measurement cannot push
   the keyboard off-screen. Instant reposition; no animation in v1.
7. **`KeyboardCoordinator`** subscribes to the visibility event and forwards occupancy
   to `KeyboardWindow`. For the initial state on keyboard show, query the window
   directly — `Context.OverlayManager.Toolbar.IsVisible` — NOT a tracked adapter flag:
   the adapter may have observed no transition at all (startup show bypasses it, and
   the settings-open suspension force-shows only via the controller's state), so a
   tracked flag can read "not occupied" while the toolbar is on screen.
8. **Safety of moving the keyboard under gaze:** `KeyboardWindow.UpdateGaze` recomputes
   key screen positions (`UpdateKeyScreenPositions`), prediction target positions, and
   emoji category targets on **every sample**, so dwell targets track window moves by
   construction — no target re-registration pass is needed. A slide under an
   in-progress key dwell lets the nearest-key logic re-acquire naturally (documented,
   accepted; the yield fires when gaze is at the screen edge summoning, the return when
   gaze is on keys — a top-row dwell may re-acquire once).

**B4 — the keyboard's edge bar (Close + Flip)** (owner round-4 direction)

9. **Reuse the shared edge-button bar, not new chrome — and the bar tracks the keyboard
   (ledger #15).** New static `KeyboardEdgeButtons` mirroring `ActionMenuEdgeButtons`
   (the menu's Close/Help pair), but keyboard-edge-dependent:
   `Definitions(int dwellMs, bool keyboardAtTop)` returns two `EdgeButtonDefinition`s at
   a right-edge anchor that sits **toward the keyboard's edge** — `ScreenGeometry`
   gains a vertical-position variant of `RightEdgeAnchor` (default offset ≈ 75%/25% of
   work-area height, exact constant plan-level):
   - **Flip** in the keyboard-facing slot (`Label: "Flip"`,
     `Icon: ActionIcon.KeyboardEdge`, new glyph — keyboard with up/down arrows);
     activation sends `KeyboardEdgeToggledMessage` (B1).
   - **Close** screen-center-ward (`Icon: ActionIcon.Close` existing, `Label: "Close"`);
     activation sends `KeyboardHideRequestedMessage`.
   - Labels always on for both — the `ActionMenuEdgeButtons` convention: "the escape
     hatch cannot rely on being recognised". Dwell time: the keyboard's own key dwell
     (`Dwell.KeyboardKeyDwellMs`) — the bar is part of the keyboard session. Telemetry
     context `DwellContext.EdgeButtonBar` already exists.
   - On flip, `HandleKeyboardEdgeToggled` re-runs `ShowButtons` with the new
     edge-dependent set and anchor; vacated positions carry no dwell target (no
     dwell-transfer hazard). The anchor follows the keyboard's edge **setting**, not
     the B3 yield offset.
10. **Show/hide wiring in `KeyboardCoordinator.SetKeyboardVisible`:** show →
    `Context.OverlayManager.EdgeButtonBar.ShowButtons(KeyboardEdgeButtons.Definitions(
    dwellMs), anchor)`; hide → `Hide()` **only if the bar still shows our definitions**
    (`DefinitionFor(KeyboardEdgeButtons.CloseId) is not null`) — mirroring the guarded
    hand-off at `OverlayMessageCoordinator.cs:168`. While the keyboard is up no other
    context can take the bar (ring, manual selection, drag and scroll all start from
    the menu, which cannot open — C3). The reverse overlap — keyboard opened
    mid-scroll/mid-drag while the stop/cancel button owns the bar — must be traced in
    the plan; it is the same hand-off dance documented at
    `OverlayMessageCoordinator.cs:153-169`.
11. **Gaze needs no new routing:** the bar's dwell runs off `GazeCursorMovedMessage`
    (`DwellOverlayViewModelBase`), which flows throughout the keyboard session —
    `SuppressFixation` gates only the fixation detector (GazeEngine.cs:538), not the
    cursor stream. Close and Flip are dwellable the whole time the keyboard is up.
12. **Messages:** `KeyboardHideRequestedMessage` (new) handled by `KeyboardCoordinator`
    → `SetKeyboardVisible(false, manageSuppression: true)` — releasing suppression
    (re-enabling fixation and the ring) is the point; the symmetric twin of Feature A's
    `KeyboardShowRequestedMessage`, not a reuse of the sender-less
    `HideKeyboardRequestMessage` (reserved text-field seam,
    `manageSuppression: false`).

### 4.3 Rejected variants (see ledger #6, #9, #12)

A **keyboard-frame control column** (the round-3 design): the owner rejected anything
dwellable on or beside the keys — "in the keyboard mis dwells are possible". Buttons on
the **main toolbar** (the round-2 design and the round-4 first reading): owner wants the
band untouched; it is also at its vertical-dock layout limit and would put the keyboard's
exit behind a summon round trip. Toolbar relocation on collision (round-1 design — the
toolbar never moves now); always-swapping keyboard + toolbar together; static nudge
below a pinned toolbar; a ring action for the flip (position is a global UI concern, not
point-targeted); a new QuickActionType (heavier editor + preset surface); a
`hideKeyboard` special-action key in the layout JSONs (three layouts × every sublayout,
and the emoji grid is built in code, not layout).

## 5. Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|----------|-----------|----------|
| 1 | Ring action semantics: **show, idempotent** (never hides) | "click and open the keyboard"; an accidental hide from the ring strands the typing flow; hiding already costs one dwell on the toolbar | Toggle semantics; reuse of `KeyboardToggledMessage` |
| 2 | New `KeyboardShowRequestedMessage` handled with `manageSuppression: true` | Must match the toolbar toggle's modal semantics (fixation suppressed, summon suspended) or the next fixation opens a menu over the keyboard | Reusing `ShowKeyboardRequestMessage` — its handler is deliberately `manageSuppression: false` for the text-field seam |
| 3 | Default **on** (layer 2, behind More) + one-shot migration `KeyboardActionMigrated` | The action never existed, so no user could have removed it — the adds-only one-shot pattern (C2) is safe; layer 2 because the primary cap (4) is a validated aimability constraint and is full. The second layer grows 5 → 6 targets (neighbour gap ~160 → ~115 DIP at default CircleSize per ActionMenuSettings.cs:34-37) — inside the documented-safe envelope, but the plan should render-check the ring | Opt-in only (feature ships invisible to existing users); primary-layer default (Validate rejects 5) |
| 4 | Dispatch intercepts in `KeyboardInteractionDispatcher` before the click switch | PinNote precedent exactly; zero Core state-machine changes; the target point is genuinely unused | A Core special-case like ScrollMode (which exists because scroll needs an anchor point) |
| 5 | Collision handled by **keyboard yielding** to a summoned toolbar; the toolbar never relocates | Owner round-1 direction ("the keyboard can slide to give room to the toolbar"); no persisted toolbar change, no DockedEdge writes, summon edge stays where the user learned it | Toolbar relocation to the opposite edge (round-1 design, superseded); static nudge below a pinned toolbar |
| 6 | Close + Flip surface: **the shared right-edge bar**, shown exactly while the keyboard is (B4) | Owner round-4: "a separate toolbar on the right side, like in action view". Reuses the context-driven `EdgeButtonBar` machinery (`ActionMenuEdgeButtons` pattern) almost wholesale; sits at the screen edge, far outside the typing field (mis-dwell concern); visible-with-keyboard makes Close an unconditional one-dwell escape | Keyboard-frame control column (round-3 — rejected: "in the keyboard mis dwells are possible"); the main toolbar (round-2/round-4 first reading — owner wants the band untouched; exit would sit behind a summon); ring action for the flip (not point-targeted); a new QuickActionType; a `hideKeyboard` layout special-action key |
| 7 | Persistence: mutate the live settings object + **explicit `SetScreenEdge` call** + `SaveDebounced` + snapshot refresh | C5/C6: the settings write alone moves nothing (no `PropertyChanged` observer; `SizeChanged` re-applies only the last stored edge), and the snapshot refresh deliberately closes the `ReloadIfChanged` back door — so the direct call is the only apply (B1 2b) | Settings-window round trip (the thing being removed); relying on the phantom-diff rebuild; direct window calls without persisting (reverts on restart) |
| 8 | Toolbar docks that don't yield: **Left/Right (orthogonal) and undocked/floating** | A side strip crossing a wide keyboard is transient (toolbar visible only while summoned); an undocked toolbar raises no visibility transitions at all (auto-hide disabled, `Feed` early-returns), so it cannot drive the yield — and the edge-bar Flip is the escape: the user moves the keyboard instead. Floating placement is user-owned | Horizontal yield for side docks; docking a floating toolbar as a side effect |
| 9 | **Un-pin the toolbar while the keyboard is up** (remove the keyboard path's auto-hide suspension only) — safe because the edge-bar Close is visible exactly while the keyboard is | Owner round-1: the pinned strip eats typing space. The pin's "stranded" rationale is answered by the edge bar, not the summon: while the keyboard is up the ring is unreachable (C3) and `IsPastDockedEdge` rejects invalid samples, so without the bar the summon would be the sole escape | Keep the pin; un-pin with no on-bar escape |
| 10 | Keyboard yield: **instant reposition, no animation, occupancy consulted on keyboard show (queried from the window's `IsVisible`)** | Minimum that satisfies "slides to give room"; per-sample target recompute (B3 #8) makes the move safe; animation is polish, not function. The query must read the window, not a tracked adapter flag — the startup show bypasses the adapter entirely (B3 #5) | Animated slide in v1; trusting an adapter-tracked flag for the initial state; yield only after show (would mis-place a keyboard opened onto a summoned toolbar) |
| 11 | Quick-actions hot-edge summon stays **suspended** while the keyboard is up | Un-pinning applies to the toolbar only; the quick-actions grid popping over the keyboard is unwanted and unrelated to the owner's complaint | Also un-suspending the quick-actions summon |
| 12 | Edge-bar mechanics: **`KeyboardEdgeButtons` context set on the shared bar, guarded hide, keyboard key dwell, labels always on** | The bar is already context-driven (menu Close/Help, drag Cancel, scroll Stop) with a documented hand-off protocol; joining it costs two definitions + show/hide calls. Labels-on and the anchor mirror `ActionMenuEdgeButtons` verbatim, so the bar reads the same whichever context owns it | A dedicated keyboard-side window (new chrome, new dwell plumbing, more surface to maintain) |
| 13 | Close semantics: `KeyboardHideRequestedMessage` → `SetKeyboardVisible(false, manageSuppression: true)` | Releasing suppression (re-enabling fixation and the ring) is the point of an explicit dismiss; symmetric twin of Feature A's show message. The sender-less `HideKeyboardRequestMessage` is reserved for the text-field seam (`manageSuppression: false`) and must not be reused | Reusing `KeyboardToggledMessage` (toggle semantics — an accidental hide from a dedicated Close is meaningless); the sender-less hide message |
| 14 | Flip is reachable **only while the keyboard is visible** (the bar hides with the keyboard) | The flip matters exactly when the keyboard occludes something; the Settings row remains the initial-edge chooser; pre-positioning was a nicety nobody asked for | Keeping a second flip surface for the hidden state |
| 15 | Flip always sits **nearer the keyboard**, and the whole **bar tracks the keyboard's edge** (owner pick, mechanism B): keyboard Bottom → bar below screen center on the right edge, Flip in the lowest slot; keyboard Top → bar above center, Flip in the top slot; Close always screen-center-ward. `KeyboardEdgeButtons.Definitions` is keyboard-edge-dependent (slot order + anchor), and `HandleKeyboardEdgeToggled` rebuilds the bar at the new anchor on every flip. Vacated positions carry no dwell target, so a gaze resting where Flip just was hits nothing — no dwell-transfer hazard. The anchor follows the keyboard's **edge setting**, not the B3 yield offset (a screen-edge element, like the action-view bar). Exact anchor offset toward the edge is plan-level (mockup ≈ 75%/25% of work-area height) | (A) swap slots at the fixed action-view anchor — the old Flip position becomes Close, so a gaze resting there through the cooldown can trigger Close; fixed Close-above/Flip-below regardless of keyboard position (owner rejected: "flip should always be closer to the keyboard. Thus, it moves with keyboard position") |

## 6. Testing

- **Core (`EyeCtrl.Core.Tests`)**
  - `ActionMenuSettings.EnsureKeyboardActionOnce`: adds `Keyboard` when absent; skips when
    already present (no duplicate); never re-runs after the flag sets; default
    `VisibleActions` contains `Keyboard`; default `PrimaryActions` does not.
  - `Validate()` unchanged behavior (4-cap still enforced).
- **ViewModels (`EyeCtrl.ViewModels.Tests`)**
  - `ActionMenuViewModelSymbolTests.EveryAction_MapsToADistinctIcon` (tests/…/
    ActionMenuViewModelSymbolTests.cs:51) — NOT automatic: it carries a hand-maintained
    `expected` map that must gain `[InteractionAction.Keyboard] = ActionIcon.Keyboard`
    (the edit is the fix; forgetting it fails `IsEquivalentTo`).
  - `FriendlyName(Keyboard)` = "Keyboard".
- **App (`EyeCtrl.App.Tests`)**
  - `FlipScreenEdge` truth table: `"Top"` → `"Bottom"`, `"Bottom"` → `"Top"`, and
    non-canonical inputs (`"top"`, garbage) read as Bottom — **output is always exactly
    `"Top"`/`"Bottom"`** (case-sensitive validation; an unguarded exit-time save crashes
    otherwise — B1 2a).
  - `HandleKeyboardEdgeToggled` thin-apply seam: assert settings mutation + the explicit
    `SetScreenEdge` call + save + snapshot refresh via the same headless style as
    `ApplyEmojiSkinTone` tests.
  - `KeyboardCoordinator.ShouldShow(bool alreadyVisible)` — the idempotency guard
    extracted as a pure seam (the `ApplyEmojiSkinTone` precedent; the handler itself
    lives in `OnInitialize` and touches WPF, so it is not headless-reachable).
  - `ApplyModalKeyboardState` rewrite: no longer touches the toolbar auto-hide
    suspension, still suspends the quick-actions summon, still publishes
    `KeyboardVisible`.
  - Yield math as a pure function: given `(keyboardEdge, occupied, toolbarEdge, extent)`
    → expected `Top` offset (0 when edges differ or not occupied; clamped).
  - Adapter edge-detect: `VisibilityChanged` fires once per real transition, not on
    re-asserts; fired on the UI thread.
  - Summoned-while-keyboard-up dwellability: a hidden-then-shown toolbar accumulates
    dwell on its buttons while `KeyboardVisible` is true (the messenger +
    `SetActive(true)` chain — B2 #3).
  - `KeyboardEdgeButtons.Definitions` (headless, mirroring the
    `EdgeButtonBarViewModelTests` style with `SimulateTargetActivated`): ids, labels
    always on, `ActionIcon.Close` reused / `ActionIcon.KeyboardEdge` new; Close
    activation sends `KeyboardHideRequestedMessage`, Flip sends
    `KeyboardEdgeToggledMessage`; dwell time wired from `KeyboardKeyDwellMs`.
    **Edge-dependence (ledger #15):** `keyboardAtTop: true` puts Flip in the top slot
    and the anchor above screen center; `false` puts Flip lowest and the anchor below
    center; Close is always screen-center-ward. The anchor math pins against
    `ScreenGeometry`'s pure overload the way `RightEdgeAnchor` tests do.
  - Guarded hide: keyboard-hide path calls `Hide()` only when the bar still shows the
    keyboard definitions (another context's stop/cancel buttons survive).
  - `ActionMenuPageSource` description completeness gate (existing) fails until the
    description string lands.
- **Breakage inventory (concrete, verified):**
  - `tests/EyeCtrl.App.Tests/Services/Coordinators/KeyboardCoordinatorTests.cs:70` —
    `ApplyModalKeyboardState_FansOutToBothControllers_AndTheSharedFlag` asserts
    `toolbar.IsAutoHideSuspended` true/false around the call (the grep token is
    `IsAutoHideSuspended`, **not** `SetSuspended` — that hits only
    `ToolbarLifecycleCoordinatorTests.SetAutoHideSuspended_ForwardsToTheController`,
    which survives because it drives the Settings-path wrapper directly). Rewrite the
    test to the quick-actions + flag fan-out; its name and 6-line rationale comment go
    stale with it.
  - `tests/EyeCtrl.App.Tests/Services/Coordinators/QuickActionsCoordinatorTests.cs:53` —
    comment cites the fan-out test's full name; update it with the rewrite.
  - `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs:51` — the expected
    map edit above.
  - **Deliberately unaffected** (no toolbar button in this design):
    `ToolbarViewModelTests.InitializesWithTheEightCoreButtons`, the band-layout
    assertions in `ToolbarButtonVisualTests`, `AppLaunchTests` button set,
    `ToolbarButtonDriftTests`, and the three `ToolbarVisibilityAdapterTests` constructor
    sites (the adapter keeps its 3-arg ctor — B3 #5).
- **Manual / UI-gated (owner-run; a real tracker is required)**: ring → Keyboard opens
  the keyboard with suppression; frame Flip moves the visible keyboard; frame Hide
  dismisses it and the ring works again immediately; keyboard up → toolbar auto-hides
  and edge-summons; summoning the toolbar onto the keyboard's edge slides the keyboard
  aside and back; the toolbar Keyboard button still hides the keyboard via summon.
  Note: under the Mouse provider the auto-hide subscription does not exist (C3), so the
  summon/yield checks are unobservable on a dev box — frame Flip/Hide still are.

## 7. Risks

- **R1 — icon quality.** Two new hand-drawn paths: the ring's keyboard glyph
  (`ActionIcon.Keyboard`, Feature A) and the edge-bar Flip glyph
  (`ActionIcon.KeyboardEdge` — keyboard with up/down arrows); Close reuses the existing
  `ActionIcon.Close`. The plan should render-check both at real size (the repo's
  established visual-review step).
- **R2 — toolbar band: no change in this design.** The band keeps its six buttons. For
  the record, the vertical docks already overflow a 1080p-at-125% work area at six
  buttons (888 DIP band vs ~816 DIP work area, `PlaceAtEdge` centring sends `Top`
  negative and the end buttons off-screen) — pre-existing, out of scope here, and the
  reason a seventh button was rejected (§4.3).
- **R3 — settings window staleness.** If the Settings window is open while the frame
  Flip fires, its Screen Edge row shows a stale value until rebuilt. Same staleness
  class as every other runtime mutation (e.g. skin tone); accepted.
- **R4 — `FlipScreenEdge` canonicalization.** Comparison is case-insensitive
  (mirroring `RepositionOnScreen`) but the written/spoken output is always exactly
  `"Top"`/`"Bottom"` — validation is a case-sensitive membership test, `Save` throws on
  failure, and the unguarded exit-time save makes a stray casing an app crash (B1 2a).
- **R5 — un-pinning is a behavior change for every keyboard user.** The primary dismiss
  is the edge-bar Close (one dwell, always present while the keyboard is up, far from
  the typing field); the edge-summon + toolbar Keyboard button is the secondary exit.
  The pin's original "stranded" rationale is answered by the bar, not the summon (C3,
  ledger #9). Manual testing must confirm both paths on a real tracker.
- **R6 — the keyboard slides under an active dwell.** Yield fires while gaze is at the
  screen edge (safe); the return fires at toolbar idle, when gaze may be on the top key
  row — nearest-key re-acquisition handles it, but a mid-dwell key can shift once.
  Accepted (documented in B3 #8); revisit if struggle-telemetry flags it.
- **R7 — yield seam coupling.** `KeyboardWindow` learns about toolbar extent/edge via the
  coordinator; the seam clamps the extent and ignores unknown edges so a pathological
  toolbar measurement cannot push the keyboard off-screen (B3 #6).
