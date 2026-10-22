namespace EyeCtrl.App.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using NFluent;
using Xunit;

/// <summary>
/// The proof, without ever showing a window, that <see cref="InvisibleTestWindow"/> does its job for
/// every window type the App tests show. Each test builds a NEW window, prepares it and checks the
/// state that makes its first <c>Show()</c> invisible and non-activating — nothing here shows
/// anything. The second half pins the assumption <c>Opacity = 0</c> rests on: product code never
/// writes (or animates) the window's own Opacity after the show, so the window cannot become visible
/// again mid-test.
///
/// <para>
/// Residual risk, named: this proves the PREPARED STATE, not the pixels. A window with
/// <c>AllowsTransparency=True</c> and <c>Opacity = 0</c> paints nothing — F6's pattern, confirmed on
/// the keyboard window by the R1-c reviewers — but a future window type that is not layered, or a
/// product change that raises a window's Opacity, is caught only by the pins below (and by the first
/// real run after review, which the coordinator does with the owner's go).
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class InvisibleTestWindowTests
{
    private static string Product(string relative) =>
        File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static void AssertPrepared(Window window)
    {
        Check.WithCustomMessage($"{window.GetType().Name} must be layered (AllowsTransparency): Opacity = 0 only hides a layered window")
            .That(window.AllowsTransparency).IsTrue();
        Check.That(window.IsVisible).IsFalse();
        InvisibleTestWindow.Prepare(window);
        Check.WithCustomMessage($"{window.GetType().Name}: Opacity").That(window.Opacity).IsEqualTo(0.0);
        Check.WithCustomMessage($"{window.GetType().Name}: ShowActivated").That(window.ShowActivated).IsFalse();
        Check.WithCustomMessage($"{window.GetType().Name}: still not shown by the helper").That(window.IsVisible).IsFalse();
    }

    [StaFact]
    public void Prepare_MakesEveryShownWindowTypeTransparentAndNonActivating_BeforeAnyShow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        foreach (var window in new Window[]
                 {
                     new ToolbarWindow(), new EdgeButtonBarWindow(), new EyeCareBannerWindow(),
                     new EyeCareExerciseWindow(), new ActionMenuWindow(), new KeyboardWindow(),
                     new PauseIndicatorWindow(), new ManualSelectionGazeIndicatorWindow(), new TaskSwitcherWindow(),
                 })
        {
            try { AssertPrepared(window); }
            finally { window.Close(); }
        }
    }

    [StaFact]
    public void Prepare_ReturnsTheSameWindow_SoItWrapsAConstruction()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ToolbarWindow();
        try { Check.That(ReferenceEquals(InvisibleTestWindow.Prepare(window), window)).IsTrue(); }
        finally { window.Close(); }
    }

    [StaFact]
    public void ToolbarTestHelpers_NewBoundToolbar_ReturnsAnInvisiblePreparedWindow()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        try
        {
            Check.That(window.Opacity).IsEqualTo(0.0);
            Check.That(window.ShowActivated).IsFalse();
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void PrepareCloaked_CloaksTheNeverShownActivationButtonWindow_AndKeepsItsOpacityToItself()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActivationButtonWindow();
        try
        {
            InvisibleTestWindow.PrepareCloaked(window);

            Check.That(window.ShowActivated).IsFalse();
            Check.That(window.IsVisible).IsFalse();
            var cloaked = InvisibleTestWindow.IsCloaked(window);
            Assert.SkipWhen(cloaked is null, "DWM did not report the cloak state (no composition): the cloak cannot be read back here.");
            Check.WithCustomMessage("the window is not cloaked by DWM after PrepareCloaked").That(cloaked == true).IsTrue();
        }
        finally { window.Close(); }
    }

    // ---- the assumption Opacity = 0 rests on: product code never raises the window's own Opacity ----------

    /// <summary>Every window type the tests prepare with <c>Opacity = 0</c> (<see cref="InvisibleTestWindow.Prepare{T}"/>).</summary>
    private static readonly string[] OpacityPreparedWindowTypes =
    [
        "ToolbarWindow", "EdgeButtonBarWindow", "EyeCareBannerWindow", "EyeCareExerciseWindow", "ActionMenuWindow",
        "KeyboardWindow", "PauseIndicatorWindow", "ManualSelectionGazeIndicatorWindow", "TaskSwitcherWindow",
    ];

    private static readonly string[] OpacityPreparedWindows =
        OpacityPreparedWindowTypes.Select(t => $"Overlays/{t}.xaml.cs").ToArray();

    [Fact]
    public void TheWindowsPreparedByOpacity_NeverWriteOrAnimateTheirOwnOpacity()
    {
        // `Opacity = ...` with no receiver (the window's own), `this.Opacity`, or an animation of
        // `OpacityProperty` on the window itself. Elements inside the window (`Banner.Opacity`, `x.Opacity`)
        // have a receiver and are not the window's.
        var ownWrite = new Regex(@"(?<![\w.])(this\.)?Opacity\s*=(?!=)|(?<![\w.])(this\.)?BeginAnimation\s*\(\s*(Window\.|UIElement\.)?OpacityProperty");
        foreach (var file in OpacityPreparedWindows)
        {
            var code = NoVisibleTestWindowGuardTests.StripCommentsAndStrings(Product(file));
            Check.WithCustomMessage($"{file} writes the window's own Opacity: Opacity = 0 no longer hides it in a test")
                .That(ownWrite.IsMatch(code)).IsFalse();
        }
    }

    [Fact]
    public void NoProductFile_WritesOrAnimatesTheOpacityOfAPreparedWindowTypeFromTheOutside()
    {
        var root = Path.Combine(SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App");
        const string windowWords = "toolbar|keyboard|edgeBar|edgeButtonBar|banner|exercise|actionMenu|pauseIndicator|indicator|taskSwitcher|switcher";
        const string write = @"\.(Opacity\s*=(?!=)|BeginAnimation\s*\(\s*(Window\.|UIElement\.)?OpacityProperty)";
        // `x.Keyboard.Opacity = 1` / `Context.OverlayManager.Keyboard.Opacity = 1` (any case after a dot),
        // or a bare lower-case variable `keyboard.Opacity = 1` / `_toolbar.Opacity = 1`
        // (a bare capitalised name is an XAML element inside its own window, e.g. Banner.Opacity).
        var outside = new Regex(
            @"(?:\.(?i:" + windowWords + @")\w*|(?<![\w.])_?(?:" + windowWords + @")\w*)" + write);
        var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => outside.IsMatch(NoVisibleTestWindowGuardTests.StripCommentsAndStrings(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(root, f)).ToList();
        Check.WithCustomMessage("Product code touches the Opacity of a toolbar/keyboard/edge-bar/banner/exercise/action-menu object: " +
                                string.Join(", ", hits))
            .That(hits).IsEmpty();
    }

    [Fact]
    public void TheWindowsPreparedByOpacity_AreDeclaredLayeredInXaml()
    {
        foreach (var name in OpacityPreparedWindowTypes)
            Check.WithCustomMessage($"{name}.xaml must keep AllowsTransparency=\"True\"")
                .That(Product($"Overlays/{name}.xaml")).Contains("AllowsTransparency=\"True\"");
    }

    [Fact]
    public void TheWindowsPreparedByOpacity_HaveNoWindowOpacityAnimationSetterOrTriggerInXaml()
    {
        // The C# pins cannot see XAML: a Loaded storyboard, a style Setter or a trigger could raise the window's
        // Opacity after the show. The root element's own attributes, any <Setter Property="Opacity"> and any
        // animation whose TargetProperty names Opacity are rejected (element-level Opacity="0.9" attributes are fine).
        foreach (var name in OpacityPreparedWindowTypes)
        {
            var xaml = Regex.Replace(Product($"Overlays/{name}.xaml"), @"<!--.*?-->", "", RegexOptions.Singleline);
            var root = Regex.Match(xaml, @"<(?![?!/])[^>]*>", RegexOptions.Singleline).Value;
            Check.WithCustomMessage($"{name}.xaml: the root element sets Opacity").That(Regex.IsMatch(root, @"\sOpacity\s*=")).IsFalse();
            Check.WithCustomMessage($"{name}.xaml: a Setter targets Opacity").That(Regex.IsMatch(xaml, @"Property\s*=\s*""Opacity""")).IsFalse();
            Check.WithCustomMessage($"{name}.xaml: an animation targets Opacity")
                .That(Regex.IsMatch(xaml, @"TargetProperty\s*=\s*""[^""]*Opacity")).IsFalse();
        }
    }

    [StaFact]
    public void PrepareOffScreen_PositionsTheBackdropFarOffEveryMonitor_WhenItsSeamRuns()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            InvisibleTestWindow.PrepareOffScreen(window);

            Check.That(window.ShowActivated).IsFalse();
            window.PositionForShow(window);    // what ShowBackdrop runs before its show; nothing is shown here
            Check.WithCustomMessage("the backdrop must be positioned off every monitor").That(window.Left).IsStrictlyLessThan(-10000.0);
            Check.That(window.Top).IsStrictlyLessThan(-10000.0);
            Check.That(window.IsVisible).IsFalse();
        }
        finally { window.Close(); }
    }

    [Fact]
    public void TheActivationButtonWindow_WritesItsOwnOpacity_SoItIsCloakedNotPreparedByOpacity()
    {
        // The reason the halo tests use PrepareCloaked: ShowAt fades the window in through Opacity.
        // If this ever stops being true the note on InvisibleTestWindow.PrepareCloaked can be relaxed.
        var code = NoVisibleTestWindowGuardTests.StripCommentsAndStrings(Product("Overlays/ActivationButtonWindow.xaml.cs"));
        Check.That(Regex.IsMatch(code, @"BeginAnimation\s*\(\s*OpacityProperty")).IsTrue();
    }
}
