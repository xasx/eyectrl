# Startup Splash Animation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the splash as an animated brand moment — amber gaze dots arrive, the gaze dot blooms, a cyan dwell arc sweeps 180° while filling into its 120° brand position, title rises, card fades — on its own STA thread so every beat plays during the blocked main-thread startup, with T3 adaptive timing (calm 1.0 s catch-up floor).

**Architecture:** The splash runs on a dedicated STA thread with its own dispatcher (the main thread is blocked for the splash's whole visible life — spec §8). `SplashWindow` owns the visuals and WPF glue; `SplashExitController` holds the pure beat/catch-up/dead-man decisions (headless-testable); `SplashThreadLauncher`/`SplashThreadHandle` own the thread, the marshalled `ISplashWindow` proxy surface, and the bounded DPI report. The base fill runs as a storyboard clock with `BeginTime = 0.55 s`; the catch-up re-targets from captured current values (`From`/`To`, cubic ease-out) — never restarting the arc. Readiness arrives via `BeginExit()` marshalled from `ShowOverlays`.

**Tech Stack:** WPF (.NET 10, `net10.0-windows`), xunit v3 (`xunit.v3`) + NFluent 3.1.0, Serilog static `Log`. Spike is a standalone console tool (not in the solution), mirroring the `tools/EyeCtrl.IconBuilder` pattern.

**Spec:** `docs/superpowers/specs/2026-11-13-splash-animation-design.md` (rev 3.2 — the r4 fixes touched three spec lines: §6 accepted-from states + the `Filled` wait, §7 the two bounded waits, §9 the factory wording). Ledger L1–L16 there is binding; where this plan repeats a number, the spec wins.

## Global Constraints

- **Work in the existing worktree** `.worktrees/splash-animation` on branch `feat/splash-animation`. Do not commit anything outside it.
- **Never paint a visible test window** (AGENTS.md rule): any test touching a showing API uses `InvisibleTestWindow.Prepare(window)` first; production `Show` calls happen only on the real splash thread. The `ISplashWindow` factory seam (Task 6) is what keeps the real thread mockable; the plan's thread tests use the real window only prepared-invisible.
- **Source pins must stay green** (spec §9): the App source keeps the literal `ShowOverlays(splash, cursorCircle, toolbar, settings);` call, the local keeps the name `splash`, `.ShowNoTrackerSentence(` appears exactly once, `SplashWindow.ShowsNoTrackerSentence(...)` keeps its call shape in both pinned sites, and the predicate stays a static on `SplashWindow`.
- **No App-level `StaticResource` / `OverlayStyles.xaml` lookups in splash XAML** — the splash thread has no Application resources. Theme values come from `Tokens` statics (`x:Static` for structs: fonts, corner radius) and frozen brushes assigned in code-behind. `Tokens.Current` is activated *before* the splash thread starts, so parse-time reads see the right theme.
- **Pinned icon constants** (theme-independent, XAML literals): amber `#E8A33C`, bright amber `#F6C77E`, cyan `#A5F3FC`, tile `#141828`. Gradient alphas live in the Colour's own bytes (`#73E8A33C` = 45 %, `#38E8A33C` = 22 %, `#00…` = 0) — `GradientStop` has no `Opacity` property.
- **`KeySpline` easing goes through `SplineDoubleKeyFrame`** (WPF type, verified); code-side catch-up animations use `CubicEase` ease-out. Every `Storyboard.SetTargetProperty` call passes a `new PropertyPath(...)`, never a raw string.
- Card geometry (spec §4): fixed 440 × 336 DIP; padding 36/28 (via `SplashLayout.CardPadding`); mark 150; gap 12 above the title; gap 8 *below* the title (title `Margin="0,12,0,8"` — the gap must NOT sit inside the 48 DIP sentence row); sentence row 48 (two lines, `TextWrapping=Wrap`, always reserved). The XAML references `SplashLayout` via `x:Static` — no re-typed literals.
- Choreography (spec §5): trail 0–0.55 s (staggered 0/0.05/0.10 s, peaks .40/.55/.75) → dot pop 0.30–0.54 s (scale 0→1.15→1.0) → sweep+fill, base course 3.0 s from 0.55 s (rotate −180°→0°, dash 24.36→0, KeySpline `.3,.55,.3,1`) → on base-fill completion the beat is **Filled** (waiting — no title, no exit until readiness) → title 0.25 s → hold 0.20 s → exit fade 0.30 s.
- Timing (spec §6): catch-up duration `= min(remaining base course, 1.0 s)` (L16 — no lower bound, r3 #1), accepted only from Idle/Entrance/Fill/**Filled** — once the tail has begun (CatchUp/Title/Hold/Exit/Closed) the request is ignored; dead-man "not Closed by 15 s"; `BeginExit` arms a hard deadline at readiness + 3.0 s; both timers on the splash thread, self-stopping one-shots.
- DPI: only the **main thread** calls `DpiHelper.Initialize` — with the splash-reported scale or the `GetDpiForSystem` fallback; late reports are discarded (r3 #2). Bounded waits: ≤ 500 ms for the handle, ≤ 500 ms for the DPI report (spec §7, rev 3.2).
- Dispatcher shutdown is owned by the **launcher**, never the window: the window raises `ExitFinished`; in STA tests nobody subscribes, so the test dispatcher survives.
- Test commands: when passing a project path, pass exactly one (multi-project args fail with MSB1008 on this box). A solution-level `dotnet test --filter "Category!=UI"` (no path args) is the AGENTS.md-documented full-suite command and is fine.
- NFluent naming: inclusive comparisons are `IsLessOrEqualThan`/`IsGreaterOrEqualThan`; strict are `IsStrictlyLessThan`/`IsStrictlyGreaterThan`.
- Per repo rules: run GitNexus impact analysis before modifying shared symbols (`ShowOverlays`, `InitializeDpi` in Task 7) and `detect_changes` before committing Task 7. (The r4 review mentioned `wait-idle-tests.ps1` / `--blame-hang-timeout` conventions — neither exists in this repo; not adopted.)
- No linter/formatter configured; follow file-scoped namespaces, Allman braces, `_camelCase` fields.

---

### Task 1: Threading spike — evidence for the separate-thread architecture

**Files:**
- Create: `tools/SplashThreadSpike/SplashThreadSpike.csproj`
- Create: `tools/SplashThreadSpike/Program.cs`
- Create (produced by the run): `tools/SplashThreadSpike/RESULTS.md`

**Interfaces:**
- Consumes: nothing (standalone).
- Produces: recorded evidence (RESULTS.md) that (a) a blocked STA thread freezes its own *running* storyboards, and (b) a second STA thread's dispatcher keeps animating through that blockage, plus the frozen-brush proof. Tasks 3–6 rely on (b); RESULTS.md is committed.

- [ ] **Step 1: Create the spike project**

```xml
<!-- tools/SplashThreadSpike/SplashThreadSpike.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write the spike program**

Both threads run dispatchers. Thread A blocks *inside a posted work item* — a true blockage with an already-running clock (a thread that never pumps proves nothing). Thread B runs `Dispatcher.Run()` and keeps animating through A's blockage.

```csharp
// tools/SplashThreadSpike/Program.cs
// Measures (a) whether a RUNNING storyboard on a BLOCKED STA thread advances, and (b) whether a
// SECOND STA thread's dispatcher keeps animating through that blockage — the evidence behind the
// splash-animation architecture (spec §8). Both windows run OFF-SCREEN (Left = -32000).
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

var resultsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RESULTS.md");

double angleBlocked = 0, angleIndependent = 0;
var blockedReady = new ManualResetEventSlim(false);
var releaseBlocked = new ManualResetEventSlim(false);
var brushProof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

// ── Thread A: window + RUNNING storyboard, then blocks inside its own dispatcher work item ──
var threadA = new Thread(() =>
{
    var dispatcher = Dispatcher.CurrentDispatcher;
    var window = MakeWindow("blocked", out var rotate, out var clock);
    window.Show();
    window.BeginStoryboard(clock);
    CompositionTarget.Rendering += (_, _) => Volatile.Write(ref angleBlocked, rotate.Angle);
    // The blockage: posted onto the dispatcher, so the clock is running when the thread freezes.
    dispatcher.BeginInvoke(new Action(() =>
    {
        blockedReady.Set();
        releaseBlocked.Wait();                       // holds THIS dispatcher thread for ~2 s
    }));
    Dispatcher.Run();
    window.Close();
});
threadA.SetApartmentState(ApartmentState.STA);
threadA.IsBackground = true;
threadA.Start();
blockedReady.Wait(5000);

// ── Thread B: independent dispatcher, keeps pumping (the splash pattern) ──
var threadB = new Thread(() =>
{
    try
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3C));
        brush.Freeze();                              // frozen-brush proof
        brushProof.TrySetResult(brush.IsFrozen);
        var window = MakeWindow("independent", out var rotate, out var clock);
        window.Show();
        window.BeginStoryboard(clock);
        CompositionTarget.Rendering += (_, _) => Volatile.Write(ref angleIndependent, rotate.Angle);
        var pump = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            // pump until the main thread signals us to stop
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until && Volatile.Read(ref angleIndependent) >= 0)
                Dispatcher.DoEvents();
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        }), DispatcherPriority.Background);
        Dispatcher.Run();
        window.Close();
    }
    catch (Exception ex) { brushProof.TrySetException(ex); }
});
threadB.SetApartmentState(ApartmentState.STA);
threadB.IsBackground = true;
threadB.Start();

// warm-up so both clocks are running before the blockage
Thread.Sleep(700);
var a0 = Volatile.Read(ref angleBlocked);
var b0 = Volatile.Read(ref angleIndependent);
releaseBlocked.Set();                                // block thread A inside its work item for ~2 s
Thread.Sleep(2000);
var a1 = Volatile.Read(ref angleBlocked);
var b1 = Volatile.Read(ref angleIndependent);

// shut both dispatchers down
threadA.Join(1000);
brushProof.Task.Wait(1000);

var brushFrozen = brushProof.Task.Result;
var blockedDelta = Math.Abs(a1 - a0);
var independentDelta = Math.Abs(b1 - b0);
var verdict = blockedDelta < 1.0 && independentDelta > 60.0 && brushFrozen;

File.WriteAllText(resultsPath, $"""
# SplashThreadingSpike results ({DateTime.Now:yyyy-MM-dd HH:mm})

| Measurement | Value | Expected | Meaning |
|---|---|---|---|
| Blocked-thread angle delta over 2 s blockage | {blockedDelta:F1}° | ≈ 0 | storyboards on a blocked STA thread freeze (rev-1 §8 claim was wrong) |
| Independent-thread angle delta over the same 2 s | {independentDelta:F1}° | > 60 | a second STA thread + dispatcher keeps animating through the blockage |
| Frozen brush created on thread B | {brushFrozen} | true | Tokens-style frozen brushes cross threads |

**Verdict: {(verdict ? "PASS — the separate-thread architecture is proven" : "FAIL — investigate before building")}**

Sampling note: values are written by each thread's own `CompositionTarget.Rendering` handler into
volatile fields — a blocked thread stops firing the handler, which is exactly the evidence wanted.
Thread A blocks INSIDE a posted dispatcher work item, so its clock is provably running first.
""");
Console.WriteLine(File.ReadAllText(resultsPath));
Environment.Exit(verdict ? 0 : 1);

static Window MakeWindow(string tag, out RotateTransform rotate, out Storyboard clock)
{
    var grid = new Grid { Width = 100, Height = 100 };
    var ellipse = new Ellipse { Width = 40, Height = 40, Fill = Brushes.DodgerBlue };
    rotate = new RotateTransform { CenterX = 50, CenterY = 50 };
    grid.RenderTransform = rotate;
    grid.Children.Add(ellipse);
    var window = new Window
    {
        Content = grid, Width = 120, Height = 120, Left = -32000, Top = -32000,
        ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
        Title = $"spike-{tag}", Topmost = true,
    };
    var anim = new DoubleAnimation(-180, 0, TimeSpan.FromSeconds(3))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
    };
    clock = new Storyboard();
    clock.Children.Add(anim);
    Storyboard.SetTarget(anim, grid);
    Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.Angle"));
    return window;
}
```

(Practical note for the executor: `Dispatcher.DoEvents()` is not a real API — implement the B-thread pump with a `DispatcherFrame` loop, or simply let thread B call `Dispatcher.Run()` and have the MAIN thread post `BeginInvokeShutdown` to B after sampling. Either is fine; the measured quantities are the volatile angles.)

- [ ] **Step 3: Run the spike**

Run: `dotnet run --project tools/SplashThreadSpike`
Expected: exit code 0 and a RESULTS.md whose verdict line reads **PASS** (blocked delta ≈ 0, independent delta > 60°, brush frozen = true). If the blocked delta is *not* ≈ 0, WPF hand-off behaviour exists on this machine — record it; the architecture still stands (thread B proves the design), but note it in RESULTS.md.

- [ ] **Step 4: Commit**

```bash
git add tools/SplashThreadSpike
git commit -m "spike(splash): blocked-thread vs independent-dispatcher animation evidence (RESULTS.md)"
```

---

### Task 2: SplashLayout constants + re-targeted sentence fit gate

**Files:**
- Create: `src/EyeCtrl.App/Overlays/SplashLayout.cs` (**public** — XAML `x:Static` resolves public members)
- Modify: `tests/EyeCtrl.App.Tests/Localization/Fit/GermanGateTests.cs` (the splash test only, ~lines 292-333)
- Test: same file (re-targeted)

**Interfaces:**
- Produces: `SplashLayout` public constants (`CardWidth=440`, `CardHeight=336`, `SidePadding=36`, `InnerWidth=368`, `PaddingTop=28`, `PaddingBottom=28`, `MarkSize=150`, `GapMarkToTitle=12`, `GapTitleToSentence=8`, `SentenceRowHeight=48`) plus `public static Thickness CardPadding => new(36, 28, 36, 28);` — consumed by Task 3's XAML (via `x:Static`) and this gate.
- The old test `TheSplashSentence_FitsTheWorkArea_InEnglishAndGerman` is replaced by `TheSplashSentence_FitsTheReservedRow_InEnglishAndGerman`, which now measures **the real window's content** (no drift between constants and XAML, r4 #6).

- [ ] **Step 1: Create SplashLayout (the gate compiles against it)**

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;

/// <summary>
/// The splash card's fixed geometry (spec §4) — theme-independent layout constants shared by the
/// window XAML (via x:Static) and the fit gates, so the two cannot drift. All values in DIP.
/// Public because XAML's StaticExtension resolves public members only. The card is fixed-size on
/// purpose: the no-tracker sentence's row is always reserved so showing it never reflows.
/// </summary>
public static class SplashLayout
{
    public const double CardWidth = 440;
    public const double CardHeight = 336;
    public const double SidePadding = 36;
    public const double InnerWidth = CardWidth - 2 * SidePadding;   // 368
    public const double PaddingTop = 28;
    public const double PaddingBottom = 28;
    public const double MarkSize = 150;
    public const double GapMarkToTitle = 12;
    public const double GapTitleToSentence = 8;                     // lives BELOW the title, outside the 48 DIP row
    public const double SentenceRowHeight = 48;                     // two lines

    public static Thickness CardPadding => new(SidePadding, PaddingTop, SidePadding, PaddingBottom);
}
```

- [ ] **Step 2: Rewrite the gate to measure the real window (failing — the new XAML does not exist yet)**

In `GermanGateTests.cs`, replace the splash test and its helper with:

```csharp
    /// <summary>
    /// The splash's sentence must fit the card's RESERVED two-line row (spec §4): wrapped height
    /// ≤ the 48 DIP row at the card's inner width, natural width ≤ 2 × inner width (wrapping can
    /// then place it in two rows), and the whole real content height ≤ the 336 DIP card. Measured
    /// on the REAL SplashWindow content so the gate and the XAML cannot drift (r4 #6). English,
    /// the German draft and the shipped German; a sentence repeated ten times must fail (teeth).
    /// </summary>
    [StaFact]
    public void TheSplashSentence_FitsTheReservedRow_InEnglishAndGerman()
    {
        const string key = "overlays.splash.noTracker";
        var english = SplashSentence(StringTable.EmbeddedEnglish.T(key));
        var german = SplashSentence(GermanDraft.Table.T(key));
        var shipped = SplashSentence(ShippedGerman.Load().T(key));
        var overLong = SplashSentence(string.Join(" ", Enumerable.Repeat(ShippedGerman.Load().T(key), 10)));
        _output.WriteLine($"English: {english}");
        _output.WriteLine($"German: {german}");
        Check.WithCustomMessage("The splash-sentence gate passed a tenfold sentence: it cannot fail.").That(overLong.Fits).IsFalse();
        Check.WithCustomMessage(english.ToString()).That(english.Fits).IsTrue();
        Check.WithCustomMessage(german.ToString()).That(german.Fits).IsTrue();
        Check.WithCustomMessage(shipped.ToString()).That(shipped.Fits).IsTrue();
    }

    /// <summary>Wraps the sentence inside the REAL splash window and measures the actual row fit.</summary>
    private static FitVerdict SplashSentence(string sentence)
    {
        EyeCtrl.App.Tests.Fixtures.WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new SplashWindow();
        try
        {
            window.ShowNoTrackerSentence();
            window.NoTrackerSentence.Text = sentence;

            // Wrapped at the card's inner width — the constraint the row actually gives it.
            window.NoTrackerSentence.Measure(new System.Windows.Size(SplashLayout.InnerWidth, double.PositiveInfinity));
            var wrappedHeight = window.NoTrackerSentence.DesiredSize.Height;

            var typeface = new Typeface(Tokens.FontStandard, FontStyles.Normal, Tokens.FontWeightMedium);
            var natural = new FormattedText(sentence, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, Tokens.FontSizeSubtitle, null, 1.0);

            // The whole real content (mark + title + reserved row + paddings) must fit the card.
            var root = (System.Windows.FrameworkElement)window.Content;
            window.Content = null;
            root.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var contentHeight = root.DesiredSize.Height;

            var widthOk = natural.Width <= SplashLayout.InnerWidth * 2;
            var heightOk = wrappedHeight <= SplashLayout.SentenceRowHeight;
            var cardOk = contentHeight <= SplashLayout.CardHeight;
            return new FitVerdict(widthOk && heightOk && cardOk,
                $"natural {FitText.Dip(natural.Width)} (limit 2×{FitText.Dip(SplashLayout.InnerWidth)}), "
                + $"wrapped {FitText.Dip(wrappedHeight)} of {FitText.Dip(SplashLayout.SentenceRowHeight)}, "
                + $"content {FitText.Dip(contentHeight)} of {FitText.Dip(SplashLayout.CardHeight)}",
                FitText.ResolvedFace(window.NoTrackerSentence), SplashLayout.SentenceRowHeight - wrappedHeight);
        }
        finally { window.Close(); }
    }
```

Also add the missing usings at the top of the file if absent: `System.Globalization`, `System.Windows`, `System.Windows.Media`, and `EyeCtrl.Controls.Theme` (for `Tokens`).

- [ ] **Step 3: Run the gate to verify it fails (compile error: new XAML not written yet)**

Run: `dotnet build tests/EyeCtrl.App.Tests`
Expected: **compile error** — `SplashWindow` still has the old layout (the gate itself compiles; if the old window's members are missing, that is this task's expected red).

- [ ] **Step 4: Commit the constants + gate together with Task 3's XAML**

This task's gate stays red until Task 3's window exists; commit at the end of Task 3 (single commit covers constants + gate + window — they are one deliverable). Do NOT commit a red gate alone.

```bash
git add src/EyeCtrl.App/Overlays/SplashLayout.cs
git commit -m "feat(splash): fixed-card layout constants (gate lands with the window in the next commit)"
```

---

### Task 3: Splash window content — mark, title, sentence row, click-through

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/SplashWindow.xaml` (full rewrite)
- Modify: `src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs`
- Modify: `tests/EyeCtrl.App.Tests/Overlays/SplashNoTrackerSentenceTests.cs` (visibility test only)
- Modify: `tests/EyeCtrl.App.Tests/Assets/AppIconTests.cs` (comment only, ~lines 89-91)
- Test: new `tests/EyeCtrl.App.Tests/Overlays/SplashWindowConstructionTests.cs`

**Interfaces:**
- Consumes: `SplashLayout` (Task 2), `Tokens` statics.
- Produces: `SplashWindow` with `internal bool SentenceShown { get; }`, `internal bool DeclaresNoOrderRepair { get; }`, `internal void BeginExit()` (safe stub, replaced in Task 5), `internal void ForceClose()`, `internal void ShowNoTrackerSentence()` (kept — source pin), element names `NoTrackerSentence`, `TitleText`, `MarkHost`, `Tile`, `DwellArc`, `GazeDot`, `Bloom`, `SplashContent`. **No `SplashExitController` here** — the controller arrives in Task 4; Task 3 must build on its own (r4 #1).

- [ ] **Step 1: Write the construction test (failing — new API does not exist)**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/SplashWindowConstructionTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The redesigned splash constructs headless (OverlayClientOriginTests also constructs it — new
/// XAML must parse with no Application resources) and carries the spec §4 contract: fixed card,
/// mark on the icon tile, reserved sentence row, click-through, no band-fallback order repair.
/// The window is never shown.
/// </summary>
[Collection("WpfApplication")]
public class SplashWindowConstructionTests
{
    [StaFact]
    public void TheWindow_CarriesTheSpecContract()
    {
        var splash = InvisibleTestWindow.Prepare(new SplashWindow());
        try
        {
            Check.That(splash.Width).IsEqualTo(SplashLayout.CardWidth);
            Check.That(splash.Height).IsEqualTo(SplashLayout.CardHeight);
            Check.That(splash.IsClickThrough).IsTrue();
            Check.That(splash.DeclaresNoOrderRepair).IsTrue();
            Check.That(splash.SentenceShown).IsFalse();
            Check.That(splash.NoTrackerSentence.Opacity).IsEqualTo(0);
            Check.That(splash.NoTrackerSentence.Margin).IsEqualTo(new Thickness(0));   // the 8 DIP gap lives below the title
            Check.That(System.Windows.Automation.AutomationProperties.GetAutomationId(splash.NoTrackerSentence))
                .IsEqualTo("Splash_NoTracker");
            // the mark's parts exist (names the storyboards target)
            Check.That(splash.FindName("DwellArc")).IsNotNull();
            Check.That(splash.FindName("GazeDot")).IsNotNull();
            Check.That(splash.FindName("Bloom")).IsNotNull();
            Check.That(splash.FindName("MarkHost")).IsNotNull();
            Check.That(splash.FindName("Tile")).IsNotNull();
            Check.That(splash.FindName("SplashContent")).IsNotNull();
        }
        finally { splash.Close(); }
    }

    /// <summary>
    /// Spec §8: the splash thread has NO Application resources — the window must construct on a
    /// bare STA thread. This is the resource-independence proof the r4 review asked the spike to
    /// carry; it belongs here where the real XAML is under test.
    /// </summary>
    [Fact]
    public void TheWindow_ConstructsOnABareStaThread_WithoutApplicationResources()
    {
        Exception? failure = null;
        var constructed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                var splash = new SplashWindow();     // no WpfApplicationGate, no Application
                Check.That(splash.NoTrackerSentence).IsNotNull();
                splash.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { constructed.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Check.That(constructed.Wait(5000)).IsTrue();
        Check.WithCustomMessage($"Splash did not construct without Application resources: {failure}").That(failure).IsNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowConstructionTests"`
Expected: **compile error** — `IsClickThrough` settable via ctor initializer already exists but `SentenceShown`, `DeclaresNoOrderRepair` do not.

- [ ] **Step 3: Rewrite SplashWindow.xaml**

```xml
<overlays:OverlayWindow x:Class="EyeCtrl.App.Overlays.SplashWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:overlays="clr-namespace:EyeCtrl.App.Overlays"
        xmlns:theme="clr-namespace:EyeCtrl.Controls.Theme;assembly=EyeCtrl.Controls"
        xmlns:loc="clr-namespace:EyeCtrl.Controls.Localization;assembly=EyeCtrl.Controls"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        WindowStartupLocation="CenterScreen" Topmost="True" ShowInTaskbar="False"
        Width="{x:Static overlays:SplashLayout.CardWidth}"
        Height="{x:Static overlays:SplashLayout.CardHeight}">
    <!-- NO App-level StaticResource here: the splash thread has no Application resources (spec §8).
         Theme-derived values (surfaces, text) are assigned in the constructor from Tokens' frozen
         brushes; pinned icon constants are literals with alpha in the colour bytes. -->
    <Border x:Name="CardBorder" CornerRadius="{x:Static theme:Tokens.LargeCornerRadius}"
            Padding="{x:Static overlays:SplashLayout.CardPadding}">
        <Grid x:Name="SplashContent">
            <Grid.RowDefinitions>
                <RowDefinition Height="{x:Static overlays:SplashLayout.MarkSize}"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="{x:Static overlays:SplashLayout.SentenceRowHeight}"/>
            </Grid.RowDefinitions>

            <!-- ── The mark: the accepted icon's geometry, animated (spec §4.1) ── -->
            <Grid Grid.Row="0" x:Name="MarkHost" Width="{x:Static overlays:SplashLayout.MarkSize}"
                  Height="{x:Static overlays:SplashLayout.MarkSize}" ClipToBounds="False">
                <Border x:Name="Tile" CornerRadius="37.5" Background="#141828"/>
                <Canvas>
                    <!-- bloom: radial amber, r25.5 at centre; alpha in the colour bytes -->
                    <Ellipse x:Name="Bloom" Canvas.Left="49.5" Canvas.Top="49.5" Width="51" Height="51" Opacity="0"
                             RenderTransformOrigin="0.5,0.5">
                        <Ellipse.Fill>
                            <RadialGradientBrush>
                                <GradientStop Color="#73E8A33C"/>
                                <GradientStop Color="#38E8A33C" Offset="0.55"/>
                                <GradientStop Color="#00E8A33C" Offset="1"/>
                            </RadialGradientBrush>
                        </Ellipse.Fill>
                        <Ellipse.RenderTransform>
                            <ScaleTransform x:Name="BloomScale" ScaleX="0.2" ScaleY="0.2"/>
                        </Ellipse.RenderTransform>
                    </Ellipse>
                    <!-- trail dots: r2.6 / 3.75 / 4.9 (lead dot bright amber) -->
                    <Ellipse x:Name="Trail1" Canvas.Left="72.4" Canvas.Top="72.4" Width="5.2" Height="5.2"
                             Fill="#E8A33C" Opacity="0">
                        <Ellipse.RenderTransform>
                            <TranslateTransform x:Name="Trail1Move" X="-82.5" Y="-63.75"/>
                        </Ellipse.RenderTransform>
                    </Ellipse>
                    <Ellipse x:Name="Trail2" Canvas.Left="71.25" Canvas.Top="71.25" Width="7.5" Height="7.5"
                             Fill="#E8A33C" Opacity="0">
                        <Ellipse.RenderTransform>
                            <TranslateTransform x:Name="Trail2Move" X="-82.5" Y="-63.75"/>
                        </Ellipse.RenderTransform>
                    </Ellipse>
                    <Ellipse x:Name="Trail3" Canvas.Left="70.1" Canvas.Top="70.1" Width="9.8" Height="9.8"
                             Fill="#F6C77E" Opacity="0">
                        <Ellipse.RenderTransform>
                            <TranslateTransform x:Name="Trail3Move" X="-82.5" Y="-63.75"/>
                        </Ellipse.RenderTransform>
                    </Ellipse>
                    <!-- gaze dot r12 -->
                    <Ellipse x:Name="GazeDot" Canvas.Left="63" Canvas.Top="63" Width="24" Height="24"
                             Fill="#E8A33C" Opacity="0" RenderTransformOrigin="0.5,0.5">
                        <Ellipse.RenderTransform>
                            <ScaleTransform x:Name="DotScale" ScaleX="0" ScaleY="0"/>
                        </Ellipse.RenderTransform>
                    </Ellipse>
                    <!-- dwell arc r57 w4.9, rest = 120° from 12 o'clock CW; dash units are stroke
                         multiples: arc length 119.4 px / stroke 4.9 ≈ 24.36 (spec §5) -->
                    <Path x:Name="DwellArc"
                          Data="M75,18 A57,57 0 0 1 124.36,103.5"
                          Stroke="#A5F3FC" StrokeThickness="4.9"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeDashCap="Round"
                          StrokeDashArray="24.36 1000" StrokeDashOffset="24.36" Opacity="0">
                        <Path.RenderTransform>
                            <RotateTransform x:Name="ArcRotate" Angle="-180" CenterX="75" CenterY="75"/>
                        </Path.RenderTransform>
                    </Path>
                </Canvas>
            </Grid>

            <TextBlock Grid.Row="1" x:Name="TitleText" Text="EyeCtrl" HorizontalAlignment="Center"
                       Opacity="0" Margin="0,12,0,8"
                       FontSize="{x:Static theme:Tokens.FontSizeDisplay}"
                       FontWeight="{x:Static theme:Tokens.FontWeightMedium}">
                <TextBlock.RenderTransform>
                    <TranslateTransform x:Name="TitleRise" Y="8"/>
                </TextBlock.RenderTransform>
            </TextBlock>

            <TextBlock Grid.Row="2" x:Name="NoTrackerSentence"
                       AutomationProperties.AutomationId="Splash_NoTracker"
                       TextWrapping="Wrap" TextAlignment="Center" HorizontalAlignment="Center"
                       Opacity="0"
                       FontSize="{x:Static theme:Tokens.FontSizeSubtitle}"
                       Text="{loc:T 'overlays.splash.noTracker'}"/>
        </Grid>
    </Border>
</overlays:OverlayWindow>
```

- [ ] **Step 4: Rewrite SplashWindow.xaml.cs (no controller — that is Task 4)**

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media.Animation;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using Serilog;

public partial class SplashWindow : OverlayWindow
{
    private bool _sentenceShown;

    public SplashWindow()
    {
        InitializeComponent();

        // The splash thread has no Application resources: theme-derived values come from Tokens'
        // frozen brushes (they freeze at source — ThemeBase.CachedBrush), pinned colors are XAML
        // literals (spec §8). Fonts/radius already ride x:Static, which reads Tokens.Current at
        // parse time — the theme is activated before this thread starts.
        CardBorder.Background = Tokens.BrushSurfaceDark;
        TitleText.Foreground = Tokens.BrushTextPrimary;
        NoTrackerSentence.Foreground = Tokens.BrushTextPrimary;

        IsClickThrough = true;                          // L13: the card must never swallow dwells
    }

    /// <summary>The order repair would run from the splash thread and touch main-thread windows
    /// (r2 #4): the splash never asks for it.</summary>
    protected override bool NeedsOrderRepairAfterBandFallback => false;

    /// <summary>Test/verification accessor for the band-fallback override.</summary>
    internal bool DeclaresNoOrderRepair => NeedsOrderRepairAfterBandFallback;

    /// <summary>Whether ShowNoTrackerSentence has been called.</summary>
    internal bool SentenceShown => _sentenceShown;

    /// <summary>
    /// Whether the splash says "no eye tracker found" (Wave B B5): only when the provider FELL BACK
    /// to the mouse — a tracker was asked for (Auto, or a Tobii type) and the mouse is what resolved.
    /// Never when the mouse was chosen explicitly, and never in test mode, whose provider is always
    /// the mouse.
    /// </summary>
    /// <param name="requested">The provider type the settings asked for.</param>
    /// <param name="resolved">The provider type actually in use (decorators unwrapped).</param>
    /// <param name="isTestMode">Whether the app runs in test mode.</param>
    /// <returns>True when the sentence should show.</returns>
    internal static bool ShowsNoTrackerSentence(GazeProviderType requested, GazeProviderType resolved, bool isTestMode) =>
        !isTestMode && requested != GazeProviderType.Mouse && resolved == GazeProviderType.Mouse;

    /// <summary>
    /// Fades the no-tracker sentence in inside its always-reserved row (spec §7). The fade runs
    /// only when the window is visible (production); on an unshown window — tests, headless — the
    /// opacity is set directly so the outcome is deterministic.
    /// </summary>
    internal void ShowNoTrackerSentence()
    {
        if (_sentenceShown) return;
        _sentenceShown = true;
        try
        {
            if (IsVisible)
            {
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
                NoTrackerSentence.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            else
            {
                NoTrackerSentence.Opacity = 1;
            }
        }
        catch (Exception ex)
        {
            NoTrackerSentence.Opacity = 1;              // containment: shown statically beats lost
            Log.Warning(ex, "Splash: sentence fade failed; shown statically");
        }
    }

    /// <summary>Stub for Task 6's proxy — replaced by the beat glue in Task 5.</summary>
    internal void BeginExit() => ForceClose();

    /// <summary>Exception-contained hard close used by the fatal path and the dead-man timers.</summary>
    internal void ForceClose()
    {
        try { Close(); }
        catch (Exception ex) { Log.Warning(ex, "Splash: hard close failed"); }
    }
}
```

- [ ] **Step 5: Update the sentence-visibility test to the opacity mechanism**

In `SplashNoTrackerSentenceTests.cs`, replace the body of `TheSentence_IsHiddenUntilAsked_AndReadsItsString`:

```csharp
    [StaFact]
    public void TheSentence_IsHiddenUntilAsked_AndReadsItsString()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = InvisibleTestWindow.Prepare(new SplashWindow()); // never shown
        try
        {
            Check.That(splash.SentenceShown).IsFalse();
            Check.That(splash.NoTrackerSentence.Opacity).IsEqualTo(0);   // reserved row, hidden by opacity
            Check.That(splash.NoTrackerSentence.Text).IsEqualTo(StringTable.Current.T("overlays.splash.noTracker"));

            splash.ShowNoTrackerSentence();

            Check.That(splash.SentenceShown).IsTrue();
            Check.That(splash.NoTrackerSentence.Opacity).IsEqualTo(1);   // unshown window → direct set (deterministic)
        }
        finally { splash.Close(); }
    }
```

- [ ] **Step 6: Touch up the AppIconTests comment (A3)**

Replace the comment block above `Splash_Decode_Yields128PxFrame` (~lines 89-91):

```csharp
    // A3 — WPF shows WIC's first-enumerated frame; with the BMP-16..128 + PNG-256 layout that
    // is the 128 px frame (r2-measured). The splash no longer hosts the ico Image (the animated
    // mark is drawn live), so this pin now covers the exe/tray consumers of the same pack URI;
    // the ImageSourceConverter path below is unchanged.
```

- [ ] **Step 7: Run the full Overlays + Assets + Fit test slice**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowConstructionTests|FullyQualifiedName~SplashNoTrackerSentenceTests|FullyQualifiedName~OverlayClientOriginTests|FullyQualifiedName~AppIconTests|FullyQualifiedName~TheSplashSentence_FitsTheReservedRow"`
Expected: ALL PASS — including the Task 2 gate, now that the real window exists.

- [ ] **Step 8: Commit (Task 2's constants + gate land together with the window)**

```bash
git add src/EyeCtrl.App/Overlays/SplashLayout.cs src/EyeCtrl.App/Overlays/SplashWindow.xaml src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs tests/EyeCtrl.App.Tests/Overlays/SplashNoTrackerSentenceTests.cs tests/EyeCtrl.App.Tests/Overlays/SplashWindowConstructionTests.cs tests/EyeCtrl.App.Tests/Assets/AppIconTests.cs tests/EyeCtrl.App.Tests/Localization/Fit/GermanGateTests.cs
git commit -m "feat(splash): animated mark card (tile, trail, dot, arc), reserved sentence row, click-through, fit gate"
```

---

### Task 4: SplashExitController — pure beat/catch-up/dead-man decisions

**Files:**
- Create: `src/EyeCtrl.App/Overlays/SplashExitController.cs`
- Test: `tests/EyeCtrl.App.Tests/Overlays/SplashExitControllerTests.cs`

**Interfaces:**
- Produces (used by Task 5's glue): `SplashBeat` enum (`Idle, Entrance, Fill, Filled, CatchUp, Title, Hold, Exit, Closed` — `Filled` = base course done, waiting for readiness, r4 #5), `SplashExitController` with `Beat`, `ComputeCatchUpDuration(TimeSpan)`, `TryBeginExit(TimeSpan, out TimeSpan)`, `AdvanceTo(SplashBeat)`, `MarkClosed()`, `ShouldDeadManClose(TimeSpan)`, `ShouldHardClose(TimeSpan)`, constants `DeadManTimeout=15 s`, `HardExitDeadline=3 s`, `CatchUpFloor=1 s`, `FillBaseCourse=3 s`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/SplashExitControllerTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using Xunit;

/// <summary>
/// Pure decisions of the splash lifecycle (spec §6): the L16 catch-up rule (only shortens what
/// remains — r3 #1), beat transitions, exit-request acceptance windows (any live beat BEFORE the
/// tail; ignored once the tail has begun — r4 #5), and the dead-man/hard-deadline predicates.
/// No WPF. All durations in SECONDS of base course.
/// </summary>
public class SplashExitControllerTests
{
    [Theory]
    [InlineData(0.15, 0.15)]     // remaining below the floor: base pace, never slowed (r3 #1)
    [InlineData(0.34, 0.34)]     // the sub-0.35 band the r3 review called out
    [InlineData(0.99, 0.99)]     // between the bands: still base pace
    [InlineData(1.0, 1.0)]       // exactly the floor
    [InlineData(2.5, 1.0)]       // above the floor: compressed to it
    [InlineData(5.0, 1.0)]
    public void ComputeCatchUpDuration_OnlyShortensWhatRemains(double remainingSeconds, double expectedSeconds)
    {
        Check.That(SplashExitController.ComputeCatchUpDuration(TimeSpan.FromSeconds(remainingSeconds)))
            .IsEqualTo(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void ComputeCatchUpDuration_ZeroRemaining_StaysZero()
    {
        Check.That(SplashExitController.ComputeCatchUpDuration(TimeSpan.Zero)).IsEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void TryBeginExit_IsIdempotent_AndReportsDuration()
    {
        var controller = new SplashExitController();
        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(3), out var first)).IsTrue();
        Check.That(first).IsEqualTo(TimeSpan.FromSeconds(1));
        Check.That(controller.Beat).IsEqualTo(SplashBeat.CatchUp);

        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(3), out _)).IsFalse();
        Check.That(controller.Beat).IsEqualTo(SplashBeat.CatchUp);
    }

    [Theory]
    [InlineData(SplashBeat.Idle)]
    [InlineData(SplashBeat.Entrance)]
    [InlineData(SplashBeat.Fill)]
    [InlineData(SplashBeat.Filled)]
    public void TryBeginExit_IsAcceptedFromAnyBeatBeforeTheTail(SplashBeat beat)
    {
        var controller = new SplashExitController();
        controller.AdvanceTo(beat);
        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(2), out var d)).IsTrue();
        Check.That(d).IsEqualTo(TimeSpan.FromSeconds(1));
        Check.That(controller.Beat).IsEqualTo(SplashBeat.CatchUp);
    }

    [Theory]
    [InlineData(SplashBeat.CatchUp)]
    [InlineData(SplashBeat.Title)]
    [InlineData(SplashBeat.Hold)]
    [InlineData(SplashBeat.Exit)]
    [InlineData(SplashBeat.Closed)]
    public void TryBeginExit_IsRejectedOnceTheTailHasBegun(SplashBeat beat)
    {
        var controller = new SplashExitController();
        controller.AdvanceTo(beat);
        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(3), out _)).IsFalse();
        Check.That(controller.Beat).IsEqualTo(beat);          // untouched — the tail keeps running
    }

    [Fact]
    public void DeadMan_FiresOnlyPastFifteenSeconds_WhenNotClosed()
    {
        var controller = new SplashExitController();
        Check.That(controller.ShouldDeadManClose(TimeSpan.FromSeconds(14.9))).IsFalse();
        Check.That(controller.ShouldDeadManClose(TimeSpan.FromSeconds(15))).IsTrue();

        controller.MarkClosed();
        Check.That(controller.ShouldDeadManClose(TimeSpan.FromSeconds(60))).IsFalse();
    }

    /// <summary>The arming itself is glue-side (Task 5); the predicate here is stateless vs it.</summary>
    [Fact]
    public void HardDeadline_FiresOnlyPastThreeSeconds_WhenNotClosed()
    {
        var controller = new SplashExitController();
        Check.That(controller.ShouldHardClose(TimeSpan.FromSeconds(2.9))).IsFalse();
        Check.That(controller.ShouldHardClose(TimeSpan.FromSeconds(3))).IsTrue();

        controller.MarkClosed();
        Check.That(controller.ShouldHardClose(TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Fact]
    public void AdvanceTo_DoesNotReviveAClosedController()
    {
        var controller = new SplashExitController();
        controller.MarkClosed();
        controller.AdvanceTo(SplashBeat.Title);
        Check.That(controller.Beat).IsEqualTo(SplashBeat.Closed);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashExitControllerTests"`
Expected: **compile error** — the type does not exist yet.

- [ ] **Step 3: Implement the controller**

```csharp
namespace EyeCtrl.App.Overlays;

using System;

/// <summary>Beats of the splash lifecycle (spec §6). Order: Idle → Entrance → Fill → Filled →
/// CatchUp → Title → Hold → Exit → Closed. <see cref="Filled"/> = the base course finished on its
/// own and the splash waits for readiness (the tail starts at readiness — spec §6.1, r4 #5).</summary>
internal enum SplashBeat
{
    Idle,
    Entrance,
    Fill,
    Filled,
    CatchUp,
    Title,
    Hold,
    Exit,
    Closed,
}

/// <summary>
/// Pure decisions for the splash lifecycle (spec §6): the L16 catch-up rule (the catch-up only
/// ever SHORTENS what remains — never below base pace, r3 #1), beat transitions, the exit-request
/// acceptance window (any beat before the tail; ignored once CatchUp/Title/Hold/Exit/Closed —
/// r4 #5), and the dead-man / hard-deadline predicates. No WPF: the SplashWindow glue drives it,
/// unit tests exercise it headless.
/// </summary>
internal sealed class SplashExitController
{
    internal static readonly TimeSpan DeadManTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan HardExitDeadline = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan CatchUpFloor = TimeSpan.FromSeconds(1);      // L16
    internal static readonly TimeSpan FillBaseCourse = TimeSpan.FromSeconds(3);    // §5 beat 3

    private SplashBeat _beat = SplashBeat.Idle;

    internal SplashBeat Beat => _beat;

    /// <summary>
    /// The L16 rule: the catch-up only ever shortens what remains — the remaining course at base
    /// pace when that is already ≤ the floor, the floor otherwise. No lower bound (r3 #1): a tiny
    /// remainder must land no later than it would have without the request.
    /// </summary>
    internal static TimeSpan ComputeCatchUpDuration(TimeSpan remainingBaseCourse) =>
        remainingBaseCourse < CatchUpFloor ? remainingBaseCourse : CatchUpFloor;

    /// <summary>
    /// The readiness request (<c>SplashWindow.BeginExit</c> → here). Idempotent, and accepted only
    /// from beats BEFORE the tail: Idle, Entrance, Fill, Filled. From CatchUp/Title/Hold/Exit/Closed
    /// it is ignored — the tail is already in flight (r4 #5). A zero remainder skips the catch-up
    /// storyboard entirely (the glue reads the returned duration).
    /// </summary>
    internal bool TryBeginExit(TimeSpan remainingBaseCourse, out TimeSpan catchUpDuration)
    {
        catchUpDuration = TimeSpan.Zero;
        if (_beat is SplashBeat.CatchUp or SplashBeat.Title or SplashBeat.Hold
            or SplashBeat.Exit or SplashBeat.Closed) return false;
        CatchUpDuration = ComputeCatchUpDuration(remainingBaseCourse);
        _beat = SplashBeat.CatchUp;
        catchUpDuration = CatchUpDuration;
        return true;
    }

    /// <summary>Moves the beat forward; a closed controller stays closed.</summary>
    internal void AdvanceTo(SplashBeat beat)
    {
        if (_beat is SplashBeat.Closed) return;
        _beat = beat;
    }

    internal void MarkClosed() => _beat = SplashBeat.Closed;

    /// <summary>Dead-man (L9): not Closed by 15 s → close now, whatever the beat.</summary>
    internal bool ShouldDeadManClose(TimeSpan elapsedSinceShow) =>
        _beat is not SplashBeat.Closed && elapsedSinceShow >= DeadManTimeout;

    /// <summary>Hard deadline (L9): BeginExit armed, not Closed within 3 s → close now.</summary>
    internal bool ShouldHardClose(TimeSpan elapsedSinceBeginExit) =>
        _beat is not SplashBeat.Closed && elapsedSinceBeginExit >= HardExitDeadline;
}
```

(Yes — the `CatchUpDuration` field assignment above would not compile; that line must simply read `catchUpDuration = ComputeCatchUpDuration(remainingBaseCourse);` with **no** `CatchUpDuration` member at all — the out parameter is the only carrier. This is the r4 #1 fix; do not add the member back.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashExitControllerTests"`
Expected: PASS (21 theory+fact cases).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/SplashExitController.cs tests/EyeCtrl.App.Tests/Overlays/SplashExitControllerTests.cs
git commit -m "feat(splash): pure exit controller — L16 catch-up rule, Filled wait, tail-locked exit request"
```

---

### Task 5: SplashWindow exit glue — storyboard clocks, catch-up from captured values, dead-man timers

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs` (extend: orchestration; replaces the Task 3 `BeginExit` stub)
- Modify: `src/EyeCtrl.App/Overlays/SplashWindow.xaml` (add `Window.Resources` storyboards)
- Test: new `tests/EyeCtrl.App.Tests/Overlays/SplashWindowExitSequenceTests.cs`
- Test: new helper `tests/EyeCtrl.App.Tests/Fixtures/StaTestPump.cs`

**Interfaces:**
- Consumes: `SplashExitController` (Task 4), element names + stubs from Task 3.
- Produces: real `BeginExit()` (From/To catch-up from captured current values — never a restart, r4 #3), `internal void OnSplashShown()`, `internal void FatalClose()`, `internal bool TryReadDpiScale(out double, out double)`, `internal SplashBeat CurrentBeat`, `internal string? ClosedReason`, `internal event Action? ExitFinished`.

- [ ] **Step 1: Write the pump helper (storyboard `Completed` fires on dispatcher ticks)**

```csharp
// tests/EyeCtrl.App.Tests/Fixtures/StaTestPump.cs
namespace EyeCtrl.App.Tests.Fixtures;

using System.Windows.Threading;

/// <summary>
/// Pumps the current dispatcher until a condition holds or the budget runs out — how STA tests
/// let WPF animation clocks advance (storyboard Completed events fire on dispatcher ticks).
/// </summary>
internal static class StaTestPump
{
    public static bool PumpUntil(Func<bool> condition, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new DispatcherOperationCallback(f => ((DispatcherFrame)f).Continue = false), frame);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
        return condition();
    }
}
```

- [ ] **Step 2: Write the failing sequence tests**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/SplashWindowExitSequenceTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The exit sequence runs on real storyboards: BeginExit commits the catch-up from the CURRENT
/// arc values, the title follows, hold and fade end in a Closed controller and a closed window.
/// Budgets sit BELOW the 3 s hard deadline so a stuck sequence fails the test instead of being
/// masked by the backstop (r4 #7). The window is never shown; animation clocks ride the STA
/// test's dispatcher via StaTestPump.
/// </summary>
[Collection("WpfApplication")]
public class SplashWindowExitSequenceTests
{
    [StaFact]
    public void BeginExit_DuringEntrance_RunsCatchUpTitleHoldExit_AndCloses()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = new SplashWindow();
        try
        {
            splash.OnSplashShown();
            splash.BeginExit();     // warm start: fill never started → full 3 s course remains

            var closed = StaTestPump.PumpUntil(
                () => splash.CurrentBeat is SplashBeat.Closed,
                TimeSpan.FromSeconds(2.5));                     // < the 3 s hard deadline
            Check.That(closed).IsTrue();
            Check.That(splash.CurrentBeat).IsEqualTo(SplashBeat.Closed);
            Check.That(splash.ClosedReason).IsEqualTo("closed");   // NOT a dead-man/hard-deadline rescue
        }
        finally { splash.ForceClose(); }
    }

    [StaFact]
    public void BeginExit_Twice_IsHarmless()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = new SplashWindow();
        try
        {
            splash.OnSplashShown();
            splash.BeginExit();
            splash.BeginExit();
            var closed = StaTestPump.PumpUntil(() => splash.CurrentBeat is SplashBeat.Closed, TimeSpan.FromSeconds(2.5));
            Check.That(closed).IsTrue();
            Check.That(splash.ClosedReason).IsEqualTo("closed");
        }
        finally { splash.ForceClose(); }
    }

    /// <summary>
    /// Slow start (spec §6.1): the base course completes on its own and the beat becomes Filled —
    /// the splash waits, no title, no exit. The tail starts only when readiness arrives (r4 #5).
    /// The pump budget (~4 s) exceeds the 3.55 s base course on purpose.
    /// </summary>
    [StaFact]
    public void SlowStart_BaseFillCompletes_WaitsInFilled_ThenClosesAfterReadiness()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = new SplashWindow();
        try
        {
            splash.OnSplashShown();
            var filled = StaTestPump.PumpUntil(() => splash.CurrentBeat is SplashBeat.Filled, TimeSpan.FromSeconds(4.2));
            Check.That(filled).IsTrue();
            Check.That(splash.ClosedReason).IsNull();           // still waiting — not closed by anything

            splash.BeginExit();                                  // remaining ≈ 0 → no catch-up, straight tail
            var closed = StaTestPump.PumpUntil(() => splash.CurrentBeat is SplashBeat.Closed, TimeSpan.FromSeconds(1.5));
            Check.That(closed).IsTrue();
            Check.That(splash.ClosedReason).IsEqualTo("closed");
        }
        finally { splash.ForceClose(); }
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowExitSequenceTests"`
Expected: **compile error** — `OnSplashShown`, `FatalClose`, `CurrentBeat`, `ClosedReason`, the real `BeginExit` do not exist (Task 3's stub closes immediately, which would mask nothing once the compile errors are fixed).

- [ ] **Step 4: Add the XAML storyboards (Window.Resources) — entrance + title + exit**

Insert before `<Border ...>` in `SplashWindow.xaml`:

```xml
    <Window.Resources>
        <!-- Trail: three decelerating arrivals, staggered 0/0.05/0.10 s, peaks .40/.55/.75,
             absorbed by 0.55 s (spec §5 beat 1). Trail2/Trail3 repeat Trail1's shape with only
             BeginTime/peak differences — kept inline on purpose. -->
        <Storyboard x:Key="SbTrail">
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail1Move" Storyboard.TargetProperty="X">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="-82.5"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.45" Value="0" KeySpline="0.3,0.75,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail1Move" Storyboard.TargetProperty="Y">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="-63.75"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.45" Value="0" KeySpline="0.3,0.75,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail1" Storyboard.TargetProperty="Opacity">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.18" Value="0.40"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.45" Value="0"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail2Move" Storyboard.TargetProperty="X" BeginTime="0:0:0.05">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="-82.5"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.45" Value="0" KeySpline="0.3,0.75,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail2Move" Storyboard.TargetProperty="Y" BeginTime="0:0:0.05">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="-63.75"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.45" Value="0" KeySpline="0.3,0.75,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail2" Storyboard.TargetProperty="Opacity" BeginTime="0:0:0.05">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.18" Value="0.55"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.45" Value="0"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail3Move" Storyboard.TargetProperty="X" BeginTime="0:0:0.10">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="-82.5"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.45" Value="0" KeySpline="0.3,0.75,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail3Move" Storyboard.TargetProperty="Y" BeginTime="0:0:0.10">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="-63.75"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.45" Value="0" KeySpline="0.3,0.75,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Trail3" Storyboard.TargetProperty="Opacity" BeginTime="0:0:0.10">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.18" Value="0.75"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.45" Value="0"/>
            </DoubleAnimationUsingKeyFrames>
        </Storyboard>

        <!-- Dot pop 0.30–0.54 s: scale 0→1.15→1.0, bloom to peak then the .25→.32 breath (§5 beat 2) -->
        <Storyboard x:Key="SbDotPop">
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="DotScale" Storyboard.TargetProperty="ScaleX" BeginTime="0:0:0.30">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.12" Value="1.15" KeySpline="0.3,0.7,0.3,1"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.24" Value="1" KeySpline="0.3,0.6,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="DotScale" Storyboard.TargetProperty="ScaleY" BeginTime="0:0:0.30">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.12" Value="1.15" KeySpline="0.3,0.7,0.3,1"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.24" Value="1" KeySpline="0.3,0.6,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="GazeDot" Storyboard.TargetProperty="Opacity" BeginTime="0:0:0.30">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.02" Value="1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="BloomScale" Storyboard.TargetProperty="ScaleX" BeginTime="0:0:0.30">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0.2"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.12" Value="1" KeySpline="0.3,0.7,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="BloomScale" Storyboard.TargetProperty="ScaleY" BeginTime="0:0:0.30">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0.2"/>
                <SplineDoubleKeyFrame KeyTime="0:0:0.12" Value="1" KeySpline="0.3,0.7,0.3,1"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimationUsingKeyFrames Storyboard.TargetName="Bloom" Storyboard.TargetProperty="Opacity" BeginTime="0:0:0.30">
                <LinearDoubleKeyFrame KeyTime="0:0:0" Value="0"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.12" Value="0.5"/>
                <LinearDoubleKeyFrame KeyTime="0:0:0.24" Value="0.25"/>
            </DoubleAnimationUsingKeyFrames>
            <DoubleAnimation Storyboard.TargetName="Bloom" Storyboard.TargetProperty="Opacity"
                             From="0.25" To="0.32" BeginTime="0:0:0.54" AutoReverse="True"
                             RepeatBehavior="Forever"/>
        </Storyboard>

        <Storyboard x:Key="SbTitle">
            <DoubleAnimation Storyboard.TargetName="TitleText" Storyboard.TargetProperty="Opacity"
                             From="0" To="1" Duration="0:0:0.25"/>
            <DoubleAnimation Storyboard.TargetName="TitleRise" Storyboard.TargetProperty="Y"
                             From="8" To="0" Duration="0:0:0.25"/>
        </Storyboard>

        <Storyboard x:Key="SbExit">
            <DoubleAnimation Storyboard.TargetName="SplashContent" Storyboard.TargetProperty="Opacity"
                             From="1" To="0" Duration="0:0:0.3"/>
        </Storyboard>
    </Window.Resources>
```

- [ ] **Step 5: Extend SplashWindow.xaml.cs with the orchestration**

Add `using System.Windows.Threading;` and `using System.Windows.Media.Animation;` (the latter from Task 3) plus the members below, replacing the Task 3 `BeginExit` stub. Add the usings:

```csharp
    private System.Diagnostics.Stopwatch _shownAt = System.Diagnostics.Stopwatch.StartNew();  // dead-man clock only
    private System.Diagnostics.Stopwatch? _exitArmedAt;
    private Clock? _baseFillClock;                    // the fill's ONE clock — BeginTime carries the 0.55 s
    private DispatcherTimer? _deadManTimer;
    private DispatcherTimer? _hardDeadlineTimer;
    private DispatcherTimer? _holdTimer;
    private Storyboard _baseFillStoryboard = null!;

    internal SplashBeat CurrentBeat => _controller.Beat;
    internal string? ClosedReason { get; private set; }

    /// <summary>Raised once after the exit finished; the LAUNCHER shuts the splash dispatcher down
    /// (the window never owns that decision — in tests nobody subscribes, so the test dispatcher
    /// survives). r4 self-review fix.</summary>
    internal event Action? ExitFinished;

    private readonly SplashExitController _controller = new();

    /// <summary>Called once on the splash thread after Show() (Task 6): entrance + the fill clock.</summary>
    internal void OnSplashShown()
    {
        SafeRun(() =>
        {
            _shownAt.Restart();
            _controller.AdvanceTo(SplashBeat.Entrance);
            StartEntrance();
            Log.Information("Splash: shown");
            ArmDeadMan();
            StartBaseFillClock();
        });
    }

    /// <summary>The readiness request — marshalled here by SplashThreadHandle (Task 6).</summary>
    internal void BeginExit()
    {
        SafeRun(() =>
        {
            var remaining = RemainingBaseCourse();
            if (!_controller.TryBeginExit(remaining, out var duration))
                return;                                     // tail already running, or dead-man closed

            _exitArmedAt ??= System.Diagnostics.Stopwatch.StartNew();   // armed only on the ACCEPTED request
            Log.Information("Splash: ready after {ShownElapsed} ms; catch-up {Duration} ms",
                _shownAt.ElapsedMilliseconds, duration.TotalMilliseconds);

            StopBaseFillClock();
            if (duration <= TimeSpan.Zero)
            {
                RunTitle();                                 // remaining ≈ 0 (slow start): straight tail
                return;
            }
            StartCatchUp(duration);
            ArmHardDeadline();
        });
    }

    private void StartEntrance()
    {
        ((Storyboard)Resources["SbTrail"]).Begin(this, true);
        ((Storyboard)Resources["SbDotPop"]).Begin(this, true);
    }

    /// <summary>
    /// The fill is ONE storyboard clock with BeginTime = 0.55 s (spec §5 beat 3) — no wall-clock
    /// bookkeeping, no second timer (r4 #10). Its Completed handler parks the beat in Filled and
    /// WAITS; only BeginExit may start the tail (spec §6.1, r4 #5).
    /// </summary>
    private void StartBaseFillClock()
    {
        _baseFillStoryboard = BuildSweepFillStoryboard(SplashExitController.FillBaseCourse, fromCurrent: false);
        _baseFillStoryboard.BeginTime = TimeSpan.FromMilliseconds(550);
        _baseFillClock = _baseFillStoryboard.CreateClock();
        _baseFillStoryboard.Completed += (_, _) =>
        {
            if (_controller.Beat is SplashBeat.Fill or SplashBeat.Entrance)
                _controller.AdvanceTo(SplashBeat.Filled);   // wait for readiness — nothing else happens
        };
        _baseFillClock.Controller.Begin();
    }

    private void StopBaseFillClock()
    {
        try { _baseFillClock?.Controller.Stop(); }
        catch (Exception ex) { Log.Debug(ex, "Splash: base fill clock already stopped"); }
        _baseFillClock = null;
    }

    private void StartCatchUp(TimeSpan duration)
    {
        _controller.AdvanceTo(SplashBeat.CatchUp);
        var catchUp = BuildSweepFillStoryboard(duration, fromCurrent: true);
        catchUp.Completed += (_, _) => RunTitle();
        catchUp.Begin(this, true);
    }

    private void RunTitle()
    {
        SafeRun(() =>
        {
            _controller.AdvanceTo(SplashBeat.Title);
            var title = (Storyboard)Resources["SbTitle"];
            Storyboard.SetTarget(title, null);              // shared resource — retarget per run
            title.Begin(this, true);
        });
    }

    private void RunExit()
    {
        SafeRun(() =>
        {
            _controller.AdvanceTo(SplashBeat.Exit);
            var exit = (Storyboard)Resources["SbExit"];
            Storyboard.SetTarget(exit, null);
            exit.Begin(this, true);
        });
    }

    /// <summary>
    /// The sweep+fill board. <paramref name="fromCurrent"/> = the catch-up: From = the CURRENT
    /// animated values (captured here — a DP read returns the effective animated value), To = the
    /// finals, cubic ease-out — the arc continues from where it is, never restarting (r4 #3).
    /// Otherwise the base course: fixed From values, the spec's KeySpline via SplineDoubleKeyFrame.
    /// </summary>
    private Storyboard BuildSweepFillStoryboard(TimeSpan duration, bool fromCurrent)
    {
        var board = new Storyboard();
        var durationKf = new System.Windows.Duration(duration);

        var angleFrom = fromCurrent ? ArcRotate.Angle : -180.0;
        var offsetFrom = fromCurrent ? DwellArc.StrokeDashOffset : 24.36;
        var opacityFrom = fromCurrent ? DwellArc.Opacity : 0.0;

        if (fromCurrent)
        {
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var rotate = new DoubleAnimation(angleFrom, 0, durationKf) { EasingFunction = easeOut };
            var fill = new DoubleAnimation(offsetFrom, 0, durationKf) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var gate = new DoubleAnimation(opacityFrom, 1, durationKf) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            board.Children.Add(rotate);
            board.Children.Add(fill);
            board.Children.Add(gate);
            Storyboard.SetTarget(rotate, ArcRotate);
            Storyboard.SetTargetProperty(rotate, new PropertyPath("Angle"));
            Storyboard.SetTarget(fill, DwellArc);
            Storyboard.SetTargetProperty(fill, new PropertyPath("StrokeDashOffset"));
            Storyboard.SetTarget(gate, DwellArc);
            Storyboard.SetTargetProperty(gate, new PropertyPath("Opacity"));
        }
        else
        {
            var rotate = new DoubleAnimationUsingKeyFrames();
            rotate.KeyFrames.Add(new LinearDoubleKeyFrame(-180, KeyTime.FromPercent(0)));
            rotate.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(0.3, 0.55, 0.3, 1)));
            var fill = new DoubleAnimationUsingKeyFrames();
            fill.KeyFrames.Add(new LinearDoubleKeyFrame(24.36, KeyTime.FromPercent(0)));
            fill.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(0.3, 0.55, 0.3, 1)));
            var gate = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
            board.Children.Add(rotate);
            board.Children.Add(fill);
            board.Children.Add(gate);
            board.Duration = durationKf;
            Storyboard.SetTarget(rotate, ArcRotate);
            Storyboard.SetTargetProperty(rotate, new PropertyPath("Angle"));
            Storyboard.SetTarget(fill, DwellArc);
            Storyboard.SetTargetProperty(fill, new PropertyPath("StrokeDashOffset"));
            Storyboard.SetTarget(gate, DwellArc);
            Storyboard.SetTargetProperty(gate, new PropertyPath("Opacity"));
        }
        return board;
    }

    /// <summary>Remaining base course at BeginExit, from the fill clock itself — never a Stopwatch
    /// (r4 #10): full course before its BeginTime fires; the un-run remainder afterwards.</summary>
    private TimeSpan RemainingBaseCourse()
    {
        var now = _baseFillClock?.CurrentTime;
        if (now is null || now.Value < TimeSpan.FromMilliseconds(550))
            return SplashExitController.FillBaseCourse;                     // not started
        var played = now.Value - TimeSpan.FromMilliseconds(550);
        var remaining = SplashExitController.FillBaseCourse - played;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void ArmDeadMan()
    {
        _deadManTimer = NewTimer(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_controller.ShouldDeadManClose(_shownAt.Elapsed)) FinishClose("dead-man");
        });
    }

    private void ArmHardDeadline()
    {
        _hardDeadlineTimer = NewTimer(TimeSpan.FromMilliseconds(250), () =>
        {
            if (_exitArmedAt is { } armed && _controller.ShouldHardClose(armed.Elapsed)) FinishClose("hard-deadline");
        });
    }

    private void RunExit()
    {
        SafeRun(() =>
        {
            _controller.AdvanceTo(SplashBeat.Exit);
            var exit = (Storyboard)Resources["SbExit"];
            Storyboard.SetTarget(exit, null);
            exit.Completed += (_, _) => FinishClose("closed");
            exit.Begin(this, true);
        });
    }

    /// <summary>The tail after the title: hold 0.20 s (self-stopping one-shot timer), then exit.</summary>
    private void RunExitAfterHold()
    {
        _holdTimer = NewTimer(TimeSpan.FromMilliseconds(200), RunExit);
    }

    /// <summary>One-shot self-stopping timer (r4 #2): DispatcherTimer repeats unless stopped —
    /// every timer here stops itself before its tick runs.</summary>
    private DispatcherTimer NewTimer(TimeSpan due, Action tick)
    {
        var timer = new DispatcherTimer(due, DispatcherPriority.Background, (_, _) => { }, Dispatcher);
        timer.Tick += (_, _) => { timer.Stop(); tick(); };
        timer.Start();
        return timer;
    }

    private void FinishClose(string reason)
    {
        if (ClosedReason is not null) return;               // idempotent: first close wins
        ClosedReason = reason;
        _controller.MarkClosed();
        StopAllTimers();
        Log.Information("Splash: closed after {ShownElapsed} ms ({Reason})", _shownAt.ElapsedMilliseconds, reason);
        ForceClose();
        ExitFinished?.Invoke();
    }

    /// <summary>The fatal-path close (App's catch path via the handle): same full teardown as any
    /// other close — controller marked closed, timers stopped, ExitFinished raised (r4 #11).</summary>
    internal void FatalClose() => SafeRun(() => FinishClose("fatal"));

    private void StopAllTimers()
    {
        _deadManTimer?.Stop();
        _hardDeadlineTimer?.Stop();
        _holdTimer?.Stop();
    }

    internal bool TryReadDpiScale(out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 0;
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not { } target) return false;
            var m = target.TransformToDevice;
            scaleX = m.M11;
            scaleY = m.M22;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Splash: DPI read failed");
            return false;
        }
    }

    /// <summary>Every public entry runs inside this: a fault degrades to a close, never a trap.</summary>
    private void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Splash: beat failed; closing");
            FinishClose("beat-failure");
        }
    }
```

**Wiring the hold:** in `RunTitle`'s `title.Completed` handler (defined when starting the title), call `RunExitAfterHold()` — i.e. start the title as:

```csharp
    private void RunTitle()
    {
        SafeRun(() =>
        {
            _controller.AdvanceTo(SplashBeat.Title);
            var title = (Storyboard)Resources["SbTitle"];
            title.Completed += (_, _) =>
            {
                _controller.AdvanceTo(SplashBeat.Hold);
                RunExitAfterHold();
            };
            Storyboard.SetTarget(title, null);
            title.Begin(this, true);
        });
    }
```

(Use THIS `RunTitle`; the earlier standalone snippet in this step is superseded. `Storyboard.SetTarget(board, null)` clears a previous run's target — the shared resource storyboards are begun against `this` each time, and the null-set keeps repeat runs from accumulating stale handlers. Because `Completed` handlers accumulate on a shared storyboard, RunTitle/RunExit must instead create a FRESH storyboard each run — see the note below.)

> **Implementation note (r4 #5 — shared storyboard handlers accumulate):** `SbTitle`/`SbExit` live in `Resources` and re-beginning them appends another `Completed` handler each run. The executor must clone per run instead:
> ```csharp
> private Storyboard CloneBoard(string key)
> {
>     var clone = ((Storyboard)Resources[key]).Clone();
>     return clone;
> }
> ```
> and use `var title = CloneBoard("SbTitle");` / `var exit = CloneBoard("SbExit");` in `RunTitle`/`RunExit`, dropping the `Storyboard.SetTarget(board, null)` lines. `Storyboard.Clone()` copies children (handlers are attached to the CLONE, so they never accumulate).

- [ ] **Step 6: Run the sequence tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowExitSequenceTests"`
Expected: PASS — all three (warm start, double-call, slow start).

- [ ] **Step 7: Re-run the Task 3 slice to catch regressions**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowConstructionTests|FullyQualifiedName~SplashNoTrackerSentenceTests|FullyQualifiedName~OverlayClientOriginTests|FullyQualifiedName~TheSplashSentence_FitsTheReservedRow"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.App/Overlays/SplashWindow.xaml src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs tests/EyeCtrl.App.Tests/Overlays/SplashWindowExitSequenceTests.cs tests/EyeCtrl.App.Tests/Fixtures/StaTestPump.cs
git commit -m "feat(splash): exit orchestration — clock-driven fill, captured-value catch-up, Filled wait, one-shot timers"
```

---

### Task 6: Splash thread + marshalled handle + DPI report

**Files:**
- Create: `src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs` (includes `ISplashWindow`, `SplashDpiReport`, `SplashThreadHandle`)
- Modify: `src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs` — `public partial class SplashWindow : OverlayWindow, ISplashWindow` (the interface members must be public; make `OnSplashShown`, `BeginExit`, `ShowNoTrackerSentence`, `ForceClose`, `FatalClose`, `TryReadDpiScale`, `ExitFinished` public — the source pins match call-shape text, not accessibility)
- Test: `tests/EyeCtrl.App.Tests/Overlays/SplashThreadLauncherTests.cs`

**Interfaces:**
- Consumes: `SplashWindow` members (Tasks 3/5).
- Produces: `SplashThreadLauncher.Start()` returning `SplashThreadHandle?` with `ShowNoTrackerSentence()`, `BeginExit()`, `Close()` (→ `FatalClose`), `TryGetDpiScale(int timeoutMs, out double sx, out double sy)`, `Inspect(Action<ISplashWindow>)` (synchronous — for tests), `Dispose()`; test seam `SplashThreadLauncher.WindowFactoryForTests` typed `Func<ISplashWindow>` (r4 #9).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/SplashThreadLauncherTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The splash thread + marshalled handle (spec §7): every public call marshals to the splash
/// thread, calls after shutdown are no-ops, only the main thread reads the DPI report, and a
/// late DPI report is discarded (r3 #2). Tests run against the real window prepared-invisible
/// via the ISplashWindow factory seam — no test shows a visible splash (r2 #3, spec §9 rev 3.2).
/// </summary>
[Collection("WpfApplication")]
public class SplashThreadLauncherTests : IDisposable
{
    private SplashThreadHandle? _handle;

    public SplashThreadLauncherTests()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        SplashThreadLauncher.WindowFactoryForTests = () => InvisibleTestWindow.Prepare(new SplashWindow());
    }

    public void Dispose() { _handle?.Dispose(); SplashThreadLauncher.WindowFactoryForTests = null; }

    private SplashThreadHandle StartHandle()
    {
        _handle = SplashThreadLauncher.Start();
        Check.That(_handle).IsNotNull();
        return _handle!;
    }

    [StaFact]
    public void ShowNoTrackerSentence_MarshalsOntoTheSplashThread()
    {
        var handle = StartHandle();
        handle.ShowNoTrackerSentence();
        var shown = false;
        var ok = StaTestPump.PumpUntil(() =>
        {
            handle.Inspect(w => shown = w.SentenceShown);   // synchronous on the splash thread
            return shown;
        }, TimeSpan.FromSeconds(3));
        Check.That(ok).IsTrue();
        Check.That(shown).IsTrue();
    }

    [Fact]
    public void LateDpiReport_IsDiscarded_AndFallbackStands()
    {
        var report = new SplashDpiReport();
        Check.That(report.TryAccept(0, out _, out _)).IsFalse();   // nothing reported yet
        report.Discard();
        report.Accept(2.0, 2.0);                                    // the "late" report
        Check.That(report.TryAccept(50, out _, out _)).IsFalse();   // discarded forever
    }

    [Fact]
    public void HandleAfterDispose_IsACompleteNoOp()
    {
        var handle = StartHandle();
        handle.Dispose();                       // shuts the splash dispatcher down
        handle.ShowNoTrackerSentence();         // must not throw
        handle.BeginExit();                     // must not throw
        handle.Close();                         // must not throw — the fatal path with the thread gone
        handle.Inspect(_ => { });               // must not throw
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadLauncherTests"`
Expected: **compile error** — the types do not exist.

- [ ] **Step 3: Implement launcher, handle, DPI report**

```csharp
namespace EyeCtrl.App.Overlays;

using System;
using System.Windows.Threading;
using Serilog;

/// <summary>
/// The narrow surface the launcher and handle need (spec §7 factory seam, r4 #9): tests may hand
/// back a fake OR the real window prepared-invisible. Implemented by SplashWindow (public members).
/// </summary>
internal interface ISplashWindow
{
    void OnSplashShown();
    void BeginExit();
    void ShowNoTrackerSentence();
    void ForceClose();
    void FatalClose();
    bool TryReadDpiScale(out double scaleX, out double scaleY);
    event Action? ExitFinished;
}

/// <summary>
/// The DPI report slot (r3 #2): the splash thread reports its scale; only the main thread accepts
/// it, and once discarded (the 500 ms wait timed out) a late report is never read — the main
/// thread's GetDpiForSystem fallback stands. Nothing here writes DpiHelper.
/// </summary>
internal sealed class SplashDpiReport
{
    private readonly TaskCompletionSource<(double Sx, double Sy)> _source =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _discarded;

    internal void Accept(double scaleX, double scaleY) => _source.TrySetResult((scaleX, scaleY));

    internal void Discard() => _discarded = true;

    internal bool TryAccept(int timeoutMs, out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 0;
        if (_discarded) return false;
        if (!_source.Task.Wait(timeoutMs)) return false;
        if (_discarded) return false;                 // raced: report arrived late
        (scaleX, scaleY) = _source.Task.Result;
        return true;
    }
}

/// <summary>
/// The marshalled splash surface (spec §7): a thread-safe handle over the splash thread. Every
/// method posts onto the splash dispatcher; calls after the dispatcher shut down (dead-man,
/// fatal path, app exit) are logged no-ops. Only the MAIN thread reads the DPI report.
/// </summary>
internal sealed class SplashThreadHandle
{
    private readonly Dispatcher _splashDispatcher;
    private readonly ISplashWindow _window;
    private readonly SplashDpiReport _dpi = new();
    private volatile bool _disposed;

    internal SplashThreadHandle(Dispatcher splashDispatcher, ISplashWindow window)
    {
        _splashDispatcher = splashDispatcher;
        _window = window;
    }

    internal void ShowNoTrackerSentence() => Post("ShowNoTrackerSentence", w => w.ShowNoTrackerSentence());

    internal void BeginExit() => Post("BeginExit", w => w.BeginExit());

    /// <summary>The fatal-path close: full teardown (controller closed, ExitFinished → dispatcher
    /// shutdown), not just a window close (r4 #11).</summary>
    internal void Close() => Post("Close", w => w.FatalClose());

    /// <summary>Main-thread read: the splash thread's reported scale, ≤ timeoutMs, else fallback.</summary>
    internal bool TryGetDpiScale(int timeoutMs, out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 0;
        if (_disposed) return false;
        var ok = _dpi.TryAccept(timeoutMs, out scaleX, out scaleY);
        if (!ok) _dpi.Discard();                      // the timeout discards late reports forever
        return ok;
    }

    /// <summary>Runs a probe SYNCHRONOUSLY on the splash thread (tests, r4 #7 — a BeginInvoke probe
    /// writes its result after the caller has read it).</summary>
    internal void Inspect(Action<ISplashWindow> probe) =>
        Invoke("Inspect", probe);

    /// <summary>Called by the launcher on the splash thread once the window's DPI is readable.</summary>
    internal void ReportDpi(double scaleX, double scaleY) => _dpi.Accept(scaleX, scaleY);

    /// <summary>App-exit safety: force-shuts the splash dispatcher if it is somehow still alive.</summary>
    internal void Dispose()
    {
        _disposed = true;
        try
        {
            _splashDispatcher.Invoke(() => { }, DispatcherPriority.Send);
            _splashDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Splash: dispatcher already gone at dispose");
        }
    }

    private void Post(string what, Action<ISplashWindow> action)
    {
        if (_disposed) return;
        try
        {
            _splashDispatcher.BeginInvoke(() =>
            {
                try { action(_window); }
                catch (Exception ex) { Log.Warning(ex, "Splash: {What} failed on the splash thread", what); }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Splash: {What} arrived after the dispatcher shut down", what);
        }
    }

    private void Invoke(string what, Action<ISplashWindow> action)
    {
        if (_disposed) return;
        try
        {
            _splashDispatcher.Invoke(() =>
            {
                try { action(_window); }
                catch (Exception ex) { Log.Warning(ex, "Splash: {What} failed on the splash thread", what); }
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Splash: {What} arrived after the dispatcher shut down", what);
        }
    }
}

/// <summary>
/// Starts the splash on its own STA thread (spec §7/§8): the main thread is blocked for the
/// splash's whole visible life, and only a second dispatcher can animate through that. The thread
/// entry is fully contained — any failure degrades to a splash-less, logged start (r2 #1). The
/// main thread's waits are bounded: ≤ 500 ms for the handle, and the DPI wait adds its own ≤ 500 ms
/// (spec §7 rev 3.2). A timed-out start CANCELS the splash before it can show (r4 #8) — the thread
/// checks the cancellation before its one visible step.
/// </summary>
internal static class SplashThreadLauncher
{
    private static volatile bool _startCancelled;

    /// <summary>Test seam (r2 #3, r4 #9): tests hand back an InvisibleTestWindow-prepared real
    /// window or a fake — anything implementing ISplashWindow.</summary>
    internal static Func<ISplashWindow>? WindowFactoryForTests { get; set; }

    internal static SplashThreadHandle? Start()
    {
        _startCancelled = false;
        var ready = new TaskCompletionSource<SplashThreadHandle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => ThreadBody(ready))
        {
            IsBackground = true,
            Name = "EyeCtrlSplash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Task.Wait(TimeSpan.FromMilliseconds(500)))
        {
            _startCancelled = true;                    // the thread must not show the window now (r4 #8)
            Log.Warning("Splash: thread did not produce a handle within 500 ms; starting without a splash");
            return null;
        }
        return ready.Task.Result;
    }

    private static void ThreadBody(TaskCompletionSource<SplashThreadHandle?> ready)
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var window = WindowFactoryForTests is { } factory ? factory() : new SplashWindow();
            var handle = new SplashThreadHandle(dispatcher, window);
            // Spec §6: a fault inside the dispatcher loop degrades to close + shutdown, never a
            // process kill and never a stuck splash.
            dispatcher.UnhandledException += (_, e) =>
            {
                e.SetHandled();
                Log.Warning(e.Exception, "Splash: unhandled exception on the splash thread; closing");
                try { window.ForceClose(); } catch { /* already closing */ }
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            };
            // The window never owns dispatcher shutdown (it cannot know which dispatcher it rides
            // in tests) — the launcher does, on the exit-finished notification.
            window.ExitFinished += () => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);

            // Hand the handle back BEFORE the first visible step, so a timed-out Start() can
            // cancel a splash that never shows (r4 #8).
            ready.TrySetResult(handle);
            if (_startCancelled)
            {
                Log.Information("Splash: start was cancelled before the first frame; no splash shown");
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                return;
            }

            window.Show();
            window.OnSplashShown();
            // The splash thread reads its own composition target; it never writes DpiHelper (r3 #2).
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (window.TryReadDpiScale(out var sx, out var sy)) handle.ReportDpi(sx, sy);
            });
            Dispatcher.Run();                          // pumps for the whole main-thread blockage
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Splash: thread failed; starting without a splash");
            ready.TrySetResult(null);
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadLauncherTests"`
Expected: PASS (3 tests — real STA threads, invisible windows).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs tests/EyeCtrl.App.Tests/Overlays/SplashThreadLauncherTests.cs
git commit -m "feat(splash): dedicated STA thread, ISplashWindow seam, marshalled handle, bounded DPI report"
```

---

### Task 7: App.xaml.cs wiring — thread start, BeginExit at readiness, OnExit safety

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` (splash site ~:139/:164-169, `InitializeDpi` ~:996, `ShowOverlays` ~:955-967, `OnExit`)
- Test: existing source pins (`SplashNoTrackerSentenceTests`, `ToolbarLifecycleCoordinatorTests`, `ControlStatusWiringTests`) must stay green; run the full App.Tests non-UI suite.

**Interfaces:**
- Consumes: `SplashThreadLauncher`/`SplashThreadHandle` (Task 6).
- Produces: production wiring — `ShowOverlays(SplashThreadHandle? splash, …)` calls `splash?.BeginExit()`; the catch path's `splash?.Close()` is the marshalled fatal close; `InitializeDpi(SplashThreadHandle? splash)` with the bounded wait.

- [ ] **Step 0: Impact analysis (repo rule — before editing shared symbols)**

Run GitNexus impact on `ShowOverlays` and `InitializeDpi` (upstream) and report the callers/processes. Record the risk level in the commit message body. If HIGH/CRITICAL, surface it before proceeding. (The GitNexus index is stale at b2cb5b6 — run `node .gitnexus/run.cjs analyze --force --index-only` first, scoped to the worktree.)

- [ ] **Step 1: Wire the splash site (replaces the construct+Show block at ~:164-169)**

```csharp
        // ── Splash screen ──
        // Own STA thread + dispatcher (spec §8): the main thread blocks from here to the end of
        // startup, and only a second dispatcher can animate through that. The handle's methods
        // marshal; a failed or timed-out thread start degrades to a splash-less, logged start
        // (spec §7) — and cancels the splash before it can show.
        if (!IsTestMode)
            splash = SplashThreadLauncher.Start();
```

The local declaration at ~:139 changes from `SplashWindow? splash = null;` to:

```csharp
        SplashThreadHandle? splash = null;
```

and after the `if (!IsTestMode)` block, stash the handle for OnExit:

```csharp
        _splashHandle = splash;
```

with the field near the other App fields:

```csharp
    private SplashThreadHandle? _splashHandle;
```

- [ ] **Step 2: Rework InitializeDpi (~:996)**

```csharp
    /// <summary>
    /// Initializes <see cref="DpiHelper"/> from the splash thread's reported composition target,
    /// waiting at most 500 ms; on timeout (or without a splash) it falls back to the Win32 system
    /// DPI — the same values the test-mode path used. ONLY this main-thread method writes
    /// DpiHelper; a splash report arriving after the timeout is discarded (r3 #2).
    /// </summary>
    private static void InitializeDpi(SplashThreadHandle? splash)
    {
        if (splash is not null && splash.TryGetDpiScale(500, out var scaleX, out var scaleY))
        {
            DpiHelper.Initialize(scaleX, scaleY);
        }
        else
        {
            var dpi = GetDpiForSystem();
            var scale = dpi / (double)DpiStandardScale;
            DpiHelper.Initialize(scale, scale);
        }
    }
```

(The old body's `if (IsTestMode)` block is replaced by the unified else — test mode has no splash, so it takes the same system-DPI fallback.)

- [ ] **Step 3: ShowOverlays + catch path + OnExit**

```csharp
    private static void ShowOverlays(
        SplashThreadHandle? splash,
        ICursorCircleOverlay cursorCircle,
        IToolbarOverlay toolbar,
        EyeCtrlSettings settings)
    {
        splash?.BeginExit();      // the exit choreography runs on the splash thread (spec §7)
        cursorCircle.Show();
        toolbar.Show();
        toolbar.SetMode(settings.Toolbar.IsDocked, settings.Toolbar.DockedEdge);
        toolbar.SetSelectionModeState(settings.SelectionMode);
        toolbar.BindToViewModel();
    }
```

The catch path at ~:728 keeps its text (`splash?.Close();`) — the type change alone makes it the marshalled fatal close. In `OnExit`, add at the top:

```csharp
        try { _splashHandle?.Dispose(); } catch { /* shutdown-time best effort */ }
```

- [ ] **Step 4: Build + run every source-pinned test + the Overlays slice**

Run: `dotnet build src/EyeCtrl.App` then
`dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashNoTrackerSentenceTests|FullyQualifiedName~ToolbarLifecycleCoordinatorTests|FullyQualifiedName~ControlStatusWiringTests|FullyQualifiedName~SplashWindowExitSequenceTests|FullyQualifiedName~SplashThreadLauncherTests|FullyQualifiedName~SplashWindowConstructionTests|FullyQualifiedName~OverlayClientOriginTests"`
Expected: ALL PASS — especially the literal-text pins.

- [ ] **Step 5: Run the full non-UI App.Tests suite**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: 0 failed.

- [ ] **Step 6: Graph change analysis (repo rule) + commit**

Run `detect_changes` (scope all, worktree param) and confirm no unexpected risk. Then:

```bash
git add src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(splash): wire the splash thread into startup — BeginExit at readiness, bounded DPI wait, OnExit safety"
```

---

### Task 8: Full gate + live-check hand-off

**Files:**
- No code. Verification only.

- [ ] **Step 1: Build the solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: 0 errors.

- [ ] **Step 2: Full non-UI suite (solution-level command — AGENTS.md-documented form)**

Run: `dotnet test --filter "Category!=UI"`
Expected: 0 failed across all projects.

- [ ] **Step 3: Confirm the tree is clean**

```bash
git status --porcelain
```
Expected: nothing tracked-dirty.

- [ ] **Step 4: Owner live check (NEVER launch without explicit owner OK — standing rule)**

Hand-off message to the owner:
1. What to watch on a launch: trail → dot pop → 1.0 s sweep+fill → title → fade; overlays (ring/toolbar) appear above the fading card; resting mark reads as the icon. On a slow start the arc completes and WAITS, then the tail runs at readiness.
2. What to check in the log: `Splash: shown` → `Splash: ready after N ms; catch-up M ms` → `Splash: closed after K ms (closed)`, with `closed − ready` ≤ ≈ 2.0 s.
3. Theme sweep: the card on Midnight, Ember, Daylight, Contrast (tile visibility on Ember/Contrast is intentional).
4. Sentence check (only if the tracker falls back): appears in the reserved row without reflow.

Do not run `EYECTRL_UI_TESTS` suites — not needed for this gate (spec §10.1).

---

## Self-Review notes (already applied while writing)

- **Spec coverage:** §4 card/geometry (Tasks 2/3), §4.1 mark (Task 3), §5 choreography (Tasks 3/5), §6 controller + rules + Filled wait (Tasks 4/5), §7 integration (Tasks 6/7), §8 threading/resources (Tasks 1/3/6), §9 tests (every task + Task 8), §10 acceptance (Task 8). L13 click-through (Task 3), L15 log lines with elapsed-ms (Tasks 5/6).
- **Type consistency:** `SplashThreadHandle`/`ISplashWindow` used in App wiring match Task 6; `BeginExit()`/`ForceClose()`/`FatalClose()`/`TryReadDpiScale` signatures identical across Tasks 3/5/6; `SplashLayout` constants identical in Tasks 2/3; `SplashBeat` values (including `Filled`) identical in Tasks 4/5.
- **r4 fixes applied:** compile set (SplineDoubleKeyFrame, PropertyPath, colour-borne alphas, no CatchUpDuration member, TimeSpan remaining, Task 3 controller-free), one-shot self-stopping timers, captured-value catch-up with CubicEase, spike loops (block inside a posted work item; B runs its dispatcher), Filled state + tail-locked exit request + slow-start sequence test, real-window fit gate + x:Static layout everywhere + gap below the title, deterministic sentence test, seconds-based controller theory, synchronous Inspect, 500 ms cancel-before-show start, ISplashWindow seam, clock-driven fill (no Stopwatch for remaining), FatalClose routing, elapsed-ms log lines, public SplashLayout.
- **Spec lines this plan changes (rev 3.2):** §6.1 "only the fixed tail remains" → explicitly the tail starts AT readiness and the beat waits in Filled; §6.2 "any state" → accepted only before the tail; §7 waits = ≤ 500 ms handle + ≤ 500 ms DPI report; §9 factory = `ISplashWindow` (fake or invisible-real).
- **Known deliberate gap (documented):** the catch-up's mid-fill continuation (BeginExit while the base fill is running, i.e. readiness between 0.55 s and 3.55 s) is not covered by a dedicated headless test — it needs a ~1 s pump after BeginExit mid-course. The warm-start and slow-start sequence tests cover both neighbours of that band, and the live check watches it. Add a mid-fill pump test if the executor wants belt-and-braces.
