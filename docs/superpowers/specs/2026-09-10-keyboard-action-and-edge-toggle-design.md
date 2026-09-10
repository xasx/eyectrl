# Keyboard action-menu action + quick Top/Bottom switch — design

Date: 2026-09-10
Status: awaiting owner review (round 3 — external spec review processed: BLOCK verdict,
15 findings, all processed; the two blockers resolved by the B4 redesign)
Brainstorm session: owner dismissed both clarifying-question rounds; every open choice is
encoded in the decision ledger (§5) and is vetoable. Point review comments at ledger
numbers.

Owner round-1 feedback (2026-09-10), addressed in this revision: the flip button must not
rely on the toolbar being force-pinned while the keyboard is up — either the button lives
elsewhere, or the toolbar stays auto-hide and the keyboard slides to give a summoned
toolbar room. This revision does BOTH: the flip (and a new Hide) live on the keyboard's
own frame (B4), and the toolbar un-pins and auto-hides while the keyboard is up (B2/B3).

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
  removes the pin anyway — safely — by giving the keyboard its own on-frame Hide (B4).
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
- G2: Flip the keyboard Top ↔ Bottom in one dwell, from the keyboard itself (always
  reachable while the keyboard is visible), plus an on-keyboard Hide so the keyboard is
  never dependent on the toolbar for dismissal.
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

The keyboard gains a slim control column on its own frame (right edge): a **Hide**
button and a **Top/Bottom flip** button, styled like keys. One dwell on Hide dismisses
the keyboard; one dwell on Flip moves it to the opposite edge immediately. The toolbar
gets **no new button** and is **no longer force-pinned** while the keyboard is up: it
hides when gaze leaves its edge and edge-summons on demand, exactly as it behaves without
the keyboard. When the toolbar is summoned onto the keyboard's edge, the keyboard slides
toward screen center to make room and slides back when the toolbar hides. The flip works
in emoji mode too (the column is outside the key grid and category bar).

### 4.2 Changes, in dependency order

**B1 — the flip itself**

1. **`KeyboardEdgeToggledMessage`** (new record, no payload, `InteractionMessages.cs`
   beside `KeyboardToggledMessage`). Its sender is the keyboard's own Flip button (B4).
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

**B4 — the keyboard's own control column (Hide + Flip)**

9. **A slim vertical column docked to the right edge of the keyboard frame**
   (`DockPanel.Dock="Right"` inside the existing `Border`), outside `KeyRows`,
   `PredictionBar`, and `CategoryBar` so it is present in text **and** emoji modes. Two
   dwellable buttons, styled like keys: **Hide** (dismiss the keyboard) and **Flip**
   (send `KeyboardEdgeToggledMessage`). Text labels, not icon-set glyphs — keyboard keys
   are labelled text; no new `ActionIcon`/`ToolbarIconKey` members. The keyboard window
   is `SizeToContent`, so the column narrows the whole keyboard rather than shrinking
   keys. Exact column width to the plan; constraint: no key may fall below its
   pre-column usable width (the window widens to compensate).
10. **Dwell mechanics:** a small dedicated `DwellTarget` set fed from `UpdateGaze` with
    per-sample recomputed centers — the same pattern the emoji category tabs use
    (`EmojiGridManager.UpdateCategoryTargets`). Dwell time: the keyboard key dwell
    (`settings.Dwell.KeyboardKeyDwellMs`); progress arc via the existing
    `DwellOutlineOverlay` key treatment; registered under the keyboard's dwell context
    so struggle telemetry sees it.
11. **Hide semantics:** new `KeyboardHideRequestedMessage` handled by
    `KeyboardCoordinator` → `SetKeyboardVisible(false, manageSuppression: true)` —
    releasing suppression (re-enabling fixation and the ring) is the point; this is the
    symmetric twin of Feature A's `KeyboardShowRequestedMessage`, not a reuse of the
    sender-less `HideKeyboardRequestMessage` (whose handler is deliberately
    `manageSuppression: false` for the text-field seam).

### 4.3 Rejected variants (see ledger #6, #9, #12)

A new **toolbar** flip button (the round-2 design): the band is already at its layout
limit for vertical docks (6 buttons ≈ 888 DIP against a ~816 DIP work area at 1080p /
125% scaling — a 7th button deepens an existing overflow), and the flip-as-escape
analysis in C3 shows the toolbar must not be the keyboard's only lifeline. Toolbar
relocation on collision (round-1 design — the toolbar never moves now);
always-swapping keyboard + toolbar together; static nudge below a pinned toolbar; a
ring action for the flip (position is a global UI concern, not point-targeted); a new
QuickActionType (heavier editor + preset surface); a `hideKeyboard` special-action key
in the layout JSONs (three layouts × every sublayout, and the emoji grid is built in
code, not layout — the frame column is mode-agnostic for free).

## 5. Decision ledger (owner-vetoable)

| # | Decision | Rationale | Rejected |
|---|----------|-----------|----------|
| 1 | Ring action semantics: **show, idempotent** (never hides) | "click and open the keyboard"; an accidental hide from the ring strands the typing flow; hiding already costs one dwell on the toolbar | Toggle semantics; reuse of `KeyboardToggledMessage` |
| 2 | New `KeyboardShowRequestedMessage` handled with `manageSuppression: true` | Must match the toolbar toggle's modal semantics (fixation suppressed, summon suspended) or the next fixation opens a menu over the keyboard | Reusing `ShowKeyboardRequestMessage` — its handler is deliberately `manageSuppression: false` for the text-field seam |
| 3 | Default **on** (layer 2, behind More) + one-shot migration `KeyboardActionMigrated` | The action never existed, so no user could have removed it — the adds-only one-shot pattern (C2) is safe; layer 2 because the primary cap (4) is a validated aimability constraint and is full. The second layer grows 5 → 6 targets (neighbour gap ~160 → ~115 DIP at default CircleSize per ActionMenuSettings.cs:34-37) — inside the documented-safe envelope, but the plan should render-check the ring | Opt-in only (feature ships invisible to existing users); primary-layer default (Validate rejects 5) |
| 4 | Dispatch intercepts in `KeyboardInteractionDispatcher` before the click switch | PinNote precedent exactly; zero Core state-machine changes; the target point is genuinely unused | A Core special-case like ScrollMode (which exists because scroll needs an anchor point) |
| 5 | Collision handled by **keyboard yielding** to a summoned toolbar; the toolbar never relocates | Owner round-1 direction ("the keyboard can slide to give room to the toolbar"); no persisted toolbar change, no DockedEdge writes, summon edge stays where the user learned it | Toolbar relocation to the opposite edge (round-1 design, superseded); static nudge below a pinned toolbar |
| 6 | Quick switch surface: **the keyboard's own frame column** (Hide + Flip, B4) — no new toolbar button | Owner round-1 alternative #1 ("the button must live somewhere else"), and the C3 escape analysis demands the keyboard dismiss itself once the toolbar un-pins; the toolbar band is also at its vertical-dock layout limit (§4.3) | A new toolbar flip button (round-2 design — fails both the band-fit check and the escape-lifetime argument); ring action for the flip (not point-targeted); a new QuickActionType (heavier editor + preset surface); a `hideKeyboard` layout special-action key (three layouts × sublayouts, emoji grid is code-built) |
| 7 | Persistence: mutate the live settings object + **explicit `SetScreenEdge` call** + `SaveDebounced` + snapshot refresh | C5/C6: the settings write alone moves nothing (no `PropertyChanged` observer; `SizeChanged` re-applies only the last stored edge), and the snapshot refresh deliberately closes the `ReloadIfChanged` back door — so the direct call is the only apply (B1 2b) | Settings-window round trip (the thing being removed); relying on the phantom-diff rebuild; direct window calls without persisting (reverts on restart) |
| 8 | Toolbar docks that don't yield: **Left/Right (orthogonal) and undocked/floating** | A side strip crossing a wide keyboard is transient (toolbar visible only while summoned); an undocked toolbar raises no visibility transitions at all (auto-hide disabled, `Feed` early-returns), so it cannot drive the yield — and the frame Flip (B4) is the escape: the user moves the keyboard instead. Floating placement is user-owned | Horizontal yield for side docks; docking a floating toolbar as a side effect |
| 9 | **Un-pin the toolbar while the keyboard is up** (remove the keyboard path's auto-hide suspension only) — safe because B4 gives the keyboard its own on-frame Hide; the summoned toolbar is the redundant second exit | Owner round-1: the pinned strip eats typing space. The pin's "stranded" rationale would otherwise be real: while the keyboard is up the ring is unreachable (C3 — `SuppressFixation` gates the activation button) and the edge-summon rejects invalid samples, so without B4 the summon would be the sole escape | Keep the pin; un-pin without an on-keyboard escape (blocked by review round 3, finding 2) |
| 10 | Keyboard yield: **instant reposition, no animation, occupancy consulted on keyboard show (queried from the window's `IsVisible`)** | Minimum that satisfies "slides to give room"; per-sample target recompute (B3 #8) makes the move safe; animation is polish, not function. The query must read the window, not a tracked adapter flag — the startup show bypasses the adapter entirely (B3 #5) | Animated slide in v1; trusting an adapter-tracked flag for the initial state; yield only after show (would mis-place a keyboard opened onto a summoned toolbar) |
| 11 | Quick-actions hot-edge summon stays **suspended** while the keyboard is up | Un-pinning applies to the toolbar only; the quick-actions grid popping over the keyboard is unwanted and unrelated to the owner's complaint | Also un-suspending the quick-actions summon |
| 12 | Control column: **right-edge vertical strip, Hide above Flip, key-styled text labels, present in text and emoji modes** | One implementation covers both modes (outside `KeyRows`/`CategoryBar`); keys are labelled text, so no new icon-set members; `SizeToContent` narrows the whole window, keys keep their size | A top control row (adds height, shifts every key down); overlay corner buttons (cover keys); icon glyphs (keyboard visual language is text) |
| 13 | Hide semantics: `KeyboardHideRequestedMessage` → `SetKeyboardVisible(false, manageSuppression: true)` | Releasing suppression (re-enabling fixation and the ring) is the point of an explicit dismiss; symmetric twin of Feature A's show message. The sender-less `HideKeyboardRequestMessage` is reserved for the text-field seam (`manageSuppression: false`) and must not be reused | Reusing `KeyboardToggledMessage` (toggle semantics — an accidental hide from a dedicated Hide button is meaningless); the sender-less hide message |
| 14 | Flip is reachable **only while the keyboard is visible** (no pre-positioning while hidden) | The flip matters exactly when the keyboard occludes something; the Settings row remains the initial-edge chooser; pre-positioning was a nicety nobody asked for | Keeping a second flip surface for the hidden state |

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

- **R1 — icon quality.** One new hand-drawn path (the ring's keyboard glyph for
  `ActionIcon.Keyboard`). The frame column's Hide/Flip are text labels, matching the
  keyboard's key language. The plan should render-check the glyph at real size (the
  repo's established visual-review step).
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
  becomes the always-visible frame Hide (one dwell, no round trip); the edge-summon +
  toolbar Keyboard button is the secondary exit. The pin's original "stranded" rationale
  is answered by B4 rather than by the summon alone (C3). Manual testing must confirm
  both paths on a real tracker.
- **R6 — the keyboard slides under an active dwell.** Yield fires while gaze is at the
  screen edge (safe); the return fires at toolbar idle, when gaze may be on the top key
  row — nearest-key re-acquisition handles it, but a mid-dwell key can shift once.
  Accepted (documented in B3 #8); revisit if struggle-telemetry flags it.
- **R7 — yield seam coupling.** `KeyboardWindow` learns about toolbar extent/edge via the
  coordinator; the seam clamps the extent and ignores unknown edges so a pathological
  toolbar measurement cannot push the keyboard off-screen (B3 #6).
