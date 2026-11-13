# Startup Splash Animation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the splash as an animated brand moment — amber gaze dots arrive, the gaze dot blooms, a cyan dwell arc sweeps 180° while filling into its 120° brand position, title rises, card fades — on its own STA thread so every beat plays during the blocked main-thread startup, with T3 adaptive timing (calm 1.0 s catch-up floor).

**Architecture:** The splash runs on a dedicated STA thread with its own dispatcher (the main thread is blocked for the splash's whole visible life — spec §8). `SplashWindow` owns the visuals and WPF glue; `SplashExitController` holds the pure beat/catch-up/dead-man decisions (headless-testable); `SplashThreadLauncher`/`SplashThreadHandle` own the thread, the marshalled proxy surface, and the bounded DPI report. All beats ride that thread's storyboards; readiness arrives via `BeginExit()` marshalled from `ShowOverlays`.

**Tech Stack:** WPF (.NET 10, `net10.0-windows`), xunit v3 (`xunit.v3`) + NFluent 3.1.0, Serilog static `Log`. Spike is a standalone console tool (not in the solution), mirroring the `tools/EyeCtrl.IconBuilder` pattern.

**Spec:** `docs/superpowers/specs/2026-11-13-splash-animation-design.md` (rev 3.1, commit 57a9a5e2). Ledger L1–L16 there is binding; where this plan repeats a number, the spec wins.

## Global Constraints

- **Work in the existing worktree** `.worktrees/splash-animation` on branch `feat/splash-animation`. Do not commit anything outside it.
- **Never paint a visible test window** (AGENTS.md rule): any test touching a showing API uses `InvisibleTestWindow.Prepare(window)` first; production `Show` calls happen only on the real splash thread. The window-factory seam (Task 6) is what keeps the real thread out of tests.
- **Source pins must stay green** (spec §9): the App source keeps the literal `ShowOverlays(splash, cursorCircle, toolbar, settings);` call, the local keeps the name `splash`, `.ShowNoTrackerSentence(` appears exactly once, `SplashWindow.ShowsNoTrackerSentence(...)` keeps its call shape in both pinned sites, and the predicate stays a static on `SplashWindow`.
- **No App-level `StaticResource` / `OverlayStyles.xaml` lookups in splash XAML** — the splash thread has no Application resources. Theme values come from `Tokens` statics (`x:Static` for structs: fonts, corner radius) and frozen brushes assigned in code-behind. `Tokens.Current` is activated *before* the splash thread starts, so parse-time reads see the right theme.
- **Pinned icon constants** (theme-independent, XAML literals): amber `#E8A33C`, bright amber `#F6C77E`, cyan `#A5F3FC`, tile `#141828`.
- Card geometry (spec §4): fixed 440 × 336 DIP; padding 36/28; mark 150; gaps 12 and 8; sentence row 48 (two lines, `TextWrapping=Wrap`, always reserved).
- Choreography (spec §5): trail 0–0.55 s (staggered 0/0.05/0.10 s, peaks .40/.55/.75) → dot pop 0.30–0.54 s (scale 0→1.15→1.0) → sweep+fill from 0.55 s, base course 3.0 s (rotate −180°→0°, dash 24.36→0, ease KeySpline `.3,.55,.3,1`) → title 0.25 s → hold 0.20 s → exit fade 0.30 s.
- Timing (spec §6): catch-up duration `= min(remaining base course, 1.0 s)` (L16 — no lower bound, r3 #1); dead-man "not Closed by 15 s"; `BeginExit` arms a hard deadline at readiness + 3.0 s; both timers on the splash thread.
- DPI: only the **main thread** calls `DpiHelper.Initialize` — with the splash-reported scale (wait ≤ 500 ms) or the `GetDpiForSystem` fallback; late reports are discarded (r3 #2).
- Test commands run **one project per invocation** (multi-project args fail with MSB1008 on this box).
- NFluent naming: inclusive comparisons are `IsLessOrEqualThan`/`IsGreaterOrEqualThan`; strict are `IsStrictlyLessThan`/`IsStrictlyGreaterThan`.
- Per repo rules: run GitNexus impact analysis before modifying shared symbols (`ShowOverlays`, `InitializeDpi` in Task 7) and `detect_changes` before committing Task 7.
- No linter/formatter configured; follow file-scoped namespaces, Allman braces, `_camelCase` fields.

---

### Task 1: Threading spike — evidence for the separate-thread architecture

**Files:**
- Create: `tools/SplashThreadSpike/SplashThreadSpike.csproj`
- Create: `tools/SplashThreadSpike/Program.cs`
- Create (produced by the run): `tools/SplashThreadSpike/RESULTS.md`

**Interfaces:**
- Consumes: nothing (standalone).
- Produces: recorded evidence (RESULTS.md) that (a) a blocked STA thread freezes its own storyboards, and (b) a second STA thread's dispatcher keeps animating through that blockage, plus the frozen-brush proof. Tasks 3–6 rely on (b); RESULTS.md is committed with the plan.

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

```csharp
// tools/SplashThreadSpike/Program.cs
// Measures (a) whether storyboards on a BLOCKED STA thread advance, and (b) whether a SECOND
// STA thread's dispatcher keeps animating through that blockage — the evidence behind the
// splash-animation architecture (spec §8). Runs both windows OFF-SCREEN (Left = -32000).
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

var resultsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RESULTS.md");

double angleBlocked = 0, angleIndependent = 0;
var blockedReady = new ManualResetEventSlim(false);
var releaseBlocked = new ManualResetEventSlim(false);
var independentDone = new ManualResetEventSlim(false);
var brushProof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

// ── Thread A: window + storyboard, then deliberately blocked ──
var threadA = new Thread(() =>
{
    var window = MakeWindow("blocked", out var rotate, out var clock);
    window.Show();
    window.BeginStoryboard(clock);
    CompositionTarget.Rendering += (_, _) => Volatile.Write(ref angleBlocked, rotate.Angle);
    blockedReady.Set();
    releaseBlocked.Wait();                                   // the blockage
    window.Close();
    System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
});
threadA.SetApartmentState(ApartmentState.STA);
threadA.IsBackground = true;
threadA.Start();
blockedReady.Wait(5000);

// ── Thread B: independent dispatcher + same storyboard (the splash pattern) ──
var threadB = new Thread(() =>
{
    try
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3C));
        brush.Freeze();                                      // frozen-brush proof
        brushProof.TrySetResult(brush.IsFrozen);
        var window = MakeWindow("independent", out var rotate, out var clock);
        window.Show();
        window.BeginStoryboard(clock);
        CompositionTarget.Rendering += (_, _) => Volatile.Write(ref angleIndependent, rotate.Angle);
        independentDone.Wait(15000);                         // pump while A is blocked
        window.Close();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
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
releaseBlocked.Set();                                        // block thread A for ~2 s
Thread.Sleep(2000);
var a1 = Volatile.Read(ref angleBlocked);
var b1 = Volatile.Read(ref angleIndependent);
independentDone.Set();

var brushFrozen = brushProof.Task.Wait(3000) && brushProof.Task.Result;
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
    Storyboard.SetTargetProperty(anim, "RenderTransform.Angle");
    return window;
}
```

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
- Create: `src/EyeCtrl.App/Overlays/SplashLayout.cs`
- Modify: `tests/EyeCtrl.App.Tests/Localization/Fit/GermanGateTests.cs` (the splash test only, ~lines 292-330)
- Test: same file (re-targeted)

**Interfaces:**
- Produces: `SplashLayout` constants (`CardWidth=440`, `CardHeight=336`, `SidePadding=36`, `InnerWidth=368`, `PaddingTop=28`, `PaddingBottom=28`, `MarkSize=150`, `GapMarkToTitle=12`, `GapTitleToSentence=8`, `SentenceRowHeight=48`) — consumed by Task 3's XAML and the fit gate.
- The old test `TheSplashSentence_FitsTheWorkArea_InEnglishAndGerman` is replaced by `TheSplashSentence_FitsTheReservedRow_InEnglishAndGerman`.

- [ ] **Step 1: Write the re-targeted gate (failing first — the constants do not exist yet)**

In `GermanGateTests.cs`, replace the splash test and its helper with:

```csharp
    /// <summary>
    /// The splash's sentence must fit the card's RESERVED two-line row (spec §4): natural width
    /// ≤ 2 × inner width so wrapping places it in two rows, wrapped height ≤ the 48 DIP row, and
    /// the whole content height ≤ the 336 DIP card. English, the German draft and the shipped
    /// German; a sentence repeated ten times must fail (the teeth). Replaces the rev-1-era
    /// work-area gate (r2 #6/#7, r3): the card is fixed-size, so the work area proves nothing.
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
        Check.WithCustomMessage($"Splash content height exceeds the card: {FitText.Dip(SplashContentHeight())} of {FitText.Dip(SplashLayout.CardHeight)}")
            .That(SplashContentHeight()).IsLessOrEqualThan(SplashLayout.CardHeight);
    }

    private static double SplashContentHeight()
    {
        var family = Tokens.FontStandard;
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal);
        var title = new FormattedText("EyeCtrl", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, Tokens.FontSizeDisplay, null, 1.0);
        return SplashLayout.PaddingTop + SplashLayout.MarkSize + SplashLayout.GapMarkToTitle
             + Math.Ceiling(title.Height) + SplashLayout.GapTitleToSentence
             + SplashLayout.SentenceRowHeight + SplashLayout.PaddingBottom;
    }

    private static FitVerdict SplashSentence(string sentence)
    {
        var family = Tokens.FontStandard;
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal);
        var natural = new FormattedText(sentence, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, Tokens.FontSizeSubtitle, null, 1.0);
        var wrapped = new TextBlock
        {
            Text = sentence,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = family,
            FontSize = Tokens.FontSizeSubtitle,
            MaxWidth = SplashLayout.InnerWidth,
        };
        wrapped.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var widthOk = natural.Width <= SplashLayout.InnerWidth * 2;
        var heightOk = wrapped.DesiredSize.Height <= SplashLayout.SentenceRowHeight;
        return new FitVerdict(widthOk && heightOk,
            $"natural {FitText.Dip(natural.Width)} (limit 2×{FitText.Dip(SplashLayout.InnerWidth)}), "
            + $"wrapped {FitText.Dip(wrapped.DesiredSize.Height)} of {FitText.Dip(SplashLayout.SentenceRowHeight)}",
            family.Source, SplashLayout.SentenceRowHeight - wrapped.DesiredSize.Height);
    }
```

Also add the missing usings at the top of the file if absent: `System.Globalization`, `System.Windows`, `System.Windows.Media`, and `EyeCtrl.Controls.Theme` (for `Tokens`).

- [ ] **Step 2: Run the gate to verify it fails to compile (constants missing)**

Run: `dotnet build tests/EyeCtrl.App.Tests`
Expected: FAIL — `SplashLayout` does not exist.

- [ ] **Step 3: Create SplashLayout**

```csharp
namespace EyeCtrl.App.Overlays;

/// <summary>
/// The splash card's fixed geometry (spec §4) — theme-independent layout constants shared by the
/// window XAML and the fit gates. All values in DIP. The card is fixed-size on purpose: the
/// no-tracker sentence's row is always reserved so showing it never reflows the card.
/// </summary>
internal static class SplashLayout
{
    internal const double CardWidth = 440;
    internal const double CardHeight = 336;
    internal const double SidePadding = 36;
    internal const double InnerWidth = CardWidth - 2 * SidePadding;   // 368
    internal const double PaddingTop = 28;
    internal const double PaddingBottom = 28;
    internal const double MarkSize = 150;
    internal const double GapMarkToTitle = 12;
    internal const double GapTitleToSentence = 8;
    internal const double SentenceRowHeight = 48;                     // two lines
}
```

- [ ] **Step 4: Run the gate to verify it passes**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~TheSplashSentence_FitsTheReservedRow"`
Expected: PASS (English + both German variants fit; tenfold fails).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/SplashLayout.cs tests/EyeCtrl.App.Tests/Localization/Fit/GermanGateTests.cs
git commit -m "feat(splash): fixed-card layout constants + reserved-row sentence fit gate"
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
- Produces: `SplashWindow` with `internal bool SentenceShown { get; }`, `internal bool DeclaresNoOrderRepair { get; }`, `internal void BeginExit()`, `internal void ForceClose()`, `internal void ShowNoTrackerSentence()` (kept — source pin), element names `NoTrackerSentence`, `TitleText`, `MarkHost`, `SplashContent`. `BeginExit`/`ForceClose` are implemented as safe stubs here (log + `Close()`), completed in Task 5 — Task 6's proxy targets these signatures.

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
            Check.That(splash.NoTrackerSentence.TextWrapping).IsEqualTo(TextWrapping.Wrap);
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
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowConstructionTests"`
Expected: FAIL — `IsClickThrough` false / `SentenceShown`, `DeclaresNoOrderRepair`, `BeginExit`, `ForceClose` missing.

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
         brushes; pinned icon constants are literals. -->
    <Border x:Name="CardBorder" CornerRadius="{x:Static theme:Tokens.LargeCornerRadius}" Padding="36,28,36,28">
        <Grid x:Name="SplashContent">
            <Grid.RowDefinitions>
                <RowDefinition Height="150"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="48"/>
            </Grid.RowDefinitions>

            <!-- ── The mark: the accepted icon's geometry, animated (spec §4.1) ── -->
            <Grid Grid.Row="0" x:Name="MarkHost" Width="150" Height="150" ClipToBounds="False">
                <Border x:Name="Tile" CornerRadius="37.5" Background="#141828"/>
                <Canvas>
                    <!-- bloom: radial amber, r25.5 at centre -->
                    <Ellipse x:Name="Bloom" Canvas.Left="49.5" Canvas.Top="49.5" Width="51" Height="51" Opacity="0"
                             RenderTransformOrigin="0.5,0.5">
                        <Ellipse.Fill>
                            <RadialGradientBrush>
                                <GradientStop Color="#E8A33C" Opacity="0.45"/>
                                <GradientStop Color="#E8A33C" Opacity="0.22" Offset="0.55"/>
                                <GradientStop Color="#E8A33C" Opacity="0" Offset="1"/>
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
                       Opacity="0" Margin="0,12,0,0"
                       FontSize="{x:Static theme:Tokens.FontSizeDisplay}"
                       FontWeight="{x:Static theme:Tokens.FontWeightMedium}">
                <TextBlock.RenderTransform>
                    <TranslateTransform x:Name="TitleRise" Y="8"/>
                </TextBlock.RenderTransform>
            </TextBlock>

            <TextBlock Grid.Row="2" x:Name="NoTrackerSentence"
                       AutomationProperties.AutomationId="Splash_NoTracker"
                       TextWrapping="Wrap" TextAlignment="Center" HorizontalAlignment="Center"
                       Opacity="0" Margin="0,8,0,0"
                       FontSize="{x:Static theme:Tokens.FontSizeSubtitle}"
                       Text="{loc:T 'overlays.splash.noTracker'}"/>
        </Grid>
    </Border>
</overlays:OverlayWindow>
```

- [ ] **Step 4: Rewrite SplashWindow.xaml.cs**

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media.Animation;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using Serilog;

public partial class SplashWindow : OverlayWindow
{
    private readonly SplashExitController _controller = new();
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

    /// <summary>Whether ShowNoTrackerSentence has been called (the fade may still be running).</summary>
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

    /// <summary>Fades the no-tracker sentence in inside its always-reserved row (spec §7).</summary>
    internal void ShowNoTrackerSentence()
    {
        if (_sentenceShown) return;
        _sentenceShown = true;
        try
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
            NoTrackerSentence.BeginAnimation(UIElement.OpacityProperty, fade);
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

- [ ] **Step 5: Run construction tests + the touched neighbours**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowConstructionTests|FullyQualifiedName~SplashNoTrackerSentenceTests|FullyQualifiedName~OverlayClientOriginTests"`
Expected: construction PASS; `SplashNoTrackerSentenceTests.TheSentence_IsHiddenUntilAsked` FAILS (still asserts `Visibility.Collapsed`) — fixed next step; `OverlayClientOriginTests` PASS (new XAML parses headless).

- [ ] **Step 6: Update the sentence-visibility test to the opacity mechanism**

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
            Check.That(splash.NoTrackerSentence.Opacity).IsEqualTo(1);   // containment sets 1 synchronously headless
        }
        finally { splash.Close(); }
    }
```

(The final `Opacity == 1` holds because the 0.25 s `BeginAnimation` on an unrendered, unpumped window is synchronously discarded by WPF's "animate without a media context → no clock" path in headless tests only when the value is set explicitly — hence the containment fallback in `ShowNoTrackerSentence` already sets it. If the assertion proves flaky on this box, assert `splash.SentenceShown` only and note it.)

- [ ] **Step 7: Touch up the AppIconTests comment (A3)**

Replace the comment block above `Splash_Decode_Yields128PxFrame` (~lines 89-91):

```csharp
    // A3 — WPF shows WIC's first-enumerated frame; with the BMP-16..128 + PNG-256 layout that
    // is the 128 px frame (r2-measured). The splash no longer hosts the ico Image (the animated
    // mark is drawn live), so this pin now covers the exe/tray consumers of the same pack URI;
    // the ImageSourceConverter path below is unchanged.
```

- [ ] **Step 8: Run the full Overlays + Assets test slice**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowConstructionTests|FullyQualifiedName~SplashNoTrackerSentenceTests|FullyQualifiedName~OverlayClientOriginTests|FullyQualifiedName~AppIconTests"`
Expected: ALL PASS.

- [ ] **Step 9: Commit**

```bash
git add src/EyeCtrl.App/Overlays/SplashWindow.xaml src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs tests/EyeCtrl.App.Tests/Overlays/SplashNoTrackerSentenceTests.cs tests/EyeCtrl.App.Tests/Overlays/SplashWindowConstructionTests.cs tests/EyeCtrl.App.Tests/Assets/AppIconTests.cs
git commit -m "feat(splash): animated mark card (tile, trail, dot, arc), reserved sentence row, click-through"
```

---

### Task 4: SplashExitController — pure beat/catch-up/dead-man decisions

**Files:**
- Create: `src/EyeCtrl.App/Overlays/SplashExitController.cs`
- Test: `tests/EyeCtrl.App.Tests/Overlays/SplashExitControllerTests.cs`

**Interfaces:**
- Produces (used by Task 5's glue and Task 6's logging): `SplashBeat` enum (`Idle, Entrance, Fill, CatchUp, Title, Hold, Exit, Closed`), `SplashExitController` with `Beat`, `ComputeCatchUpDuration(TimeSpan)`, `TryBeginExit(TimeSpan, out TimeSpan)`, `AdvanceTo(SplashBeat)`, `MarkClosed()`, `ShouldDeadManClose(TimeSpan)`, `ShouldHardClose(TimeSpan)`, constants `DeadManTimeout=15 s`, `HardExitDeadline=3 s`, `CatchUpFloor=1 s`, `FillBaseCourse=3 s`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/SplashExitControllerTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using Xunit;

/// <summary>
/// Pure decisions of the splash lifecycle (spec §6): the L16 catch-up rule (only shortens what
/// remains — r3 #1), beat transitions, exit-request idempotence, and the dead-man/hard-deadline
/// predicates. No WPF.
/// </summary>
public class SplashExitControllerTests
{
    [Theory]
    [InlineData(0.10, 0.30)]     // remaining < floor: runs at base pace (0.10 × 3.0 s), never slowed
    [InlineData(0.20, 0.60)]
    [InlineData(0.34, 1.00)]
    [InlineData(0.40, 1.00)]     // remaining course > 1.0 s: compressed to the floor
    [InlineData(1.00, 1.00)]
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
    [InlineData(SplashBeat.Title)]
    [InlineData(SplashBeat.Hold)]
    public void TryBeginExit_IsAcceptedFromAnyLiveBeat(SplashBeat beat)
    {
        var controller = new SplashExitController();
        controller.AdvanceTo(beat);
        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(2), out var d)).IsTrue();
        Check.That(d).IsEqualTo(TimeSpan.FromSeconds(1));
        Check.That(controller.Beat).IsEqualTo(SplashBeat.CatchUp);
    }

    [Fact]
    public void TryBeginExit_AfterExitOrClosed_IsRejected()
    {
        var controller = new SplashExitController();
        controller.AdvanceTo(SplashBeat.Exit);
        Check.That(controller.TryBeginExit(TimeSpan.FromSeconds(3), out _)).IsFalse();

        var closed = new SplashExitController();
        closed.AdvanceTo(SplashBeat.Exit);
        closed.MarkClosed();
        Check.That(closed.TryBeginExit(TimeSpan.FromSeconds(3), out _)).IsFalse();
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

    [Fact]
    public void HardDeadline_FiresOnlyPastThreeSeconds_AfterBeginExit_WhenNotClosed()
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
Expected: FAIL — type missing.

- [ ] **Step 3: Implement the controller**

```csharp
namespace EyeCtrl.App.Overlays;

using System;

/// <summary>Beats of the splash lifecycle (spec §6). Order: Idle → Entrance → Fill →
/// CatchUp → Title → Hold → Exit → Closed; CatchUp is the committed exit request.</summary>
internal enum SplashBeat
{
    Idle,
    Entrance,
    Fill,
    CatchUp,
    Title,
    Hold,
    Exit,
    Closed,
}

/// <summary>
/// Pure decisions for the splash lifecycle (spec §6): the L16 catch-up rule (the catch-up only
/// ever SHORTENS what remains — never below base pace, r3 #1), beat transitions, exit-request
/// idempotence, and the dead-man / hard-deadline predicates. No WPF: the SplashWindow glue drives
/// it, unit tests exercise it headless.
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
    /// The readiness request (<c>SplashWindow.BeginExit</c> → here). Idempotent: the first call
    /// from any live beat commits the catch-up and reports its duration; calls from
    /// <see cref="SplashBeat.CatchUp"/>, <see cref="SplashBeat.Exit"/> or
    /// <see cref="SplashBeat.Closed"/> are ignored (already exiting, or dead-man closed).
    /// </summary>
    internal bool TryBeginExit(TimeSpan remainingBaseCourse, out TimeSpan catchUpDuration)
    {
        catchUpDuration = TimeSpan.Zero;
        if (_beat is SplashBeat.Closed or SplashBeat.Exit or SplashBeat.CatchUp) return false;
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

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashExitControllerTests"`
Expected: PASS (all 11 cases).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/SplashExitController.cs tests/EyeCtrl.App.Tests/Overlays/SplashExitControllerTests.cs
git commit -m "feat(splash): pure exit controller — L16 catch-up rule, beat transitions, dead-man rules"
```

---

### Task 5: SplashWindow exit glue — storyboards, catch-up sequencing, dead-man timers

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs` (extend: entrance/fill/catch-up/title/hold/exit orchestration)
- Test: new `tests/EyeCtrl.App.Tests/Overlays/SplashWindowExitSequenceTests.cs`
- Test: new helper `tests/EyeCtrl.App.Tests/Fixtures/StaTestPump.cs`

**Interfaces:**
- Consumes: `SplashExitController` (Task 4), element names + stubs from Task 3.
- Produces: real `BeginExit()` (replaces the Task 3 stub), `internal void OnSplashShown()` (the launcher calls this once after `Show()` — arms the dead-man timer, starts the entrance + pending fill), `internal bool TryReadDpiScale(out double scaleX, out double scaleY)`, `internal SplashBeat CurrentBeat` (test accessor).

- [ ] **Step 1: Write the pump helper (needed because storyboard `Completed` fires on dispatcher ticks)**

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

- [ ] **Step 2: Write the failing sequence test**

```csharp
// tests/EyeCtrl.App.Tests/Overlays/SplashWindowExitSequenceTests.cs
namespace EyeCtrl.App.Tests.Overlays;

using System;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// The exit sequence runs on real storyboards: BeginExit commits the catch-up, the title follows
/// the fill, the hold and fade end in a Closed controller and a closed window. The window is
/// never shown; animation clocks ride the STA test's dispatcher via StaTestPump.
/// </summary>
[Collection("WpfApplication")]
public class SplashWindowExitSequenceTests
{
    [StaFact]
    public void BeginExit_RunsCatchUpTitleHoldExit_AndCloses()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var splash = new SplashWindow();
        try
        {
            splash.OnSplashShown();
            splash.BeginExit();     // warm-start case: fill never started → full 3 s course remains

            var closed = StaTestPump.PumpUntil(
                () => splash.CurrentBeat is SplashBeat.Closed,
                TimeSpan.FromSeconds(5));
            Check.That(closed).IsTrue();
            Check.That(splash.CurrentBeat).IsEqualTo(SplashBeat.Closed);
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
            var closed = StaTestPump.PumpUntil(() => splash.CurrentBeat is SplashBeat.Closed, TimeSpan.FromSeconds(5));
            Check.That(closed).IsTrue();
        }
        finally { splash.ForceClose(); }
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowExitSequenceTests"`
Expected: FAIL — `OnSplashShown`, real `BeginExit`, `CurrentBeat` missing (Task 3's stub closes immediately, which the first test may accidentally pass — the second's double-call and `CurrentBeat == Closed` assertions pin the real behaviour; if the stub passes both, the task's build step still fails on missing members).

- [ ] **Step 4: Extend SplashWindow.xaml.cs with the orchestration**

Add to `SplashWindow` (replacing the Task 3 `BeginExit` stub), keeping all existing members. Also add `using System.Windows.Threading;` to the file's usings (the animation using from Task 3 already covers `Storyboard`/`DoubleAnimation`/`SplineEase`/`KeySpline`):

```csharp
    private System.Diagnostics.Stopwatch _shownAt = System.Diagnostics.Stopwatch.StartNew();
    private System.Diagnostics.Stopwatch? _exitArmedAt;
    private System.Windows.Threading.DispatcherTimer? _fillStartTimer;
    private System.Windows.Threading.DispatcherTimer? _deadManTimer;
    private System.Windows.Threading.DispatcherTimer? _hardDeadlineTimer;
    private System.Windows.Threading.DispatcherTimer? _holdTimer;
    private Storyboard? _baseFillStoryboard;

    internal SplashBeat CurrentBeat => _controller.Beat;

    /// <summary>Called once on the splash thread after Show() (Task 6): entrance + timers.</summary>
    internal void OnSplashShown()
    {
        SafeRun(() =>
        {
            _shownAt.Restart();
            _controller.AdvanceTo(SplashBeat.Entrance);
            StartEntrance();
            Log.Information("Splash: shown");
            ArmDeadMan();
            // The fill starts at 0.55 s (spec §5 beat 3) unless an earlier BeginExit commits it.
            _fillStartTimer = NewTimer(TimeSpan.FromMilliseconds(550), StartBaseFill);
        });
    }

    /// <summary>The readiness request — marshalled here by SplashThreadHandle (Task 6).</summary>
    internal void BeginExit()
    {
        SafeRun(() =>
        {
            _exitArmedAt = System.Diagnostics.Stopwatch.StartNew();
            var remaining = RemainingBaseCourse();
            if (!_controller.TryBeginExit(remaining, out var duration))
                return;                                     // already exiting, or dead-man closed
            Log.Information("Splash: ready (catch-up {Duration} ms)", duration.TotalMilliseconds);
            _fillStartTimer?.Stop();
            _baseFillStoryboard?.Stop(this);
            StartCatchUp(duration);
            ArmHardDeadline();
        });
    }

    private void StartEntrance()
    {
        var trail = (Storyboard)Resources["SbTrail"];
        var pop = (Storyboard)Resources["SbDotPop"];
        trail.Begin(this, true);
        pop.Begin(this, true);
    }

    private void StartBaseFill()
    {
        if (_controller.Beat is SplashBeat.CatchUp or SplashBeat.Closed) return;   // committed exit wins
        _controller.AdvanceTo(SplashBeat.Fill);
        _baseFillStoryboard = BuildSweepFillStoryboard(SplashExitController.FillBaseCourse);
        _baseFillStoryboard.Completed += (_, _) => { if (_controller.Beat is SplashBeat.Fill) RunTitle(); };
        _baseFillStoryboard.Begin(this, true);
    }

    private void StartCatchUp(TimeSpan duration)
    {
        _controller.AdvanceTo(SplashBeat.CatchUp);
        var catchUp = BuildSweepFillStoryboard(duration);
        catchUp.Completed += (_, _) => RunTitle();
        catchUp.Begin(this, true);
    }

    private void RunTitle()
    {
        SafeRun(() =>
        {
            _controller.AdvanceTo(SplashBeat.Title);
            var title = (Storyboard)Resources["SbTitle"];
            title.Completed += (_, _) =>
            {
                _controller.AdvanceTo(SplashBeat.Hold);
                _holdTimer = NewTimer(TimeSpan.FromMilliseconds(200), RunExit);
            };
            title.Begin(this, true);
        });
    }

    private void RunExit()
    {
        SafeRun(() =>
        {
            _controller.AdvanceTo(SplashBeat.Exit);
            var exit = (Storyboard)Resources["SbExit"];
            exit.Completed += (_, _) => FinishClose("closed");
            exit.Begin(this, true);
        });
    }

    /// <summary>The sweep+fill pair for a given duration: rotation −180°→0° and dash 24.36→0,
    /// decelerating KeySpline, arc opacity gated on at the start (stray-cap mitigation).</summary>
    private Storyboard BuildSweepFillStoryboard(TimeSpan duration)
    {
        var ease = new KeySpline(0.3, 0.55, 0.3, 1.0);
        var rotate = new DoubleAnimation(-180, 0, duration)
        {
            EasingFunction = new SplineEase { EasingMode = EasingMode.EaseOut, Spline = ease },
        };
        var fill = new DoubleAnimation(24.36, 0, duration)
        {
            EasingFunction = new SplineEase { EasingMode = EasingMode.EaseOut, Spline = ease },
        };
        var gate = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        var board = new Storyboard();
        board.Children.Add(rotate);
        board.Children.Add(fill);
        board.Children.Add(gate);
        Storyboard.SetTarget(rotate, ArcRotate);
        Storyboard.SetTargetProperty(rotate, "Angle");
        Storyboard.SetTarget(fill, DwellArc);
        Storyboard.SetTargetProperty(fill, "StrokeDashOffset");
        Storyboard.SetTarget(gate, DwellArc);
        Storyboard.SetTargetProperty(gate, "Opacity");
        return board;
    }

    private double RemainingBaseCourse()
    {
        // Remaining fraction of the 3.0 s course at BeginExit: full course before the fill starts,
        // the un-run remainder afterwards (never below zero).
        var remaining = _baseFillStoryboard is null
            ? SplashExitController.FillBaseCourse
            : SplashExitController.FillBaseCourse - (_shownAt.Elapsed - TimeSpan.FromMilliseconds(550));
        return Math.Max(0, remaining.TotalSeconds);
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

    private void FinishClose(string reason)
    {
        _controller.MarkClosed();
        StopAllTimers();
        Log.Information("Splash: closed ({Reason})", reason);
        ForceClose();
        // The window does NOT own dispatcher shutdown: in STA tests CurrentDispatcher is the
        // TEST's dispatcher, and shutting it down would break the runner. The launcher (Task 6)
        // subscribes to ExitFinished and shuts the splash dispatcher down there.
        ExitFinished?.Invoke();
    }

    /// <summary>Raised once after the exit finishes and the window is closing; the launcher
    /// shuts the splash dispatcher down on this (the window itself never owns that decision).</summary>
    internal event Action? ExitFinished;

    private DispatcherTimer NewTimer(TimeSpan due, Action tick)
    {
        var timer = new DispatcherTimer(due, DispatcherPriority.Background, (_, _) => tick(), Dispatcher);
        timer.Start();
        return timer;
    }

    private void StopAllTimers()
    {
        _fillStartTimer?.Stop();
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

Add the entrance/sentence/title/exit storyboards to `SplashWindow.xaml` (inside `Window.Resources`) — the trail, dot-pop, title, sentence-fade and exit boards referenced above:

```xml
    <Window.Resources>
        <!-- Trail: three decelerating arrivals, staggered 0/0.05/0.10 s, peaks .40/.55/.75,
             absorbed by 0.55 s (spec §5 beat 1) -->
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
            <!-- Trail2: BeginTime 0.05, peak 0.55; Trail3: BeginTime 0.10, peak 0.75 — same
                 spline/shape, offsets per spec -->
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

(The comment in `SbTrail` documents Trail2/Trail3 shape repetition deliberately — they differ only in `BeginTime` and peak, so they stay inline rather than behind a shared style.)

- [ ] **Step 5: Run the sequence tests**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowExitSequenceTests"`
Expected: PASS — both sequence tests close via the real storyboard chain within 5 s.

- [ ] **Step 6: Re-run the Task 3 slice to catch regressions**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashWindowConstructionTests|FullyQualifiedName~SplashNoTrackerSentenceTests|FullyQualifiedName~OverlayClientOriginTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Overlays/SplashWindow.xaml src/EyeCtrl.App/Overlays/SplashWindow.xaml.cs tests/EyeCtrl.App.Tests/Overlays/SplashWindowExitSequenceTests.cs tests/EyeCtrl.App.Tests/Fixtures/StaTestPump.cs
git commit -m "feat(splash): exit orchestration — catch-up sequencing, dead-man timers, log lines"
```

---

### Task 6: Splash thread + marshalled handle + DPI report

**Files:**
- Create: `src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs`
- Test: `tests/EyeCtrl.App.Tests/Overlays/SplashThreadLauncherTests.cs`

**Interfaces:**
- Consumes: `SplashWindow.OnSplashShown/BeginExit/ShowNoTrackerSentence/ForceClose/TryReadDpiScale` (Tasks 3/5).
- Produces: `SplashThreadLauncher.Start()` returning `SplashThreadHandle?` with `ShowNoTrackerSentence()`, `BeginExit()`, `Close()`, `TryGetDpiScale(int timeoutMs, out double sx, out double sy)`, `Dispose()`; test seam `SplashThreadLauncher.WindowFactoryForTests`.

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
/// late DPI report is discarded (r3 #2). Tests run against a prepared-invisible window via the
/// factory seam — no test ever shows a real splash (r2 #3).
/// </summary>
[Collection("WpfApplication")]
public class SplashThreadLauncherTests : IDisposable
{
    private SplashThreadHandle? _handle;

    public void Dispose() => _handle?.Dispose();

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
        var shown = StaTestPump.PumpUntil(() =>
        {
            var shownOnThread = false;
            handle.Inspect(w => shownOnThread = w.SentenceShown);
            return shownOnThread;
        }, TimeSpan.FromSeconds(3));
        Check.That(shown).IsTrue();
    }

    [Fact]
    public void LateDpiReport_IsDiscarded_AndFallbackStands()
    {
        // No splash thread at all: simulate the timeout path directly on the handle's report slot.
        var report = new SplashDpiReport();
        Check.That(report.TryAccept(50, out var sx, out var sy)).IsFalse();   // nothing reported yet
        report.Discard();
        report.Accept(2.0, 2.0);                                              // the "late" report
        Check.That(report.TryAccept(0, out _, out _)).IsFalse();              // discarded forever
    }

    [Fact]
    public void HandleAfterDispose_IsACompleteNoOp()
    {
        var handle = StartHandle();
        handle.Dispose();
        handle.ShowNoTrackerSentence();     // must not throw
        handle.BeginExit();                 // must not throw
        handle.Close();                     // must not throw
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadLauncherTests"`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement launcher, handle, DPI report**

```csharp
namespace EyeCtrl.App.Overlays;

using System.Diagnostics;
using System.Windows.Threading;
using Serilog;

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

    internal void Accept(double scaleX, double scaleY) =>
        _source.TrySetResult((scaleX, scaleY));

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
    private readonly SplashWindow _window;
    private readonly SplashDpiReport _dpi = new();
    private volatile bool _disposed;

    internal SplashThreadHandle(Dispatcher splashDispatcher, SplashWindow window)
    {
        _splashDispatcher = splashDispatcher;
        _window = window;
    }

    internal void ShowNoTrackerSentence() => Post("ShowNoTrackerSentence", w => w.ShowNoTrackerSentence());

    internal void BeginExit() => Post("BeginExit", w => w.BeginExit());

    internal void Close() => Post("Close", w => w.ForceClose());

    /// <summary>Main-thread read: the splash thread's reported scale, ≤ timeoutMs, else fallback.</summary>
    internal bool TryGetDpiScale(int timeoutMs, out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 0;
        if (_disposed) return false;
        var ok = _dpi.TryAccept(timeoutMs, out scaleX, out scaleY);
        if (!ok) _dpi.Discard();                      // the timeout discards late reports forever
        return ok;
    }

    /// <summary>Test/inspection seam: runs a read-only probe on the splash thread.</summary>
    internal void Inspect(Action<SplashWindow> probe) => Post("Inspect", probe);

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

    private void Post(string what, Action<SplashWindow> action)
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
}

/// <summary>
/// Starts the splash on its own STA thread (spec §7/§8): the main thread is blocked for the
/// splash's whole visible life, and only a second dispatcher can animate through that. The thread
/// entry is fully contained — any failure degrades to a splash-less, logged start (r2 #1).
/// </summary>
internal static class SplashThreadLauncher
{
    /// <summary>Test seam (r2 #3): tests hand back an InvisibleTestWindow-prepared window.</summary>
    internal static Func<SplashWindow>? WindowFactoryForTests { get; set; }

    internal static SplashThreadHandle? Start()
    {
        var ready = new TaskCompletionSource<SplashThreadHandle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => ThreadBody(ready))
        {
            IsBackground = true,
            Name = "EyeCtrlSplash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(2)))
        {
            Log.Warning("Splash: thread did not produce a window in 2 s; starting without a splash");
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
            window.Show();
            window.OnSplashShown();
            // The splash thread reads its own composition target; it never writes DpiHelper (r3 #2).
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (window.TryReadDpiScale(out var sx, out var sy)) handle.ReportDpi(sx, sy);
            });
            ready.TrySetResult(handle);
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

Add the report relay to `SplashThreadHandle`:

```csharp
    /// <summary>Called by the launcher on the splash thread once the window's DPI is readable.</summary>
    internal void ReportDpi(double scaleX, double scaleY) => _dpi.Accept(scaleX, scaleY);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/EyeCtrl.App.Tests --filter "FullyQualifiedName~SplashThreadLauncherTests"`
Expected: PASS (3 tests). Note the launcher tests start REAL STA threads with invisible windows — allowed via `InvisibleTestWindow.Prepare` in the factory; set it in the test constructor:

```csharp
    public SplashThreadLauncherTests()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        SplashThreadLauncher.WindowFactoryForTests = () =>
            InvisibleTestWindow.Prepare(new SplashWindow());
    }
```

and clear it in `Dispose`: `SplashThreadLauncher.WindowFactoryForTests = null;`

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.App/Overlays/SplashThreadLauncher.cs tests/EyeCtrl.App.Tests/Overlays/SplashThreadLauncherTests.cs
git commit -m "feat(splash): dedicated STA thread, marshalled handle, bounded DPI report"
```

---

### Task 7: App.xaml.cs wiring — thread start, BeginExit at readiness, OnExit safety

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs` (splash site ~:139/:164-169, `InitializeDpi` ~:996, `ShowOverlays` ~:955-967, `OnExit`)
- Test: existing source pins (`SplashNoTrackerSentenceTests`, `ToolbarLifecycleCoordinatorTests`, `ControlStatusWiringTests`) must stay green; run the full App.Tests non-UI suite.

**Interfaces:**
- Consumes: `SplashThreadLauncher`/`SplashThreadHandle` (Task 6).
- Produces: production wiring — `ShowOverlays(SplashThreadHandle? splash, …)` calls `splash?.BeginExit()`; the catch path's `splash?.Close()` is the marshalled hard close; `InitializeDpi(SplashThreadHandle? splash)` with the bounded wait.

- [ ] **Step 0: Impact analysis (repo rule — before editing shared symbols)**

Run GitNexus impact on `ShowOverlays` and `InitializeDpi` (upstream) and report the callers/processes. Record the risk level in the commit message body. If HIGH/CRITICAL, surface it before proceeding.

- [ ] **Step 1: Wire the splash site (replaces the construct+Show block at ~:164-169)**

```csharp
        // ── Splash screen ──
        // Own STA thread + dispatcher (spec §8): the main thread blocks from here to the end of
        // startup, and only a second dispatcher can animate through that. The handle's methods
        // marshal; a failed thread start degrades to a splash-less, logged start (spec §7).
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

The catch path at ~:728 keeps its text (`splash?.Close();`) — the type change alone makes it the marshalled hard close. In `OnExit`, add at the top:

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

Run `detect_changes` (scope all) and confirm no unexpected risk. Then:

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

- [ ] **Step 2: Full non-UI suite**

Run: `dotnet test --filter "Category!=UI"`
Expected: 0 failed across all projects.

- [ ] **Step 3: Commit (if anything was left unstaged) and confirm the tree is clean**

```bash
git status --porcelain
```
Expected: only `.superpowers/`-style ignored output, nothing tracked-dirty.

- [ ] **Step 4: Owner live check (NEVER launch without explicit owner OK — standing rule)**

Hand-off message to the owner:
1. What to watch on a launch: trail → dot pop → 1.0 s sweep+fill → title → fade; overlays (ring/toolbar) appear above the fading card; resting mark reads as the icon.
2. What to check in the log: `Splash: shown` → `Splash: ready` → `Splash: closed`, with `closed − ready` ≤ ≈ 2.0 s.
3. Theme sweep: the card on Midnight, Ember, Daylight, Contrast (tile visibility on Ember/Contrast is intentional).
4. Sentence check (only if the tracker falls back): appears in the reserved row without reflow.

Do not run `EYECTRL_UI_TESTS` suites — not needed for this gate (spec §10.1).

---

## Self-Review notes (already applied while writing)

- **Spec coverage:** §4 card/geometry (Tasks 2/3), §4.1 mark (Task 3), §5 choreography (Tasks 3/5), §6 controller + rules (Tasks 4/5), §7 integration (Tasks 6/7), §8 threading/resources (Tasks 1/3/6), §9 tests (every task + Task 8), §10 acceptance (Task 8). L13 click-through (Task 3), L15 log lines (Tasks 5/6).
- **Type consistency:** `SplashThreadHandle` used in App wiring matches Task 6's class; `BeginExit()`/`ForceClose()`/`TryReadDpiScale` signatures identical across Tasks 3/5/6; `SplashLayout` constants identical in Tasks 2/3; `SplashBeat` values identical in Tasks 4/5.
- **Known deliberate gaps (documented, not placeholders):** the Trail2/Trail3 storyboards are written as inline repeats (comment in XAML explains); the sequencing tests cover the warm-start path and double-call; the cold-start (base-course completion → title without BeginExit) path is exercised only by the live check — its logic shares every branch with the tested catch-up path except `_baseFillStoryboard.Completed → RunTitle`, which the Task 5 test does not reach headless. If the executor wants belt-and-braces, add a pump test that calls `StartBaseFill()` via a test accessor and pumps past 3 s — optional, not required by the spec's gate.
