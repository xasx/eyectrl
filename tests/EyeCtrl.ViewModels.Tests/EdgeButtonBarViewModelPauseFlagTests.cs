namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// The edge-bar <see cref="EdgeButtonBarViewModel.Receive"/> override must keep the base's
/// <c>LastSampleWasPaused</c> side effect: the flag is what activation handlers read to suppress
/// their OS-input EFFECT while paused (the "gate the effect, not the dwell" rule). No current
/// edge-bar definition sends OS input, so today this is an invariant pin, not a behavior test —
/// the external-review r1 trap was precisely that a future definition would silently lose pause
/// suppression because the override replaced the base's only writer of the flag.
///
/// <para>
/// The base sets the flag for EVERY message (including skipped and in-gap ones), before any
/// gate — the pins below mirror that order, which is what an activation fired by the same
/// sample's detector update observes.
/// </para>
///
/// <para>
/// DpiHelper collection member: the bar's <c>ShowButtons</c> registers dwell targets through
/// DpiHelper-converted geometry (the documented categorical rule).
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class EdgeButtonBarViewModelPauseFlagTests
{
    private static readonly PhysicalPoint Anchor = new(500, 500);

    private static EdgeButtonDefinition Def() => new(
        "pauseProbe", Symbol: string.Empty, DwellMs: 600,
        Activate: static _ => { }, VerticalOffset: 0, RadiusLogical: 50,
        Icon: ActionIcon.Close, Label: "probe");

    private static GazeCursorMovedMessage Sample(bool paused = false, bool inGap = false) =>
        new(new GazeFrame(Anchor, 1_000_000, IsInGap: inGap)) { IsPaused = paused };

    [Fact]
    public void PausedSample_SetsTheFlag_EvenThoughTheSampleIsSkipped()
    {
        var bar = new ProbeBar();
        bar.ShowButtons(new[] { Def() }, Anchor); // visible: the sample is skipped for pause, not invisibility

        bar.Receive(Sample(paused: true));

        Check.That(bar.SampleWasPaused).IsTrue(); // set BEFORE the SampleMayDwell gate — like the base
    }

    [Fact]
    public void InGapPausedSample_StillSetsTheFlag_BeforeTheGapEarlyReturn()
    {
        var bar = new ProbeBar();
        bar.ShowButtons(new[] { Def() }, Anchor);

        bar.Receive(Sample(paused: true, inGap: true));

        Check.That(bar.SampleWasPaused).IsTrue(); // the base set it for in-gap messages too
    }

    [Fact]
    public void UnpausedSample_ClearsAStalePausedFlag()
    {
        var bar = new ProbeBar();
        bar.ShowButtons(new[] { Def() }, Anchor);
        bar.Receive(Sample(paused: true));

        bar.Receive(Sample(paused: false));

        Check.That(bar.SampleWasPaused).IsFalse(); // every sample re-stamps the flag, like the base
    }

    /// <summary>
    /// Reads the protected flag. internal sealed partial — a private nested probe crashes the MVVM
    /// RegisterAll generator (the generated internal extensions cannot reference a private type).
    /// </summary>
    internal sealed partial class ProbeBar : EdgeButtonBarViewModel
    {
        public ProbeBar() : base(new EyeCtrlSettings(), new WeakReferenceMessenger())
        {
        }

        public bool SampleWasPaused => LastSampleWasPaused;
    }
}
