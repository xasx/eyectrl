# Toolbar gaze accessibility — Task 10 findings

Recorded 2026-08-30, against `feat/toolbar-gaze-accessibility`.

Three of the four checks were settled by rendering the real `ToolbarWindow` content headlessly
(`RenderTargetBitmap` at 2.0×, the user's actual display scale) rather than by eye on the tracker.
That is deliberate: the same method caught the sun-shaped "gear" and the mis-sized UIA anchor
earlier in this plan, and it produces evidence that can be re-examined instead of a recollection.

---

## 0. Blocker found and fixed first: no button was dwellable

Not one of the four checks — it was found *by* attempting them, and it invalidated the first
on-tracker run entirely.

Every button face reported `ActualWidth == 0` when the gaze route asked for its centre, so
`PointToScreen(ActualWidth / 2, ActualHeight / 2)` degenerated to `PointToScreen(0, 0)`. An
unarranged element also carries no visual offset, so that returned the **same** screen point for
all six buttons: all six dwell targets were created stacked on one spot. Recorded from the running
app — six placements, every one `-> screen=(1084,8)`.

Fixed in `1f385f43`. See that commit for the full mechanism and the second (pre-existing) defect
that made it unrecoverable. Re-verified on the tracker afterwards: six distinct centres 288
physical px apart, all six buttons registering dwell progress, five completed activations.

**This is the finding that matters most for the plan's own claim.** Task 10 exists because "every
test above is headless by design". The headless suite was 672 tests green while the toolbar was
completely non-functional, because nothing exercised
`Initialize → layout → UpdateTargetPositions → DwellTarget` end to end.

---

## 1. Are the three selection-mode icons distinguishable at 54 px? — PASS

Rendered all three by driving the real message path (`SelectionModeChangedMessage` through
`ToolbarViewModel`), not by calling `SetSelectionModeState` on the window, which sets only the tint.

| Mode | Icon | Fill |
|---|---|---|
| `None` | slashed ring | untinted surface |
| `ManualSelection` | crosshair reticle | green |
| `AutoSelection` | ring around a rounded control | blue |

The silhouettes are genuinely different and the state is **double-encoded** — icon *and* fill —
so neither channel carries it alone.

**Caveat worth keeping:** all three icons are "a ring with something inside it". They separate at
54 px, but the fill tint is doing real work at a glance, and green-vs-blue is the weaker half of
that pair for some colour vision deficiencies. The redundancy is what makes this safe; if a future
change ever drops the mode tint, the icons alone are a thinner margin than they look.

## 2. Does the 4.8 px arc read as progress at 120 DIP? — PASS

Rendered at 0.15, 0.45 and 0.85. The mapping is linear and correct — `UpdateArc` computes
`angle = progress × 360` from 12 o'clock clockwise, and the renders match: ~54° at 0.15, ending
just short of bottom-centre at 0.45, and a ~54° gap remaining at 0.85.

At 2× the arc is ~9.6 device px of bright cyan, with a darker halo arc beneath giving it an edge
against the disc. It is unambiguous at all three values. **No case for changing
`ArcThicknessRatio`**, which would have been a four-caller conversation rather than a toolbar-local
one.

(One correction against my own first reading: I initially judged the 0.45 sweep as too long. It is
not — the arithmetic above and the 0.85 frame both confirm the mapping. The always-present rim
ring is easy to mistake for arc at a glance.)

## 3. Is `BorderSubtle` enough as a rim over bright content? — PASS

The plan's concern was that both surfaces ship at `0xFA` alpha, so the real ratio drifts over live
desktop content and the boundary might disappear over a bright window.

Computed from the token values, compositing the rim over the composited surface over three grounds:

| Theme | over black | over mid-grey | over white |
|---|---|---|---|
| Midnight | 3.29 | 3.20 | 3.11 |
| Ember | 3.26 | 3.18 | 3.09 |
| Contrast | 21.00 | 21.00 | 21.00 |

The drift is **0.18 across the entire range**, because `0xFA` lets only 2% of the ground through.
It never approaches disappearing, and stays above the 3:1 WCAG 1.4.11 non-text minimum on every
theme and every ground. **`BorderSubtle` holds; no `BorderFocus` at rest.** (`BorderFocus` would be
11.5–12.5:1 — the right magnitude for an acquired/focus state, not for rest.)

## 4. Does `Toolbar_Button_Pause` still resolve in UIA? — UNRESOLVED

Probed the automation peers headlessly. All six anchors exist with the correct
`AutomationId` (`Toolbar_Button_<Id>`) and `Name`, and each measures **120×120** — the explicit
sizing that replaced text-sized anchors holds, so a client aiming at the published rectangle's
centre hits the disc centre rather than a sliver at its top-left.

But every peer reported `IsControlElement=False`, `IsOffscreen=True`, `BoundingRectangle=Empty`.
Those last two are certain artifacts of there being no `PresentationSource` — the exact condition
the anchor exists to work under — so **this probe cannot tell whether the control-view exclusion is
real or an artifact of the same missing source.** It is not evidence either way.

This is the one item the plan is right that only a live run answers: "run the UI suite once, or
confirm via an accessibility inspector". `AppFixture.SimulateDwellOn` consumes these ids, so the
`Category=UI` suite is the direct test — but it launches the real application and displays real
`Topmost` overlays, so it needs explicit permission before running.

**Status: open.** Nothing depends on it for the visual work; it gates the UI test suite's ability to
drive the toolbar, not the toolbar itself.

---

## Follow-ups opened

Everything this plan leaves open, in one place. Several of these were agreed during the work and
existed only in the session transcript until now; they are written out here so acting on them does
not require that conversation.

### 1. UIA control-view membership of the toolbar anchors

The six `Toolbar_Button_<Id>` anchors carry the right ids, names and 120x120 size, but a headless
peer probe reports `IsControlElement=False` / `IsOffscreen=True` / `BoundingRectangle=Empty`. The
last two are certain artifacts of there being no `PresentationSource`, so the probe settles
nothing. Confirm with a `Category=UI` run (`AppFixture.SimulateDwellOn` consumes these ids) or an
accessibility inspector against the running app. **That suite launches the application and shows
real `Topmost` overlays, so it needs explicit permission before running.** If the anchors really
are outside the control view, the fix is an `AutomationProperties.SetAutomationControlType` or a
custom peer, not a bigger anchor. See item 4 of this document.

### 2. End-to-end coverage of the dwell-target placement path

`ToolbarTargetPlacementTests` (added with `1f385f43`) pins the arrangement guard, which is the half
that failed. The other half — `PointToScreen` producing six *distinct* screen centres — still has
no automated cover, because it needs a real window source. The 672-test suite was green while no
button was dwellable; that gap is narrowed, not closed. A UI-category test that shows the toolbar
and asserts distinct target centres would close it.

### 3. Cooling-down state is not rendered

Recorded in the plan's own self-review: `Anatomy.dc.html` specifies a cooling-down state and
`DwellDetector` exposes no in-cooldown signal, so there is nothing for the view to bind. Needs
detector work first. Deferred, not dropped.

### 4. `ToolbarWindow.xaml.cs` is 798 lines

It now owns band construction, layout, icon swapping, face painting, acquired/flash state, mode
tinting, the provider indicator and target placement. Extracting the band (construction + layout +
painting) is the obvious split. Deliberately not done inside this plan — it would have mixed a
large structural move into a change whose value is visual, and the diff was already the largest in
the plan.

### 5. Provider indicator has no `AutomationProperties`

It is chrome rather than a target, which is why it has no dwell anchor — but it also carries no
name, so a screen reader has nothing to announce for it.

### 6. Vertical-dock separator has no test

`Reset_CarriesTheWarningHue_ClearanceAndSeparator` covers the horizontal band only.
`LayOutBand`'s separator arithmetic has a `_vertical` branch (`SetMode`'s axis flip) that no test
exercises. The horizontal case had a real off-by-48px bug caught in plan review, so the untested
branch is not a safe assumption.

### 7. `ToolbarVisibilityAdapterTests` duplicates `WpfApplicationGate`

It carries a private copy of the gate rather than using the shared one, so a future change to the
`Application`-singleton handling has two places to land. Its own comment (line 34) names the
duplication.
