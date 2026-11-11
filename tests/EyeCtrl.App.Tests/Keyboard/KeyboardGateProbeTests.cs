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
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
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

    private static KeyboardLayout LoadShipped(string file) =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", file));

    private static KeyboardLayout ShippedQwerty() => LoadShipped("qwerty.json");

    private static KeyboardLayout ShippedQwertz() => LoadShipped("qwertz.json");

    private static KeyboardLayout ShippedAzerty() => LoadShipped("azerty.json");

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

    /// <summary>The messenger the VM was constructed with: <see cref="ObservableRecipient"/>
    /// keeps <c>Messenger</c> protected, so a test that wants to observe a message must hold
    /// the instance the VM sends on — created here, next to the VM.</summary>
    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, WeakReferenceMessenger Messenger,
        FrameworkElement Content, HwndSource Source) : IDisposable
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
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), messenger);
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

        return new Probe(w, vm, messenger, content, source);
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
    /// round trip passing. <paramref name="holdMs"/> extends the hold past the standard
    /// <see cref="DwellMs"/> for app-escaping keys, whose policy floor
    /// (<see cref="KeyboardSpecialActions.AppEscapingPolicyFloorMs"/>, 600 ms) exceeds the
    /// probe's 500 ms key dwell.
    /// </summary>
    private static void DwellOn(Probe probe, string keyId, ref long ts, int holdMs = DwellMs)
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
        var holdUs = holdMs * 1000L;
        for (var t = latchTs + 50_000; t < latchTs + holdUs; t += 50_000)
            probe.Window.UpdateGaze(centre, t);
        probe.Window.UpdateGaze(centre, latchTs + holdUs);
        ts = latchTs + holdUs + 1_000;
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

    // ---- positive half, on the SHIPPED pages (E1 step 0a) --------------------------------------
    //
    // The synthetic grid above proves the mechanism; this theory proves the mechanism over what
    // actually ships, at the user's effective scale 2.0, where the mixed-unit gate bug lived. Every
    // page of every shipped layout file is probed directly (the layout is opened with the page as
    // its default sub-layout, so no switch dance can mask a page that never renders), and every key
    // on it must respond at its own centre. The cases come straight from the FILES (MemberData), so
    // a new or renamed page is probed by its new name automatically — there is no second list to
    // drift (review 01 F5). The emoji surface is NOT a sub-layout page — it has no JSON page to
    // clone — and its enter/exit is already round-tripped at 2.0 by
    // ShippedQwerty_EveryEscapeKeyRoundTrips_AtScale2 and the accents tests below.
    //
    // The window is never shown: the probe rides the same off-screen HwndSource as every other test
    // in this file (no Window.Show() is ever called).

    public static IEnumerable<object[]> ShippedPages()
    {
        foreach (var file in new[] { "qwerty.json", "azerty.json", "qwertz.json" })
            foreach (var page in LoadShipped(file).SubLayouts.Keys.OrderBy(k => k, StringComparer.Ordinal))
                yield return [file, page];
    }

    /// <summary>The same layout, opened directly on <paramref name="page"/> as its default.</summary>
    private static KeyboardLayout OpenedOnPage(KeyboardLayout layout, string page) => new()
    {
        Name = layout.Name,
        DefaultSubLayout = page,
        SubLayouts = layout.SubLayouts,
    };

    [StaTheory]
    [MemberData(nameof(ShippedPages))]
    public void EveryShippedPageKey_RespondsToGazeAtItsOwnCentre_AtScale2(string file, string page)
    {
        var layout = LoadShipped(file);
        var sub = layout.SubLayouts[page];
        var keyCount = sub.Rows.Sum(r => r.Count);

        var probe = Open(OpenedOnPage(layout, page), 2.0);
        try
        {
            // After the rebuild the positions dictionary is only repopulated by the next sample
            // (the same fixture trap DwellOn documents): one clear sample, one layout pass.
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 1_000);
            probe.Content.UpdateLayout();
            var centres = new Dictionary<string, PhysicalPoint>(Centres(probe.Window));

            // COMPLETENESS (review 01 F5): the window must have registered EVERY key of the page —
            // a key that never registers, or two keys sharing one id (a duplicated SpecialAction),
            // would otherwise go unprobed in silence.
            Check.WithCustomMessage(
                    $"{file}/{page} reported {centres.Count} key centres for {keyCount} keys. A key that " +
                    "never registers is never probed, and a duplicated SpecialAction id would collapse " +
                    "two keys into one target.")
                .That(centres.Count).IsEqualTo(keyCount);

            long ts = 2_000;
            var probed = new List<string>();
            var dead = new List<string>();
            foreach (var kv in centres)
            {
                probed.Add(kv.Key);
                if (LookAt(probe, kv.Value, ref ts) != kv.Key)
                    dead.Add(kv.Key);
            }

            // The probe measured exactly the centres it collected — a `.Skip(1)` or an early
            // `break` in the loop above fails here rather than silently shrinking the probe.
            Check.WithCustomMessage(
                    $"The probe measured {probed.Count} of {centres.Count} keys on {file}/{page}.")
                .That(probed.Count).IsEqualTo(centres.Count);

            Check.WithCustomMessage(
                    $"{file}/{page}: {dead.Count} of {centres.Count} keys did not respond to a gaze sample "
                    + "placed exactly on the key's own reported centre, at effective scale 2.0. Dead keys: "
                    + $"{string.Join(", ", dead)}")
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
                "switchSub:numbers", "switchSub:symbols", "switchSub:function",
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
            // grid; the way back is its own special_switchText key. T6 removed switchEmoji from
            // the letters page, so it is entered from the numbers page (which keeps it); fixup 02
            // put the Fn row's ABC back at main's x, so the last round trip's anchor no longer
            // covers the 123 switch and the dwell below needs no look-away first.
            DwellOn(probe, "switchSub:numbers", ref ts);
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
            // The exit restores the sub-layout the emoji surface opened OVER — the numbers page
            // here, since T6 removed switchEmoji from the letters page.
            Check.WithCustomMessage(
                    "Leaving emoji mode did not restore the layout emoji was entered from.")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("numbers");
            DwellOn(probe, "switchSub:letters", ref ts);
            Check.WithCustomMessage(
                    "The numbers page's ABC key did not return to letters — the round trip ends "
                    + "trapped on numbers.")
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
            // T6 removed switchEmoji from the letters page: enter via the numbers page, then
            // hide — the hide resets to the default layout either way.
            DwellOn(probe, "switchSub:numbers", ref ts);
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

    // ---- the shipped accents sub-layouts, at the owner's effective scale 2.0 ----

    [StaFact]
    public void ShippedQwertz_Accents_AutoReturnRoundTrip_AtScale2()
    {
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;

            // T6 moved the accents switch off the letters page (the footer keeps exactly two
            // plain keys, , and .): it now lives on the #+= footer. Route: letters → symbols →
            // accents.
            DwellOn(probe, "switchSub:symbols", ref ts);
            Check.WithCustomMessage("Dwelling '#+=' did not reach the symbols page.")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("symbols");

            // Manual round trip: àé opens accents; the ABC key returns to letters (the default).
            DwellOn(probe, "switchSub:accents", ref ts);
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");
            DwellOn(probe, "switchSub:letters", ref ts);
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");

            // Auto-return: back via symbols, dwell àé, then dwell é (accents key_0_0) — é types
            // and the keyboard is back on letters.
            DwellOn(probe, "switchSub:symbols", ref ts);
            DwellOn(probe, "switchSub:accents", ref ts);
            DwellOn(probe, "key_0_0", ref ts);
            Check.WithCustomMessage("dwelling an accent did not auto-return to letters")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
            Check.That(probe.Vm.CurrentInput).Contains("é");

            // Emoji entered FROM accents must not auto-return under the open emoji grid (R1):
            // exiting emoji lands back on accents, and dwelling an accent from there returns.
            DwellOn(probe, "switchSub:symbols", ref ts);
            DwellOn(probe, "switchSub:accents", ref ts);
            DwellOn(probe, "switchEmoji", ref ts);
            Check.That(probe.Vm.IsEmojiMode).IsTrue();
            Check.WithCustomMessage("emoji entry fired auto-return under the emoji grid")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");
            DwellOn(probe, "special_switchText", ref ts);
            Check.That(probe.Vm.IsEmojiMode).IsFalse();
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");

            DwellOn(probe, "key_0_0", ref ts);
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_AccEntry_LandsOnTheAccentsOwnSymbolsSwitch()
    {
        // Plan review M2 / plan-r2 I2, restated for T6: pin the landing geometry. The accents
        // switch (àé) now sits on the SYMBOLS footer (letters keeps exactly two plain keys), so
        // the entry route is letters → #+= → àé, and the gaze rests at àé's old spot when the
        // accents page materializes. The accents key whose centre is nearest that resting point
        // must be a NON-TEXT key — specifically the accents footer's own `#+=` switch, which
        // sits almost exactly under àé's old slot — so a resting gaze can never type an
        // accented letter it did not mean. A later layout edit that moves the landing onto a
        // text key shows up here. The swap dwell empties the window's key positions
        // (_keyScreenPositions is rebuilt inside the next gaze pass), so one LAYOUT pass + one
        // GAZE PASS must run before Centres() — in that order (DwellOn's own idiom): the gaze
        // pass that repopulates the registry reads each container's arranged rect, and a gaze
        // pass before the layout pass collects the UNARRANGED rebuilt tree, where every key
        // reports the same collapsed point and the ranking below degenerates to dictionary
        // insertion order (key_0_0 by tie-break, measuring nothing).
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            DwellOn(probe, "switchSub:symbols", ref ts);
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
            var accCentre = Centres(probe.Window)["switchSub:accents"];

            DwellOn(probe, "switchSub:accents", ref ts);

            // Refresh positions before reading them (plan-r2 I2): arrange first, collect second.
            probe.Content.UpdateLayout();
            LookAt(probe, accCentre, ref ts);

            var accentsCentres = Centres(probe.Window);
            var landing = accentsCentres
                .OrderBy(kvp => kvp.Value.DistanceSquaredTo(accCentre))
                .First().Key;
            Check.WithCustomMessage(
                    $"the key under àé's resting gaze is {landing}, expected the accents footer's "
                    + "'#+=' switch (switchSub:symbols) — a text key there would type under a resting gaze")
                .That(landing).IsEqualTo("switchSub:symbols");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_AccEntry_RestingGaze_NeverFiresTheLanding_AtScale2()
    {
        // Impl review I1: the only test that pins D13 against the REAL window's aliasing.
        // The VM's _lastKeyScreenPositions IS the window's live _keyScreenPositions
        // dictionary (an alias, not a snapshot), and assigning ActiveSubLayoutId runs
        // RebuildKeys → ClearKeyState SYNCHRONOUSLY, which EMPTIES it. D13 therefore works
        // only because SwitchSubLayout measures the anchor/radius from the old dict BEFORE
        // the assignment. Every VM test passes a static dictionary nothing clears, so a
        // one-line reorder of those two statements disarms D13 in production with the whole
        // VM suite green (mutation-measured: 17 VM tests + the 14 shipped probes stayed
        // green on the mutant; only THIS probe catches it — resting on the landing spot,
        // the accents `#+=` must never fire; with the anchor disarmed it fires at ~500 ms).
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            DwellOn(probe, "switchSub:symbols", ref ts);
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
            var accCentre = Centres(probe.Window)["switchSub:accents"];

            DwellOn(probe, "switchSub:accents", ref ts);
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");

            // The swap dwell emptied the window's key positions; arrange the accents grid so
            // UpdateGaze's gate and centre collection see the real new geometry (the same
            // layout-pass-then-gaze-pass idiom as the landing pins above).
            probe.Content.UpdateLayout();
            for (var t = ts; t < ts + 3_000_000; t += 50_000)
            {
                probe.Window.UpdateGaze(accCentre, t);
                Check.WithCustomMessage($"resting gaze typed '{probe.Vm.CurrentInput}' / swapped to "
                        + $"'{probe.Vm.ActiveSubLayoutId}' — D13 did not arm in the real window")
                    .That(probe.Vm.CurrentInput).IsEmpty();
                Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");
            }
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedAzerty_AccEntry_LandsOnTheAccentsOwnFunctionSwitch()
    {
        // The AZERTY half of the landing pin (the old row-0-end expectation died with T6's
        // route change: àé — "Acc" — now sits on the #+= footer). Fixup 02's reorder puts Acc
        // further right of the footer, and the accents footer's own `Fn` switch measures nearest
        // to the resting point — a page switch, never a text key. Same refresh dance as the
        // QWERTZ pin.
        var probe = Open(ShippedAzerty(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            DwellOn(probe, "switchSub:symbols", ref ts);
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
            var accCentre = Centres(probe.Window)["switchSub:accents"];

            DwellOn(probe, "switchSub:accents", ref ts);

            probe.Content.UpdateLayout();
            LookAt(probe, accCentre, ref ts);

            var accentsCentres = Centres(probe.Window);
            var landing = accentsCentres
                .OrderBy(kvp => kvp.Value.DistanceSquaredTo(accCentre))
                .First().Key;
            Check.WithCustomMessage(
                    $"the key under Acc's resting gaze is {landing}, expected switchSub:function")
                .That(landing).IsEqualTo("switchSub:function");
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_SwapRepaintsModifierVisuals_CapsStaysArmed()
    {
        // Plan review H1: RebuildKeys draws static labels; the VM's unconditional
        // ActiveModifiers raise must relabel the fresh grid. With Caps on, accents key_0_0
        // must show É (its shiftOutput), and after auto-return the letters Caps key must
        // still paint as armed.
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            probe.Vm.ToggleModifier(ModifierKey.CapsLock);

            // T6: àé lives on the #+= footer now — letters → symbols → accents.
            DwellOn(probe, "switchSub:symbols", ref ts);
            DwellOn(probe, "switchSub:accents", ref ts);

            var accentLabel = KeyLabel(probe, "key_0_0");
            Check.WithCustomMessage($"accents key_0_0 reads '{accentLabel}', expected the capital É")
                .That(accentLabel).IsEqualTo("É");

            DwellOn(probe, "key_0_0", ref ts);   // É types; auto-return to letters
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");

            // Guards the D15-preserved half: a future refactor that clears ALL modifiers
            // (not just the system ones) on swap fails here. The label half above is the
            // stronger discriminator for the M7 repaint itself (plan-r2: this assert cannot
            // fail against the planned ClearSystemModifiers — fixup F6's rename of
            // ClearSystemOneShotModifiers — which never touches CapsLock; kept as a
            // regression net, not as the H1 proof).
            Check.WithCustomMessage("Caps Lock was silently disarmed by the swap")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.CapsLock)).IsTrue();
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_ArmedShift_IsVisibleOnTheAccentsPage_AtScale2()
    {
        // T6 polish 2: an armed Shift stays armed INTO the autoReturn accents page (D15 clears
        // only the SYSTEM modifiers on entry) and is VISIBLE there: the accents keys relabel to
        // their shiftOutput capitals while Shift is held, so the state that will modify every
        // accented letter is on screen, not latent. Dwelling the key then sends the capital and
        // auto-returns with Shift still armed — the accents page is where an invisible Shift
        // would be most misleading, since every key it touches changes.
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            DwellOn(probe, "modifier:shift", ref ts);
            Check.WithCustomMessage("Dwelling the letters Shift key did not arm Shift — the "
                    + "assertions below would not measure an armed state.")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.Shift)).IsTrue();

            // T6's route: àé lives on the #+= footer — letters → symbols → accents.
            DwellOn(probe, "switchSub:symbols", ref ts);
            DwellOn(probe, "switchSub:accents", ref ts);

            Check.WithCustomMessage("D15: entering the autoReturn accents page must keep Shift armed.")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.Shift)).IsTrue();
            Check.WithCustomMessage(
                    $"With Shift armed, accents key_0_0 reads '{KeyLabel(probe, "key_0_0")}', expected "
                    + "its shiftOutput capital É — the armed Shift is invisible on the accents page.")
                .That(KeyLabel(probe, "key_0_0")).IsEqualTo("É");

            DwellOn(probe, "key_0_0", ref ts);   // É types; auto-return to letters
            Check.WithCustomMessage("dwelling an accented key did not auto-return to letters")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
            Check.WithCustomMessage(
                    $"dwelling é under an armed Shift sent '{probe.Vm.CurrentInput}', expected É")
                .That(probe.Vm.CurrentInput).Contains("É");
            // Shift is a one-shot: the key it modified consumed it (it survived the two page
            // switches above — D15 — and cleared on the fire, as on the letters page).
            Check.WithCustomMessage("the accented key fired without consuming the one-shot Shift.")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.Shift)).IsFalse();
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_ArmedAlt_IsVisibleOnTheFunctionPage_AtScale2()
    {
        // T5 (keyboard-one-look): the Fn page carries Win/Alt/Strg, so a system modifier armed
        // on one page is VISIBLE on the page the user dwells onto next. The chain a gaze user
        // performs: dwell Alt on letters (it lights), dwell onto Fn — the Fn grid's Alt key must
        // exist AND be lit while the VM keeps Alt armed (D15) — then dwell F4: the chord must
        // ride Alt+F4 (not a silently disarmed bare F4) and the one-shot must clear afterwards.
        var probe = Open(ShippedQwertz(), 2.0);
        try
        {
            long ts = 1_000;
            SpecialKeyMessage? f4 = null;
            probe.Messenger.Register<SpecialKeyMessage>(probe.Messenger, (_, msg) => f4 = msg);

            DwellOn(probe, "modifier:alt", ref ts);
            Check.WithCustomMessage("Dwelling the letters Alt key did not arm Alt — the assertions "
                    + "below would not measure an armed state.")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.Alt)).IsTrue();
            Check.WithCustomMessage("The letters Alt key is not lit while armed.")
                .That(KeyBrushColor(probe, "modifier:alt")).IsEqualTo(Tokens.SurfaceKeyActive);

            DwellOn(probe, "switchSub:function", ref ts);
            Check.That(probe.Vm.ActiveSubLayoutId).IsEqualTo("function");
            Check.WithCustomMessage("D15: the swap onto Fn must keep the armed Alt.")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.Alt)).IsTrue();

            // The T5 core, on the NEW grid: the armed Alt key exists on the Fn page and is lit;
            // Win/Strg are on the page but unlit (a modifier key's base is the special-key colour).
            Check.WithCustomMessage(
                    $"The Fn page's Alt key brush is not the armed colour while Alt is armed — "
                    + "the armed state is invisible on the page it was kept for (T5).")
                .That(KeyBrushColor(probe, "modifier:alt")).IsEqualTo(Tokens.SurfaceKeyActive);
            Check.WithCustomMessage("The Fn page's Win key is lit although Win is not armed.")
                .That(KeyBrushColor(probe, "modifier:win")).IsEqualTo(Tokens.SurfaceKeyModifier);
            Check.WithCustomMessage("The Fn page's Strg key is lit although Ctrl is not armed.")
                .That(KeyBrushColor(probe, "modifier:ctrl")).IsEqualTo(Tokens.SurfaceKeyModifier);

            DwellOn(probe, "key:F4", ref ts, holdMs: 900);   // app-escaping: 600 ms policy floor
            Check.WithCustomMessage("Dwelling F4 on the Fn page sent no SpecialKeyMessage.")
                .That(f4).IsNotNull();
            Check.That(f4!.KeyName).IsEqualTo("f4");   // the window dispatches key: names lowercased
            Check.WithCustomMessage(
                    "F4 fired without a modifier set — an armed Alt vanished at the chord, or the "
                    + "snapshot never rode the message.")
                .That(f4.Modifiers).IsNotNull();
            Check.That(f4.Modifiers!).Contains(ModifierKey.Alt);
            Check.WithCustomMessage("The one-shot Alt did not clear after F4 fired.")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.Alt)).IsFalse();

            // Fixup 01 (owner decision: a visible Shift key on the Fn page): Fn now carries Shift
            // too. Dwell it ON the Fn page (standard 500 ms hold — modifier keys are not
            // app-escaping) and pin the visual half of the chain the VM test proves logically:
            // the Fn Shift key exists, is dwellable at its own centre, and lights while Shift is
            // armed — an armed Shift is never invisible on the page it rides every key of.
            DwellOn(probe, "modifier:shift", ref ts);
            Check.WithCustomMessage("Dwelling the Fn Shift key did not arm Shift — the assertions "
                    + "below would not measure an armed state.")
                .That(probe.Vm.ActiveModifiers.Contains(ModifierKey.Shift)).IsTrue();
            Check.WithCustomMessage(
                    "The Fn page's Shift key is not lit while Shift is armed — the armed state is "
                    + "invisible on the page whose every key it modifies (fixup 01 F1).")
                .That(KeyBrushColor(probe, "modifier:shift")).IsEqualTo(Tokens.SurfaceKeyActive);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_LayoutReload_RepaintsModifierVisuals_AtScale2()
    {
        // Impl review r2 N1: RebuildKeys draws STATIC labels, and LoadLayout rebuilds
        // unconditionally AFTER the VM's SetLayout raise — the raise's repaint is discarded
        // with the old grid. Without the end-of-rebuild repaint, any settings apply that
        // reloads the keyboard (layout, key size, language or model change, all via
        // LoadKeyboard → LoadLayout) with Caps armed draws the ß key as ß and the Caps key
        // unlit while the keys type capitals — a gaze user trusting the unlit Caps key and
        // dwelling it toggles Caps OFF.
        var probe = Open(ShippedQwertz(), 2.0);
        try
        {
            probe.Vm.ToggleModifier(ModifierKey.CapsLock);
            // T6 moved ß to the letters third row's right end (row index 2): its positional id
            // is key_2_11.
            Check.WithCustomMessage("Caps did not relabel the ß key before the reload — the "
                    + "assertion after it would not measure a repaint.")
                .That(KeyLabel(probe, "key_2_11")).IsEqualTo("ẞ");

            // The production reload seam, with the same argument shape Open used.
            probe.Window.LoadLayout(ShippedQwertz(), 1.0,
                keyDwellMs: DwellMs, predictionDwellMs: 800, cooldownMs: 0);

            // The SHIFT-BEARING label is the discriminator: ß→ẞ happens only in the modifier
            // repaint, never in the static rebuild.
            Check.WithCustomMessage(
                    $"A layout reload with Caps armed redrew the ß key as '{KeyLabel(probe, "key_2_11")}' "
                    + "— RebuildKeys erased the modifier repaint and nothing restored it "
                    + "(impl review r2 N1).")
                .That(KeyLabel(probe, "key_2_11")).IsEqualTo("ẞ");
            Check.WithCustomMessage("The Caps key's brush is unlit after the reload.")
                .That(KeyBrushColor(probe, "toggle:capslock")).IsEqualTo(Tokens.SurfaceKeyActive);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_HideReopen_RepaintsModifierVisuals_AtScale2()
    {
        // Impl review r2 N2 (the M2 hide half, against the real window): hiding resets
        // ActiveSubLayoutId to the default, which rebuilds the letters grid with STATIC
        // labels — the repaint must relight it, or reopening with Caps armed shows ß reading
        // ß and the Caps key unlit while the keys type capitals. Defense in depth: the VM's
        // post-assignment raise and RebuildKeys' own repaint (r2 N1) each cover this path;
        // the pin fails only when both are gone.
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            probe.Vm.ToggleModifier(ModifierKey.CapsLock);

            // T6: àé lives on the #+= footer now — letters → symbols → accents.
            DwellOn(probe, "switchSub:symbols", ref ts);
            DwellOn(probe, "switchSub:accents", ref ts);
            Check.WithCustomMessage("Pre-hide guard: accents key_0_0 does not read É with "
                    + "Caps armed — the post-reopen assert would not measure a repaint.")
                .That(KeyLabel(probe, "key_0_0")).IsEqualTo("É");

            // The production hide/show seam (same as HideInEmojiMode): the VM must be OPEN
            // first — it starts hidden, and a set to the value it already holds is a no-op.
            probe.Vm.IsVisible = true;
            probe.Vm.IsVisible = false;   // reset → letters rebuild under the armed Caps
            probe.Vm.IsVisible = true;    // reopen: no rebuild on this half

            Check.WithCustomMessage(
                    $"Reopening after a hide with Caps armed shows the ß key as '{KeyLabel(probe, "key_2_11")}' "
                    + "— the letters grid the reset rebuilt was never relit (impl review r2 N2, "
                    + "M2 hide half).")
                .That(KeyLabel(probe, "key_2_11")).IsEqualTo("ẞ");
            Check.WithCustomMessage("The Caps key's brush is unlit after the reopen.")
                .That(KeyBrushColor(probe, "toggle:capslock")).IsEqualTo(Tokens.SurfaceKeyActive);
        }
        finally { probe.Dispose(); }
    }

    [StaFact]
    public void ShippedQwertz_EmojiExit_RepaintsModifierVisuals_AtScale2()
    {
        // Impl review r2 N3: the emoji ABC exit rebuilds the text grid with NO ActiveModifiers
        // raise anywhere on its path (special_switchText → IsEmojiMode=false → ExitEmojiMode →
        // RebuildKeys) — only the rebuild's own repaint (r2 N1) relights it. Newly visible
        // from accents: exiting emoji lands back on accents (pinned by the round-trip probe
        // above), whose lower-case labels lie under Caps.
        var probe = Open(ShippedQwertz(), 2.0, emojiJsonPath: ShippedEmojiJson());
        try
        {
            long ts = 1_000;
            probe.Vm.ToggleModifier(ModifierKey.CapsLock);

            // T6: àé lives on the #+= footer now — letters → symbols → accents.
            DwellOn(probe, "switchSub:symbols", ref ts);
            DwellOn(probe, "switchSub:accents", ref ts);
            DwellOn(probe, "switchEmoji", ref ts);
            Check.WithCustomMessage("Dwelling 'switchEmoji' did not enter emoji mode — the "
                    + "exit below would assert a transition that never happened.")
                .That(probe.Vm.IsEmojiMode).IsTrue();

            DwellOn(probe, "special_switchText", ref ts);   // ABC: exit → RebuildKeys
            Check.WithCustomMessage("The ABC key did not leave emoji mode.")
                .That(probe.Vm.IsEmojiMode).IsFalse();
            Check.WithCustomMessage("Leaving emoji did not restore the accents sub-layout.")
                .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("accents");

            Check.WithCustomMessage(
                    $"Exiting emoji with Caps armed shows accents key_0_0 as '{KeyLabel(probe, "key_0_0")}' "
                    + "— the ABC exit rebuilt the grid with static labels and no raise exists "
                    + "on that path (impl review r2 N3).")
                .That(KeyLabel(probe, "key_0_0")).IsEqualTo("É");
        }
        finally { probe.Dispose(); }
    }

    private static Visibility PredictionBarVisibility(KeyboardWindow w) =>
        ((FrameworkElement)typeof(KeyboardWindow)
            .GetField("PredictionBar", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!).Visibility;

    /// <summary>
    /// Reads a key's label through the window's PRIVATE <c>_keyVisuals</c> registry — keys carry
    /// no XAML names, so a <c>FindName</c> route cannot work. The registry's value is
    /// <c>(Border Container, TextBlock? Label)</c>; Item2 is the label the modifier repaint
    /// rewrites, which is exactly what the H1 repaint pin reads.
    /// </summary>
    private static string? KeyLabel(Probe probe, string keyId)
    {
        var field = typeof(KeyboardWindow).GetField("_keyVisuals",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(probe.Window) is not System.Collections.IDictionary visuals
            || visuals[keyId] is not { } entry)
            return null;

        // The registry's value is (Border Container, TextBlock? Label) — a ValueTuple; Item2
        // is the label the modifier repaint rewrites, which is exactly what H1 pins.
        var label = entry.GetType().GetField("Item2")?.GetValue(entry)
            as System.Windows.Controls.TextBlock;
        return label?.Text;
    }

    /// <summary>
    /// Reads a key's surface colour through the window's PRIVATE <c>_keyBrushes</c> registry —
    /// the per-key mutable brush <c>UpdateModifierVisuals</c> recolours on a modifier state
    /// change, which is what the armed-Caps half of the repaint pins reads.
    /// </summary>
    private static Color KeyBrushColor(Probe probe, string keyId)
    {
        var field = typeof(KeyboardWindow).GetField("_keyBrushes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(probe.Window) is not System.Collections.IDictionary brushes
            || brushes[keyId] is not SolidColorBrush brush)
            throw new InvalidOperationException($"Key '{keyId}' has no brush in _keyBrushes.");
        return brush.Color;
    }

    /// <summary>Out to <paramref name="targetSubLayoutId"/>, then back to letters.</summary>
    private static void RoundTrip(Probe probe, string outKeyId, string targetSubLayoutId, ref long ts)
    {
        // Spec D13: the out dwell completes while the gaze rests on the out key, so the swap it
        // triggers arms the spatial swap suppression AT that centre — and the shipped footers
        // lead with same-slot switch keys (letters `Ctrl 123 #+=…`, numbers `Ctrl ABC #+=…`),
        // so the back key 'switchSub:letters' materializes exactly on the anchor. Dwelling
        // straight back is the one pattern D13 suppresses by design: the key under the swap
        // anchor is ledgered-inert until the gaze leaves it. DwellOn's own far-away sample
        // cannot clear that — it lands OUTSIDE the key area, and only an in-window sample
        // beyond the radius clears the suppression. Model the realistic path instead: a user
        // who switches sub-layouts looks at what they switched for, away from the footer,
        // before travelling back. One gaze sample on the target grid's centre farthest from
        // the out key's old centre (always many key widths from the anchor, whatever row it
        // lands in) clears the suppression, and the return leg below dwells normally.
        //
        // The out key's centre is captured from a refreshed registry: a previous round trip's
        // back-swap emptied it (RebuildKeys clears the positions), and it is only repopulated
        // by a gaze pass over an ARRANGED tree — the same refresh idiom DwellOn opens with.
        probe.Content.UpdateLayout();
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var outCentre = Centres(probe.Window)[outKeyId];

        DwellOn(probe, outKeyId, ref ts);
        Check.WithCustomMessage(
                $"Dwelling '{outKeyId}' at scale 2.0 did not switch to sub-layout "
                + $"'{targetSubLayoutId}' (still '{probe.Vm.ActiveSubLayoutId}'). The key is in the "
                + "bottom row — the row the mixed-unit gate killed entirely.")
            .That(probe.Vm.ActiveSubLayoutId).IsEqualTo(targetSubLayoutId);

        // The gaze-away leg: arrange the new grid, refresh its centres with one gaze pass
        // (the swap emptied the registry — same idiom as DwellOn's opening), look away, and
        // guard that the sample really latched: if the suppression were still armed, the back
        // dwell below would fail with a message blaming the back key for what is really a
        // fixture-pattern problem.
        probe.Content.UpdateLayout();
        probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var away = Centres(probe.Window)
            .OrderByDescending(kvp => kvp.Value.DistanceSquaredTo(outCentre))
            .First().Value;
        var latchedAway = LookAt(probe, away, ref ts);
        Check.WithCustomMessage(
                "The gaze-away sample between the out and back dwells did not latch a key — "
                + "the D13 suppression would still be armed and the back dwell below would "
                + "fail for the wrong reason.")
            .That(latchedAway).IsNotNull();

        DwellOn(probe, "switchSub:letters", ref ts);
        Check.WithCustomMessage(
                $"Dwelling 'switchSub:letters' in the '{targetSubLayoutId}' layout did not return "
                + $"to letters (still '{probe.Vm.ActiveSubLayoutId}'). Without the return leg the "
                + "target layout is a one-way trap: ActiveSubLayoutId survives hide/show.")
            .That(probe.Vm.ActiveSubLayoutId).IsEqualTo("letters");
    }
}
