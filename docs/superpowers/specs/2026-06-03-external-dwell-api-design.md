# External Dwell API — Design Spec

> Revised 2026-06-04. Supersedes the original SDK-owned draft. Core architectural
> change: EyeCtrl owns the single dwell engine; the SDK is a thin, framework-specific
> client. Supports both **WinUI 3** and **WPF** consumer apps.

## Summary

Enable an external Windows application that **you build** to have its UI controls
activated by gaze dwell, with minimal integration effort. A control opts in with one
XAML attached property; EyeCtrl does all dwell detection on the gaze it already tracks,
renders the dwell-progress feedback it already renders, and calls back to the app to
invoke the control.

There is exactly **one dwell engine**, and it lives in EyeCtrl. The app-side SDK is a
thin client that (a) reports each opted-in control's screen rectangle and window handle,
and (b) invokes the control when EyeCtrl says it was activated. The SDK ships as **two
NuGet packages** — one for WinUI 3 consumers, one for WPF consumers — over a shared,
framework-agnostic core.

## Why EyeCtrl-owned (not SDK-owned)

The original draft had each app embed a copy of `DwellDetector`, poll the cursor, and
fire the click in-process ("zero IPC for activation"). That design has a fatal coupling
problem: EyeCtrl already runs its own fixation → activation-button → action-menu engine
on the same cursor (`GazeEngine.OnGazePoint`, `src/EyeCtrl.Core/GazeEngine.cs`). To stop
EyeCtrl from popping its own menu over a cooperating control, the SDK must signal EyeCtrl
to suppress itself — so IPC is required anyway. Once IPC exists, the in-process advantage
evaporates and the SDK-owned design is left with a duplicated dwell engine, a second
cursor-polling timer sampling an already-filtered cursor, and duplicated dwell-time and
feedback config.

EyeCtrl-owned eliminates all of that:

| Concern | SDK-owned (old) | EyeCtrl-owned (this spec) |
|---|---|---|
| Dwell engine | Duplicated in every app | Single, in EyeCtrl |
| Coordination with EyeCtrl's own menu | Must add suppress signal | Inherent — one engine |
| IPC | Suppress/resume (needed anyway) | Register + activate (bidirectional) |
| Visual feedback | App re-implements | Reuses EyeCtrl's existing overlay |
| Cursor polling | `GetCursorPos()` on a timer | None — hit-tests EyeCtrl's filtered gaze |
| App integration code | Heavy (ships Core dwell types) | Trivial (one attached property) |
| Activation latency | In-process | One pipe hop (negligible for ~800 ms dwell) |

The only thing given up — in-process activation latency — is irrelevant for an action
that by definition takes hundreds of milliseconds of dwell to trigger.

## Architecture

```
┌─ External app (WinUI 3 or WPF) ─┐          ┌─ EyeCtrl.App (WPF, unchanged) ──┐
│ EyeCtrl.Sdk.* (NuGet)           │          │ GazeEngine → filtered gaze      │
│  XAML: eyectrl:Dwell.Enabled    │          │   │                             │
│  • on load: element → screen    │ register │   ▼                             │
│    rect (physical px) + HWND ───┼─────────►│ ExternalDwellService            │
│  • push rect on layout change   │  update  │   • rect DwellTargets           │
│  • map controlId ⇆ element      │          │   • topmost-HWND gating         │
│                                 │          │   • suppresses own fixation     │
│  • on activate: UIA Invoke ◄────┼──────────│   • draws progress overlay      │
│    or Focus                     │ activated│   • applies dwell cooldown      │
└─────────────────────────────────┘          └─────────────────────────────────┘
   bidirectional named pipe "eyectrl_dwell": register / update / unregister  +  activated
```

The pipe contract is framework-agnostic: a WinUI 3 consumer and a WPF consumer look
identical to EyeCtrl. EyeCtrl never reads the OS cursor; external targets are hit-tested
against the already-filtered gaze point that `GazeEngine` produces
(`CursorMoved`, `src/EyeCtrl.Core/GazeEngine.cs`).

## SDK (app side)

### Project layout — three projects, two packages

```
EyeCtrl.Contracts          ── wire DTOs (primitive fields only); referenced by App + SDK
        ▲                         ▲
        │                         │
EyeCtrl.Sdk.Abstractions ─────────┘   ── pipe client, reconnect, rect-delta coalescing,
        ▲          ▲                      activation routing   (NO UI-framework refs)
        │          │
  ┌─────┘          └─────┐
EyeCtrl.Sdk.Wpf     EyeCtrl.Sdk.WinUI    ── attached properties + per-element adapter
 (UseWPF)            (UseWinUI, WinAppSDK)
```

- `EyeCtrl.Contracts` and `EyeCtrl.Sdk.Abstractions` are **not** published standalone;
  they are packed into each head package (or referenced as a private dependency). The two
  *consumer-facing* packages are `EyeCtrl.Sdk.Wpf` and `EyeCtrl.Sdk.WinUI`. WPF and WinUI
  cannot share one assembly cleanly, hence two packages.

### Shared core (`EyeCtrl.Sdk.Abstractions`)

Owns everything framework-independent: the named-pipe client, connect/reconnect with
backoff, message (de)serialization, dedupe so only **changed** rects go on the wire, and
routing an incoming `activated{id}` back to the correct element. The single seam each head
implements:

```csharp
public interface IDwellElementAdapter
{
    string Id { get; }
    bool TryGetScreenRect(out SdkRect rect);  // physical screen pixels
    long WindowHandle { get; }                // HWND, for topmost-window gating
    void Activate(DwellMode mode);            // marshal to UI thread, then invoke
}

public enum DwellMode { Invoke, Focus }
```

`SdkRect` is a lightweight struct (x, y, w, h doubles) defined in `EyeCtrl.Contracts` and
used both by the adapter and by the wire DTOs. It is intentionally **separate** from
`EyeCtrl.Core.Models.PhysicalRect` so the SDK never references Core. EyeCtrl maps the
incoming primitive fields to Core's `PhysicalRect` / rectangular `DwellTarget` on its side.

The head registers/unregisters an adapter as controls load/unload and notifies the client
when a control's layout may have changed; the client recomputes via `TryGetScreenRect`,
diffs, and sends `update` only on real change.

### Framework heads

Identical XAML surface for both:

```xml
<!-- WinUI 3:  xmlns:eyectrl="using:EyeCtrl.Sdk.WinUI" -->
<!-- WPF:      xmlns:eyectrl="clr-namespace:EyeCtrl.Sdk.Wpf;assembly=EyeCtrl.Sdk.Wpf" -->
<Button Content="Save"
        eyectrl:Dwell.Enabled="True"
        eyectrl:Dwell.Mode="Invoke" />
```

Attached properties (this phase):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Opt this element into gaze-dwell activation |
| `Mode` | `DwellMode` | `Invoke` | `Invoke` fires the control's invoke pattern (Button Click + bound Command); `Focus` only sets focus |

Dwell **time** is global this phase (`ExternalDwellSettings.DefaultDwellMs` in EyeCtrl) —
there is no per-control `Time` property and no `dwellMs` field on the wire. (Deferred; see
Non-Goals.)

Per-head implementation differences:

| Concern | `EyeCtrl.Sdk.Wpf` | `EyeCtrl.Sdk.WinUI` |
|---|---|---|
| Element → physical rect | `element.PointToScreen(0,0)` and `PointToScreen(ActualWidth, ActualHeight)` (already physical device px; per-monitor DPI handled by WPF) | `element.TransformToVisual(XamlRoot.Content).TransformPoint(...)` (DIPs) × `XamlRoot.RasterizationScale`, offset by `ClientToScreen(hwnd, {0,0})` for the client origin |
| HWND | `new WindowInteropHelper(Window.GetWindow(element)).Handle` | Explicit `EyeCtrlDwell.Initialize(window)` if called; **else implicit** fallback via `XamlRoot.ContentIslandEnvironment.AppWindowId` → `Win32Interop.GetWindowFromWindowId` |
| UI-thread marshal | `element.Dispatcher.BeginInvoke(...)` | `element.DispatcherQueue.TryEnqueue(...)` |
| Activate (`Invoke`) | `UIElementAutomationPeer.CreatePeerForElement(e)` → `IInvokeProvider.Invoke()` | `FrameworkElementAutomationPeer.CreatePeerForElement(e)` → `IInvokeProvider.Invoke()` |
| Activate (`Focus`) | `element.Focus()` | `element.Focus(FocusState.Programmatic)` |
| Rect-change events | `LayoutUpdated` + window `LocationChanged` / `SizeChanged` | `LayoutUpdated` + `AppWindow.Changed` (move/resize) + `XamlRoot.Changed` (scale) |
| TFM / props | `net8.0-windows`, `<UseWPF>true</UseWPF>` | `net8.0-windows10.0.19041.0`, `<UseWinUI>true</UseWinUI>`, `Microsoft.WindowsAppSDK` |

`IInvokeProvider.Invoke()` is the correct activation primitive for both frameworks: on a
`Button` it raises Click **and** runs the bound `Command`, and it generalizes to other
invokable controls without reflection. (The original draft's "raise the Click event"
directly is not possible — `OnClick` is protected.)

### Target frameworks

The SDK deliberately floors **below** the first consumer (a net10 / Windows 11 app),
because nothing it does needs net10 or Win11, and a lower floor maximizes who can consume
it:

| Project | TFM | Notes |
|---|---|---|
| `EyeCtrl.Contracts` | `net8.0` | DTOs + `SdkRect`; consumable by net8 and net10 apps |
| `EyeCtrl.Sdk.Abstractions` | `net8.0` | Pipe client + coordination; no UI-framework or OS-version dependency |
| `EyeCtrl.Sdk.Wpf` | `net8.0-windows`, `UseWPF` | — |
| `EyeCtrl.Sdk.WinUI` | `net8.0-windows10.0.19041.0`, `UseWinUI` | Win10 2004 floor; **Win11 not required**. References **Windows App SDK 1.4+** — the minimum for the implicit `XamlRoot.ContentIslandEnvironment` HWND fallback. Explicit `Initialize(window)` has no such requirement. |

All four are referenceable from the net10 / Win11 consumer without friction.

### Failure behavior

If EyeCtrl is not running, the pipe client connect fails silently and retries with
backoff; the app functions normally with no gaze dwell. No exceptions surface to the host
app.

## EyeCtrl side (`ExternalDwellService`)

New service in `src/EyeCtrl.App/Services/`, registered in `AppBootstrapper`'s DI.

- **Pipe server** `eyectrl_dwell` (byte-mode, newline-framed JSON, bidirectional). Accepts
  multiple connections; one connection = one app instance. Target `id`s are namespaced per
  connection. On disconnect (app closes/crashes), all of that connection's targets are
  removed. Each connection owns a single synchronized writer.
- **`ExternalDwellArbiter`** (pure, UI-free) — the dwell decision engine for external
  targets: topmost-window gating, nearest-target selection, dwell accumulation, and
  cooldown (`ExternalDwellSettings.CooldownMs`). External targets are **rectangular**
  `DwellTarget`s (see Core Change). The arbiter **reuses `DwellTarget` and EyeCtrl's
  dwell-timing convention** (`EnteredTimestamp` + `DwellTimeMicroseconds`) for a consistent
  feel, but does **not** use or modify `DwellDetector` (see Core Change for why). It is fed
  the filtered gaze from `GazeEngine.CursorMoved` and is fully unit-testable via an injected
  `rootWindowAtPoint` seam.
- **Topmost-window gating.** Each target carries its owning HWND. Before accumulating
  dwell / activating, the arbiter checks the **root** window under the gaze point —
  `GetAncestor(WindowFromPoint(gaze), GA_ROOT)` — equals that HWND; otherwise the target is
  ignored. The `GA_ROOT` step is essential for WinUI 3, where `WindowFromPoint` returns the
  child **content-island** HWND, not the top-level window the SDK reports. This also rejects
  controls covered by another window, scrolled off, or minimized, and disambiguates
  side-by-side apps. Note: EyeCtrl's own overlays are click-through (`WS_EX_TRANSPARENT`),
  which `WindowFromPoint` skips, so the feedback overlay never defeats the check.
- **Threading.** `GazeEngine.CursorMoved` fires on the **background gaze thread**
  (`GazeEngineAdapter` marshals via `Dispatcher.BeginInvoke`). `ExternalDwellService`
  therefore injects a `Dispatcher` and marshals all WPF-window (feedback) calls; the atomic
  `SuppressFixation` bool and pipe writes happen inline. Arbiter/target state touched by both
  the gaze thread and pipe-handler threads is lock-guarded.
- **Interaction integration.** While gaze is over a (gated) external target, EyeCtrl sets
  `GazeEngine.SuppressFixation = true` (suppresses its own fixation → activation-button →
  action-menu flow, `src/EyeCtrl.Core/GazeEngine.cs`) and hides the cursor ring. On leave,
  both are restored. Cursor movement (FreeLook `MoveCursor`) is **not** suppressed — the
  user can still move between controls.
- **Feedback.** EyeCtrl draws a dwell-progress indicator as a click-through overlay over
  the target's screen rect, reusing the existing overlay infrastructure
  (`OverlayManager` / overlay windows).
- **Activation.** On dwell completion, EyeCtrl sends `activated{id}` to the owning
  connection and applies the external-dwell cooldown.

### Settings

New `ExternalDwellSettings` following the project convention (`ObservableObject +
IValidatable`, `partial void OnXChanging` guards + `Validate()`):

| Setting | Purpose |
|---|---|
| `Enabled` | Master switch for the external dwell feature |
| `DefaultDwellMs` | Dwell time for all external targets (global this phase) |
| `CooldownMs` | Re-trigger cooldown for the external arbiter |

Must be wired into `EyeCtrlSettings.Validate()` **and**
`SettingsViewModel.ResetAllSettings()` (both enumerate every sub-setting).

## IPC protocol

Bidirectional byte-mode named pipe `eyectrl_dwell`, newline-framed JSON (one message per
line). Rects are primitive fields (`SdkRect`) in **physical screen pixels**; HWND is an
int64. DTOs live in `EyeCtrl.Contracts`, referenced by both sides. EyeCtrl maps `SdkRect`
to Core's `PhysicalRect`; the SDK never references Core.

Client → server:

```
register   { "id": "save_btn", "x": 1200, "y": 340, "w": 96, "h": 32, "hwnd": 132456 }
update     { "id": "save_btn", "x": 1205, "y": 340, "w": 96, "h": 32 }
unregister { "id": "save_btn" }
```

Server → client:

```
activated  { "id": "save_btn" }
```

## Core change (the only one)

Extend `DwellTarget` (`src/EyeCtrl.Core/Processing/DwellTarget.cs`) to support a
**rectangular** shape in addition to its current circle:

- Add a **rectangular constructor** + a settable optional `Bounds` (so a moved control's
  rect can update without resetting dwell); the existing circle constructor is left
  unchanged so current callers are untouched.
- `HitTest` gains a rectangle branch (used by `ExternalDwellArbiter`). "Nearest target"
  selection continues to use center-distance.

**Risk: LOW** (impact analysis, upstream): `DwellTarget.HitTest` has exactly one direct
caller (`DwellDetector.Update`); the constructor's only non-test callers are
`KeyboardWindow.UpdateGaze` and `EmojiGridManager.UpdateCategoryTargets`. Adding rather
than changing leaves all of them untouched. The change is contained to the `Processing`
area. (Per project convention, re-run `gitnexus_impact` before editing during
implementation.)

**`DwellDetector` is deliberately NOT modified.** Impact analysis shows its `Update` is
**HIGH** change-blast-radius (20 direct callers — every overlay/ViewModel gaze receiver).
Topmost-window gating doesn't fit its model, and adding a gate hook would touch that hot,
widely-depended-on method. Instead, `ExternalDwellArbiter` owns the gated accumulation in
isolation while reusing the `DwellTarget` primitive and the same timing convention — so
the "feel" stays consistent and the engine all of EyeCtrl depends on stays untouched.
No thread-safety change to `DwellDetector` is needed; the SDK never touches it.

## Files Created / Changed

| File | New/Change | Purpose |
|------|------------|---------|
| `src/EyeCtrl.Contracts/*` | New | Wire DTOs (register/update/unregister/activated) + `SdkRect`; shared by App + SDK |
| `src/EyeCtrl.Sdk.Abstractions/IDwellElementAdapter.cs` | New | Per-element seam (rect, HWND, `Activate()`) + `DwellMode` |
| `src/EyeCtrl.Sdk.Abstractions/OutboundRectTracker.cs` | New | Pure dedupe: Register vs Update vs skip; reset on reconnect |
| `src/EyeCtrl.Sdk.Abstractions/DwellClient.cs` | New | Byte-mode pipe client, reconnect/backoff, activation routing |
| `src/EyeCtrl.Sdk.Wpf/Dwell.cs` (+ `WpfElementAdapter`) | New | WPF attached properties + adapter → NuGet package |
| `src/EyeCtrl.Sdk.WinUI/Dwell.cs` (+ `WinUiElementAdapter`, `WinUiCoords`, `EyeCtrlDwell.Initialize`) | New | WinUI 3 attached properties + adapter → NuGet package |
| `src/EyeCtrl.Core/Processing/DwellTarget.cs` | Change | Rectangular ctor + settable `Bounds` + rect `HitTest` (additive; `DwellDetector` untouched) |
| `src/EyeCtrl.Core/Settings/ExternalDwellSettings.cs` | New | Settings (wire into `EyeCtrlSettings.Validate()` + `SettingsViewModel.ResetAllSettings()`) |
| `src/EyeCtrl.App/Services/ExternalDwellArbiter.cs` | New | Pure gated-dwell engine (gating, accumulation, cooldown) reusing `DwellTarget` |
| `src/EyeCtrl.App/Services/ExternalDwellService.cs` | New | Byte-mode pipe server, dispatcher-marshaled feedback overlay, suppression, activation |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Change | Register `ExternalDwellService` in DI (with `dispatcher`) |
| `src/EyeCtrl.App/App.xaml.cs` | Change | Resolve + `Start()` on the UI thread; dispose on exit |
| `tests/EyeCtrl.Core.Tests/Processing/DwellTargetRectTests.cs` | New | Rect hit-test (inside/edge/outside), settable `Bounds`, circle unchanged |
| `tests/EyeCtrl.App.Tests/Services/ExternalDwellArbiterTests.cs` | New | Gating, accumulation, cooldown, feedback selection, suppression |
| `tests/EyeCtrl.App.Tests/Services/ExternalDwellServiceTests.cs` | New | Pipe round-trip + per-connection cleanup |
| `tests/EyeCtrl.Sdk.Abstractions.Tests/*` | New | `OutboundRectTracker` dedupe state machine |
| `tests/EyeCtrl.Sdk.WinUI.Tests/*` | New | `WinUiCoords` pure coordinate composition |
| `samples/` | New | A WinUI 3 sample and a WPF sample, each with `eyectrl:Dwell.Enabled="True"` |

## Testing Strategy

- **`DwellTarget`**: rectangular hit-test (inside, on edge, outside); settable `Bounds`;
  existing circle behavior unchanged.
- **`ExternalDwellArbiter`** (pure): topmost-window gating (incl. WinUI island root-window
  case), dwell accumulation, cooldown, feedback rect + suppression — fed timestamps and a
  mock `rootWindowAtPoint`. This is where the behavioral coverage lives.
- **`ExternalDwellService`**: real-pipe round-trip (register) and per-connection cleanup on
  disconnect.
- **`OutboundRectTracker`** (pure): first-seen → Register, unchanged → skip, changed →
  Update, reset → Register again.
- **`WinUiCoords`** (pure): DIP×scale + client-origin composition.
- **Manual smoke**: WinUI 3 sample and WPF sample, each dwell-activating a `Button` with
  EyeCtrl running (incl. non-100% DPI and window-move cases).

## Delivery — two implementation plans

The work splits into two plans so the first delivers a complete, testable vertical slice
before the second framework head is added.

**Plan 1 — EyeCtrl side + WPF head (the full vertical slice).**
`EyeCtrl.Contracts`, `EyeCtrl.Sdk.Abstractions` (pipe client, coalescing, routing),
`EyeCtrl.Sdk.Wpf` head + adapter, the rectangular `DwellTarget` Core change,
`ExternalDwellSettings`, `ExternalDwellArbiter` (gated dwell) + `ExternalDwellService`
(pipe server, suppression, feedback overlay), DI wiring, a WPF sample, and all of the
above's tests. Outcome: a
working WPF-app → EyeCtrl dwell loop, end to end. WPF first because EyeCtrl is itself WPF
and the slice is easiest to exercise in-process.

**Plan 2 — WinUI 3 head.** `EyeCtrl.Sdk.WinUI` head + adapter (TransformToVisual ×
RasterizationScale + ClientToScreen, `Initialize(window)` with implicit
`ContentIslandEnvironment` fallback, `DispatcherQueue` marshaling,
`FrameworkElementAutomationPeer` invoke), a WinUI 3 sample, and head tests. Reuses
`EyeCtrl.Contracts`, `EyeCtrl.Sdk.Abstractions`, and the entire EyeCtrl side from Plan 1
unchanged — the pipe contract is framework-agnostic.

## Non-Goals (this phase)

- JSON profiles for **unmodified** third-party apps (and the visual zone editor). Deferred;
  the EyeCtrl-owned architecture already accommodates them later — a profile loader would
  feed the same rect `DwellTarget`s into the same detector.
- Per-control dwell-time override (`Dwell.Time`) — global `DefaultDwellMs` only for now.
- Gaze-data streaming to the SDK.
- WinForms consumer support.
- UI Automation auto-discovery of controls.
- Cross-platform (Windows-only).
