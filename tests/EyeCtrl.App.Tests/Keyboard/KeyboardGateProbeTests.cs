namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// The property the keyboard exists to provide: <b>looking at a key selects that key, and looking
/// anywhere else selects nothing.</b>
///
/// <para>
/// Driven through the real public entry point <see cref="KeyboardWindow.UpdateGaze"/> against a
/// real, laid-out visual tree, so it covers the whole shipped chain — key-centre collection, the
/// key-grid gate, and <c>KeyboardViewModel.ProcessKeyGaze</c>'s nearest-key latch. The oracle
/// carries NO geometry constants: each key's own reported centre is the gaze point, so it survives
/// any change to layout, key size, padding, theme or screen edge.
/// </para>
///
/// <para><b>The oracle MUST stay two-sided.</b> The positive half alone ("no key is dead") is
/// passed by <c>IsGazeInsideKeyArea =&gt; true</c> — measured: gate deleted, positive-only suite
/// 2/2 green. That mutation means gaze anywhere on screen types the nearest key, which is worse
/// than the bug this class was written for, because the nearest-key search has no maximum distance
/// (<c>KeyboardViewModel.cs:133-141</c>) and the gate is the mechanism's ONLY spatial bound. Never
/// delete <see cref="GazeClearOfTheGrid_ClearsNearestKey"/> to "simplify" this file — and note that
/// its two steps are both load-bearing: <c>NearestKeyId</c> is null at construction, so asserting
/// null without first establishing a live key is the vacuous assignment-back oracle.</para>
///
/// <para><b>Scale is the point, and it must not come from DpiHelper.</b> <c>PointToScreen</c>
/// composes the element's whole transform chain, so a <see cref="ScaleTransform"/> on the window
/// root imposes an effective scale independent of the DPI of the machine running the test — and
/// needs no <c>DpiHelper</c> write, so this class stays out of the exclusive DpiHelper collection.
/// Setting <c>DpiHelper</c> instead would be VACUOUS here: it has no influence on
/// <c>PointToScreen</c>, which is what the gate reads. Scales 1.25 and 1.5 are not padding — the
/// live fraction of a mixed-unit rect goes as 1/s², so a fix that is right at exactly 1.0 and 2.0
/// but rounds or special-cases integral scales is caught only in between.</para>
///
/// <para><b>Why a live source is required, and why it is an HwndSource rather than Show().</b>
/// <c>PointToScreen</c> throws <see cref="InvalidOperationException"/> without a
/// <see cref="PresentationSource"/> — and the gate CATCHES that and returns false
/// (<c>KeyboardWindow.xaml.cs TryGetScreenRect</c>). So a test over a sourceless visual
/// reports "outside" for every input and is green against the bug, against the fix, AND against
/// <c>return false;</c>. A source is therefore mandatory, and the <c>IsNotNull</c> guard in
/// <c>Open</c> is what stops this file ever going quietly vacuous.</para>
///
/// <para>
/// The source is an off-screen <c>WS_POPUP</c> <see cref="HwndSource"/> with no <c>WS_VISIBLE</c>,
/// NOT <c>Window.Show()</c>. Three reasons, all of them measured. (1) <c>Show()</c> does not stay
/// off-screen: <c>SizeChanged -&gt; RepositionOnScreen</c> (<c>:94</c>, <c>:187-200</c>) overwrites
/// <c>Left</c>/<c>Top</c> from <c>WorkArea</c>, so the window lands at the real docked position —
/// measured <c>(516,834)</c>, not the requested <c>-30000</c> — and this window is
/// <c>Topmost</c> with <c>AllowsTransparency</c>, which is not something to put over a
/// gaze-only user's screen. An <c>HwndSource</c>'s <c>PositionX</c> is honoured (measured
/// <c>-29984</c>) and the <c>Window</c> is never shown at all. (2) Every <c>Show()</c>-calling
/// test in this project is tagged <c>Category=UI</c>, which CI excludes — a guard that cannot run
/// in CI is a guard that launders the gap it was written to close. (3) Both routes measure the
/// same geometry: <c>KeyRows</c> 740x222 DIP, effective scale 2.00, 10 of 30 keys live on the
/// unfixed build, identical either way.</para>
///
/// <para>
/// The unbounded measure followed by <c>Arrange(DesiredSize)</c> replicates the real window's
/// <c>SizeToContent="WidthAndHeight"</c>. Arranging into a fixed box instead lets <c>KeyRows</c>
/// stretch (it has no <c>HorizontalAlignment</c>, <c>KeyboardWindow.xaml:19</c>) while the rows
/// stay centred inside it — a different layout from the shipped one. Measured: that mistake
/// reported <c>KeyRows</c> as 1984 DIP wide and 0 of 30 keys live, which looks like a far worse
/// bug and is actually a broken fixture.
/// </para>
///
/// <para>
/// <b>The shipped-layout leg is round-trip, not one-way, and the difference is load-bearing.</b>
/// Every sub-layout switch key and the emoji switch live in the bottom row, which the mixed-unit
/// gate killed entirely, and <c>ActiveSubLayoutId</c>/<c>IsEmojiMode</c> survive hide/show — so a
/// partial fix leaves the function/numbers/symbols layouts and emoji mode as permanent one-way
/// traps, worse than a dead key. "The key responds" cannot see that; "letters→numbers→letters"
/// can.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardGateProbeTests
{
    private const int SyntheticRows = 3;
    private const int SyntheticCols = 10;
    private const int DwellMs = 500;
    private const long DwellUs = DwellMs * 1000L;
    private const int WsPopup = unchecked((int)0x80000000);

    private static KeyboardLayout SyntheticGrid()
    {
        var rows = new List<List<KeyDefinition>>();
        for (var r = 0; r < SyntheticRows; r++)
        {
            var row = new List<KeyDefinition>();
            for (var c = 0; c < SyntheticCols; c++)
                row.Add(new KeyDefinition { Label = $"{r}{c}", Output = $"{r}{c}", Width = 1.0 });
            rows.Add(row);
        }
        return new KeyboardLayout
        {
            Name = "Synthetic",
            DefaultSubLayout = "letters",
            SubLayouts = new Dictionary<string, SubLayout> { ["letters"] = new() { Label = "abc", Rows = rows } },
        };
    }

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    /// <summary>One row exercising every id shape the escaping classifier keys off, plus a regular key.</summary>
    private static KeyboardLayout SpecialActionLayout()
    {
        var rows = new List<List<KeyDefinition>>
        {
            new()
            {
                new KeyDefinition { Label = "q", Output = "q", Width = 1.0 },
                new KeyDefinition { Label = "Enter", Output = "", SpecialAction = "enter", Width = 1.0 },
                new KeyDefinition { Label = "Esc", Output = "", SpecialAction = "key:Escape", Width = 1.0 },
                new KeyDefinition { Label = "Up", Output = "", SpecialAction = "key:ArrowUp", Width = 1.0 },
                new KeyDefinition { Label = "Copy", Output = "", SpecialAction = "shortcut:Ctrl+C", Width = 1.0 },
            },
        };
        return new KeyboardLayout
        {
            Name = "SpecialActions",
            DefaultSubLayout = "letters",
            SubLayouts = new Dictionary<string, SubLayout> { ["letters"] = new() { Label = "abc", Rows = rows } },
        };
    }

    private static string ShippedEmojiJson() =>
        Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Emojis", "emojis.json");

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source) : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    /// <summary>
    /// A laid-out keyboard at a chosen effective scale, with its key centres collected — hosted in
    /// an HwndSource that is never made visible, so <c>Window.Show()</c> is never called and no
    /// window ever reaches the user's screen. <paramref name="emojiJsonPath"/> is only needed by
    /// the shipped-layout round-trip leg (the emoji grid loads its data on first entry).
    /// </summary>
    private static Probe Open(KeyboardLayout layout, double effectiveScale, string? emojiJsonPath = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var w = new KeyboardWindow();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        w.SetViewModel(vm);
        w.LoadLayout(layout, 1.0, keyDwellMs: DwellMs, predictionDwellMs: 800, cooldownMs: 0);
        if (emojiJsonPath != null)
            // The recent-store path deliberately does not exist: RecentEmojiStore.Load tolerates
            // a missing file, and the probe never adds an emoji.
            w.SetEmojiData(emojiJsonPath,
                Path.Combine(Path.GetTempPath(), $"eyectrl-gate-probe-recent-{Guid.NewGuid():N}.json"));

        var content = (FrameworkElement)w.Content;
        w.Content = null;   // detach from the Window, which is never shown

        // WS_POPUP only — deliberately no WS_VISIBLE, and parked off-screen. Unlike Window.Left
        // (which WPF clamps, and which SizeChanged -> RepositionOnScreen overwrites anyway), an
        // HwndSource's PositionX is honoured: measured TL x = -29984.
        var source = new HwndSource(new HwndSourceParameters("kb-gate-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var factor = effectiveScale / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;

        // Replicates the real window's SizeToContent="WidthAndHeight". Arranging into a fixed box
        // instead lets KeyRows stretch (it has no HorizontalAlignment, KeyboardWindow.xaml:19)
        // while the rows stay centred inside it — a DIFFERENT layout from the shipped one, which
        // would silently make this probe measure the wrong geometry.
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: PointToScreen would throw, the key-grid gate would catch "
                + "it and return false, and every assertion below would pass VACUOUSLY — green "
                + "against the bug, the fix, and `return false;`. If this fires in CI, the "
                + "environment cannot create an HWND: fix the environment or the fixture, never "
                + "delete this guard.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        w.UpdateGaze(new PhysicalPoint(0, 0), 0);
        Check.WithCustomMessage("No key centres collected — the probe would measure nothing.")
             .That(Centres(w)).Not.IsEmpty();

        return new Probe(w, vm, content, source);
    }

    /// <summary>Gaze lands on <paramref name="p"/> from a clean slate; returns the latched key id.</summary>
    private static string? LookAt(Probe probe, PhysicalPoint p, ref long ts)
    {
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        probe.Window.UpdateGaze(p, ts); ts += 1_000;
        return probe.Vm.NearestKeyId;
    }

    /// <summary>
    /// Restates the latch, then holds the gaze one full dwell so the window's activation handler
    /// runs. The caller asserts the HANDLER'S EFFECT (sub-layout changed, emoji mode toggled), never
    /// the mere response of the key — that is the difference between a one-way door passing and a
    /// round trip passing.
    /// </summary>
    private static void DwellOn(Probe probe, string keyId, ref long ts)
    {
        // A completed dwell's handler may have rebuilt the key rows (sub-layout switch, emoji
        // mode). The rebuild is synchronous inside UpdateGaze, but ARRANGEMENT is not: without
        // this pass every container in the new layout has an unarranged position, all centres
        // collapse onto one point, and the latch check below blames the wrong key. The real
        // window pumps layout itself between frames — the probe must do it explicitly.
        probe.Content.UpdateLayout();

        // Clear the previous latch and refresh the centres from the CURRENT visual tree: after a
        // sub-layout switch the positions dictionary has been emptied and is only repopulated by
        // the next UpdateGaze.
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var centres = Centres(probe.Window);
        Check.WithCustomMessage(
                $"Key '{keyId}' is not in the current layout's reported centres "
                + $"(known: {string.Join(", ", centres.Keys.OrderBy(k => k, StringComparer.Ordinal))}). "
                + "If this is the first step of a round trip, the key never came back.")
            .That(centres.ContainsKey(keyId)).IsTrue();

        var centre = centres[keyId];
        var latchTs = ts;
        probe.Window.UpdateGaze(centre, latchTs); ts += 1_000;
        Check.WithCustomMessage(
                $"Gaze at '{keyId}''s own reported centre did not latch it "
                + $"(latched '{probe.Vm.NearestKeyId}') — the dwell that follows would never complete.")
            .That(probe.Vm.NearestKeyId).IsEqualTo(keyId);

        // Hold it for a full dwell in a CONTINUOUS 50 ms stream, then the completing sample. A
        // single jump to latchTs + 500 ms would be restarted by the F3 gap clamp (any silence
        // over 150 ms resets the dwell rather than being credited) and the activation would never
        // happen — the same class of fixture trap as the post-rebuild layout pass above.
        for (var t = latchTs + 50_000; t < latchTs + DwellUs; t += 50_000)
            probe.Window.UpdateGaze(centre, t);
        probe.Window.UpdateGaze(centre, latchTs + DwellUs);
        ts = latchTs + DwellUs + 1_000;
    }

    // ---- positive half: no key is dead ----

    [StaTheory]
    [InlineData(1.0)]   // control: the scale at which this bug class is numerically invisible
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]   // this user's actual display scale
    public void EveryKey_RespondsToGazeAtItsOwnCentre(double effectiveScale)
    {
        var probe = Open(SyntheticGrid(), effectiveScale);
        try
        {
            long ts = 1_000;
            var centres = new Dictionary<string, PhysicalPoint>(Centres(probe.Window));
            var dead = centres
                .Where(kv => LookAt(probe, kv.Value, ref ts) != kv.Key)
                .Select(kv => kv.Key).ToList();

            Check.WithCustomMessage(
                    $"{dead.Count} of {centres.Count} keys did not respond to a gaze sample "
                    + $"placed exactly on the key's own reported centre, at effective scale "
                    + $"{effectiveScale}. Dead keys: {string.Join(", ", dead)}")
                .That(dead).IsEmpty();
        }
        finally { probe.Dispose(); }
    }

    // ---- negative half: nothing else is live. DO NOT DELETE (see class doc). ----

    /// <summary>
    /// Establishes a LIVE nearest key first, THEN moves gaze clear of the grid.
    ///
    /// <para>
    /// Both halves are load-bearing. Step 1 exists because <c>NearestKeyId</c> is null at
    /// construction, so a bare <c>IsNull</c> after a reset sample is the vacuous assignment-back
    /// oracle this repo has been bitten by — it passes against the correct fix for the wrong
    /// reason.
    /// </para>
    ///
    /// <para>
    /// The 1 ms step is load-bearing too. Under a gate that never says no, the nearest key to the
    /// point 2000 px clear of the grid is the grid's top-left corner, and step 1 latches that same
    /// key (it is the dictionary's first entry). A dwell-length gap would therefore take step 2
    /// into the same-nearest branch and reach the activation at
    /// <c>KeyboardViewModel.cs:155-161</c>, whose branch nulls <c>NearestKeyId</c> itself
    /// (<c>:159</c>) — so the assertion would pass with the gate uninvolved. The post-activation
    /// cooldown (<c>:119-127</c>) only keeps a LATER sample null; it is not what clears the first
    /// one, and citing it would name the wrong mechanism. 1 ms keeps step 2 far short of the
    /// 500 ms dwell, so no activation branch can run.
    /// </para>
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void GazeClearOfTheGrid_ClearsNearestKey(double effectiveScale)
    {
        var probe = Open(SyntheticGrid(), effectiveScale);
        try
        {
            var (firstId, firstCentre) = Centres(probe.Window).First();

            // 1. A live nearest key. Without this the negative assertion below is vacuous.
            probe.Window.UpdateGaze(firstCentre, 0);
            Check.WithCustomMessage(
                    "Could not establish a live nearest key, so the negative assertion that follows "
                    + "would be vacuous.")
                .That(probe.Vm.NearestKeyId).IsEqualTo(firstId);

            // 2. Far outside, derived from the app's own reported geometry so it assumes nothing
            //    about where RepositionOnScreen actually put the window. 1 ms later: far short of
            //    the 500 ms dwell, so no activation can null this for the wrong reason.
            var centres = new Dictionary<string, PhysicalPoint>(Centres(probe.Window));
            var far = new PhysicalPoint(
                centres.Values.Min(p => p.X) - 2000,
                centres.Values.Min(p => p.Y) - 2000);
            probe.Window.UpdateGaze(far, 1_000);

            Check.WithCustomMessage(
                    $"Gaze 2000 px clear of the grid still held key '{probe.Vm.NearestKeyId}' at "
                    + $"effective scale {effectiveScale}. The gate is the ONLY spatial bound in the "
                    + "mechanism (the nearest-key search has no maximum distance), so if this fails, "
                    + "gaze anywhere on screen types while the keyboard is open.")
                .That(probe.Vm.NearestKeyId).IsNull();
        }
        finally { probe.Dispose(); }
    }

    // ---- the id convention the escaping dwell floor is classified from ----

    /// <summary>
    /// Pins the id convention at <c>KeyboardWindow.RebuildKeys</c>: for every key with a
    /// <c>SpecialAction</c>, the registered dwell-target id IS the SpecialAction string.
    ///
    /// <para>
    /// This is safety-critical now: <c>KeyboardSpecialActions.IsAppEscaping</c> classifies a key by
    /// that id and <c>KeyboardViewModel</c> holds the escaping keys' dwell floor under it. A change
    /// to the id minting — a prefix, a lowercasing, a sanitized form — would silently un-class every
    /// app-escaping key and drop the floor while the whole suite stayed green. The test runs the real
    /// window over a synthetic layout and reads the ids it actually registered, so it holds for any
    /// implementation that keeps the property and fails for one that only looks equivalent.
    /// </para>
    ///
    /// <para>
    /// The regular key's positional id is part of the pin: it must stay distinguishable from the
    /// <c>key:</c> action prefix, or every letter would silently enter the escaping class and lose the
    /// forgiving response.
    /// </para>
    /// </summary>
    [StaFact]
    public void SpecialActionKeys_RegisterUnderTheirSpecialActionId()
    {
        var layout = SpecialActionLayout();
        var probe = Open(layout, 1.0);
        try
        {
            var ids = new HashSet<string>(Centres(probe.Window).Keys);
            var specialActions = layout.SubLayouts.Values
                .SelectMany(sub => sub.Rows.SelectMany(r => r))
                .Where(k => !string.IsNullOrEmpty(k.SpecialAction))
                .Select(k => k.SpecialAction!)
                .ToList();

            // Guard the guard: an empty special set would make the loop below vacuous.
            Check.WithCustomMessage("The synthetic layout registered no special-action keys.")
                .That(specialActions.Count).IsEqualTo(4);

            foreach (var action in specialActions)
                Check.WithCustomMessage(
                        $"Special-action key '{action}' registered as '{string.Join(", ", ids.OrderBy(k => k, StringComparer.Ordinal))}'. "
                        + "The id must BE the SpecialAction: KeyboardSpecialActions.IsAppEscaping classifies on "
                        + "that convention and nothing fails loudly if it breaks.")
                    .That(ids.Contains(action)).IsTrue();

            Check.WithCustomMessage(
                    "The regular key's positional id collided with (or became indistinguishable from) a "
                    + "special-action id — if it starts matching the escaping grammar, the floor lands on "
                    + "every letter.")
                .That(ids.Contains("key_0_0")).IsTrue();
            Check.That(KeyboardSpecialActions.IsAppEscaping("key_0_0")).IsFalse();

            // The classifier must agree with the ids the window just minted (the other side of the
            // coupling: changing the classifier's grammar fails here, changing the minting fails in
            // the loop above). The ids are the REAL CASED layout strings — a lowercase-only
            // classifier would pass a hand-built lowercase set and classify nothing in flight.
            Check.That(KeyboardSpecialActions.IsAppEscaping("enter")).IsTrue();
            Check.That(KeyboardSpecialActions.IsAppEscaping("key:Escape")).IsTrue();
            Check.That(KeyboardSpecialActions.IsAppEscaping("key:Insert")).IsTrue();
            Check.That(KeyboardSpecialActions.IsAppEscaping("shortcut:Ctrl+C")).IsTrue();
            Check.That(KeyboardSpecialActions.IsAppEscaping("key:ArrowUp")).IsFalse(); // navigation stays adaptive
        }
        finally { probe.Dispose(); }
    }

    // ---- the shipped layout's escape keys, which are all in the dead bottom row ----

    [StaFact]
    public void ShippedQwerty_EveryEscapeKeyRoundTrips_AtScale2()
    {
        var probe = Open(ShippedQwerty(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            var letters = new Dictionary<string, PhysicalPoint>(Centres(probe.Window));
            string[] escapes =
            [
                "switchSub:numbers", "switchSub:symbols", "switchSub:function", "switchEmoji",
            ];
            // Guard the guard: if a rename left the escape set empty, the round trips below would
            // measure keys the layout no longer ships (DwellOn would report them missing, but the
            // intent is recorded here).
            Check.WithCustomMessage(
                    $"The shipped qwerty letters layout ships {letters.Keys.Count(k => escapes.Contains(k))} "
                    + $"of its {escapes.Length} escape keys — this guard would pass vacuously. Did the "
                    + "layout's SpecialAction ids change?")
                .That(letters.Keys.Count(k => escapes.Contains(k))).IsEqualTo(escapes.Length);

            long ts = 1_000;

            // Each sub-layout switch is a round trip: out to the target layout, and BACK to
            // letters. One-way "the key responded" cannot see a partial fix that leaves
            // ActiveSubLayoutId pointing at a layout with no way out, because that field (like
            // IsEmojiMode) survives hide/show.
            RoundTrip(probe, "switchSub:numbers", "numbers", ref ts);
            RoundTrip(probe, "switchSub:symbols", "symbols", ref ts);
            RoundTrip(probe, "switchSub:function", "function", ref ts);

            // Emoji swaps the whole surface: IsEmojiMode flips and the emoji manager rebuilds the
            // grid; the way back is its own special_switchText key.
            DwellOn(probe, "switchEmoji", ref ts);
            Check.WithCustomMessage(
                    "Dwelling 'switchEmoji' did not enter emoji mode, so the ABC round trip that "
                    + "follows would assert a state that never changed.")
                .That(probe.Vm.IsEmojiMode).IsTrue();
            DwellOn(probe, "special_switchText", ref ts);
            Check.WithCustomMessage(
                    "The emoji grid's ABC key did not leave emoji mode — emoji mode is a one-way "
                    + "trap, and IsEmojiMode survives hide/show.")
                .That(probe.Vm.IsEmojiMode).IsFalse();
            Check.WithCustomMessage(
                    "Leaving emoji mode did not restore the letters layout.")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
        }
        finally { probe.Dispose(); }
    }

    /// <summary>
    /// Hiding while the emoji grid is up must reopen in the letters layout with the prediction bar
    /// back. This is a WINDOW test, not a VM test on purpose: the VM's reset is correct on its own,
    /// but the window only reacts to property changes, and while emoji mode held the surface
    /// <c>ActiveSubLayoutId</c> already points at the underlying sublayout — so resetting it is a
    /// no-op and nothing else rebuilds the text grid. The VM/View seam hiding a state has produced
    /// three defects in this ticket set (the gate, T1's latch, this); a VM-only assertion cannot
    /// see any of them.
    /// </summary>
    [StaFact]
    public void HideInEmojiMode_ReopensInTheLettersLayout()
    {
        var probe = Open(ShippedQwerty(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            DwellOn(probe, "switchEmoji", ref ts);
            Check.WithCustomMessage(
                    "Dwelling 'switchEmoji' did not enter emoji mode — the rest of this test would "
                    + "assert a transition that never happened.")
                .That(probe.Vm.IsEmojiMode).IsTrue();

            // The production hide/show path: the coordinator sends the messages, the VM's
            // properties change, and the window must mirror them. The VM must first be OPEN —
            // it starts hidden, and a set to the value a property already holds is a no-op, so a
            // bare `IsVisible = false` here would never reach the reset.
            probe.Vm.IsVisible = true;
            probe.Vm.IsVisible = false;
            probe.Vm.IsVisible = true;

            Check.That(probe.Vm.IsEmojiMode).IsFalse();
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");

            // The window's surface must have followed: text keys re-registered, emoji grid gone,
            // prediction bar visible again.
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
            var centres = Centres(probe.Window);
            Check.WithCustomMessage(
                    "The window still shows the emoji surface after a hide in emoji mode: the letters "
                    + "layout's escape key is absent while the emoji grid's ABC key is still a live "
                    + "target. The VM says text mode, so UpdateGaze would run the prediction branch "
                    + "over emoji visuals.")
                .That(centres.ContainsKey("switchSub:numbers")).IsTrue();
            Check.WithCustomMessage("The emoji grid's ABC key is still registered as a dwell target.")
                .That(centres.ContainsKey("special_switchText")).IsFalse();
            Check.WithCustomMessage(
                    "The prediction bar stayed collapsed after leaving emoji mode, so the keyboard "
                    + "reopens missing its only editing affordance.")
                .That(PredictionBarVisibility(probe.Window)).IsEqualTo(Visibility.Visible);
        }
        finally { probe.Dispose(); }
    }

    private static Visibility PredictionBarVisibility(KeyboardWindow w) =>
        ((FrameworkElement)typeof(KeyboardWindow)
            .GetField("PredictionBar", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!).Visibility;

    /// <summary>Out to <paramref name="targetSubLayoutId"/>, then back to letters.</summary>
    private static void RoundTrip(Probe probe, string outKeyId, string targetSubLayoutId, ref long ts)
    {
        DwellOn(probe, outKeyId, ref ts);
        Check.WithCustomMessage(
                $"Dwelling '{outKeyId}' at scale 2.0 did not switch to sub-layout "
                + $"'{targetSubLayoutId}' (still '{probe.Vm.ActiveSubLayoutId}'). The key is in the "
                + "bottom row — the row the mixed-unit gate killed entirely.")
            .That(probe.Vm.ActiveSubLayoutId).IsEqualTo(targetSubLayoutId);

        DwellOn(probe, "switchSub:letters", ref ts);
        Check.WithCustomMessage(
                $"Dwelling 'switchSub:letters' in the '{targetSubLayoutId}' layout did not return "
                + $"to letters (still '{probe.Vm.ActiveSubLayoutId}'). Without the return leg the "
                + "target layout is a one-way trap: ActiveSubLayoutId survives hide/show.")
            .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
    }
}
