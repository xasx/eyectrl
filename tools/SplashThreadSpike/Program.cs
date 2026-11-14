// tools/SplashThreadSpike/Program.cs
// Measures (a) whether a RUNNING storyboard on a BLOCKED STA thread advances, and (b) whether a
// SECOND STA thread's dispatcher keeps animating through that blockage — the evidence behind the
// splash-animation architecture (spec §8). Both windows run OFF-SCREEN (Left = -32000).
//
// Mechanics (per the plan's practical note — `Dispatcher.DoEvents()` is not a real API):
//   * Thread A pumps its own dispatcher freely (window + storyboard + Rendering handler). The
//     blockage is POSTED onto A's dispatcher by the main thread after the warm-up, so the storyboard
//     clock is provably running when A freezes inside that work item.
//   * Thread B simply calls Dispatcher.Run() and keeps pumping; after sampling, the main thread
//     posts BeginInvokeShutdown to both dispatchers (practical-note option 2).
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.IO;
using Path = System.IO.Path;   // System.Windows.Shapes also exports a Path; keep the brief's `Path.Combine` call compiling

var resultsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RESULTS.md");

double angleBlocked = 0, angleIndependent = 0;
var blockedReady = new ManualResetEventSlim(false);    // set by A's first CompositionTarget.Rendering tick → clock provably running
var bReady = new ManualResetEventSlim(false);          // set by B's first CompositionTarget.Rendering tick → clock provably running
var blockedNow = new ManualResetEventSlim(false);      // set inside A's posted blockage work item, before it blocks
var releaseBlocked = new ManualResetEventSlim(false);  // main ends A's blockage
var brushProof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

Dispatcher? dispatcherA = null, dispatcherB = null;

// ── Thread A: window + RUNNING storyboard; the blockage is posted onto its dispatcher by the
//    main thread after the warm-up, so the clock is running when the thread freezes ──
var threadA = new Thread(() =>
{
    try
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcherA = dispatcher;
        var window = MakeWindow("blocked", out var rotate, out var clock);
        window.Show();
        window.BeginStoryboard(clock);
        CompositionTarget.Rendering += (_, _) =>
        {
            Volatile.Write(ref angleBlocked, rotate.Angle);
            blockedReady.Set();                      // a render tick has applied an animated value
        };
        Dispatcher.Run();
        window.Close();
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); }
});
threadA.SetApartmentState(ApartmentState.STA);
threadA.IsBackground = true;
threadA.Start();
if (!blockedReady.Wait(5000)) Fail("thread A never produced a render tick — spike mechanics broken");

// ── Thread B: independent dispatcher, keeps pumping (the splash pattern) ──
var threadB = new Thread(() =>
{
    try
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcherB = dispatcher;
        var brush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3C));
        brush.Freeze();                              // frozen-brush proof
        brushProof.TrySetResult(brush.IsFrozen);
        var window = MakeWindow("independent", out var rotate, out var clock);
        window.Show();
        window.BeginStoryboard(clock);
        CompositionTarget.Rendering += (_, _) =>
        {
            Volatile.Write(ref angleIndependent, rotate.Angle);
            bReady.Set();
        };
        Dispatcher.Run();                            // pumps until the main thread posts BeginInvokeShutdown
        window.Close();
    }
    catch (Exception ex) { brushProof.TrySetException(ex); }
});
threadB.SetApartmentState(ApartmentState.STA);
threadB.IsBackground = true;
threadB.Start();
if (!brushProof.Task.Wait(5000)) Fail("thread B never completed the frozen-brush proof");
if (brushProof.Task.IsFaulted) Fail($"thread B threw during setup: {brushProof.Task.Exception?.GetBaseException()}");
if (!bReady.Wait(5000)) Fail("thread B never produced a render tick — spike mechanics broken");

// warm-up so both clocks are running before the blockage
Thread.Sleep(700);

// The blockage: posted onto thread A's dispatcher, so the clock is running when the thread freezes.
// It holds THIS dispatcher thread for ~2 s.
_ = dispatcherA!.BeginInvoke(new Action(() =>     // posted work item: fires ahead of the next render tick (Normal > Render)
{
    blockedNow.Set();
    releaseBlocked.Wait();                           // holds THIS dispatcher thread for ~2 s
}));
if (!blockedNow.Wait(2000)) Fail("thread A never entered the posted blockage work item");

var a0 = Volatile.Read(ref angleBlocked);            // last value A's running clock applied before freezing
var b0 = Volatile.Read(ref angleIndependent);
Thread.Sleep(2000);                                  // the blockage window
var a1 = Volatile.Read(ref angleBlocked);            // sampled while A is provably blocked
var b1 = Volatile.Read(ref angleIndependent);        // sampled while B keeps pumping through that blockage

// release A, then shut both dispatchers down
releaseBlocked.Set();
dispatcherA.BeginInvokeShutdown(DispatcherPriority.Normal);
dispatcherB!.BeginInvokeShutdown(DispatcherPriority.Normal);
threadA.Join(2000);
threadB.Join(2000);

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

File.AppendAllText(resultsPath, $"""

Spike mechanics (implementation notes)
- `Dispatcher.DoEvents()` from the plan sketch is not a real API. Thread B simply runs
  `Dispatcher.Run()`; after sampling, the main thread posts `BeginInvokeShutdown` to both
  dispatchers (practical-note option 2). Thresholds are the plan's, unchanged:
  blocked delta < 1.0°, independent delta > 60°, brush frozen.
- Thread A pumps freely first (blockedReady = its first render tick), then the main thread posts
  the blockage work item onto A's dispatcher and waits for A to confirm it is inside that item
  (blockedNow). a0 = {a0:F1}° is the last animated value A's running clock applied — a real
  animated reading, not the 0° rest value — so the frozen clock is provably a RUNNING clock.
- a0/b0 are sampled the moment A is frozen, a1/b1 exactly 2 s later, A is then released, and both
  dispatchers are shut down cleanly.
""");

Console.WriteLine(File.ReadAllText(resultsPath));
Environment.Exit(verdict ? 0 : 1);

static void Fail(string message)
{
    Console.Error.WriteLine("SPIKE ABORTED: " + message);
    Environment.Exit(1);
}

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
