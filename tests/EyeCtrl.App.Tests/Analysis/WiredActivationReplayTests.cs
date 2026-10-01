namespace EyeCtrl.App.Tests.Analysis;

using System.Globalization;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 8 fixup's proof for review B F1: the frozen recording replayed through the REAL
/// <see cref="GazeEngine"/> (its own filter and pipeline) with the REAL
/// <see cref="ToolbarAutoHideController"/> driving the toolbar zone, exactly as the product wires
/// them. Prints the 750 ms row at R90 and R75 for comparison with the ticket 7 replay report and with
/// review B's auto-hide measurement (R90: 68.8 % on screen, 34.0 pop-ups/min, G3′ 2).
///
/// <para>
/// Env-gated on <c>EYECTRL_GAZE_RECORDING</c> (the frozen copy), like the ticket 7 replay tests.
/// Set <c>EYECTRL_WIRED_REPLAY_OUT</c> to also write the numbers to a file. The recording is parsed
/// locally because the shared reader is a Core.Tests helper and this probe needs the App-side
/// controller.
/// </para>
/// </summary>
public class WiredActivationReplayTests
{
    private const string RecordingEnvironmentVariable = "EYECTRL_GAZE_RECORDING";
    private const string ReportEnvironmentVariable = "EYECTRL_WIRED_REPLAY_OUT";
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalRect ToolbarRect = new(695, 0, 2450, 288);

    private sealed record RunResult(double OnScreenShare, double PopUpsPerMinute, int Completions, double Minutes)
    {
        public string Describe(string label) =>
            $"{label}: on-screen {OnScreenShare * 100:0.0} %, pop-ups {PopUpsPerMinute:0.0}/min, G3' {Completions} ({Minutes:0.00} min)";
    }

    [Fact]
    public void WiredReplay_AutoHideToolbar_ReportsThe750MsRows()
    {
        var path = Environment.GetEnvironmentVariable(RecordingEnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(path) || !File.Exists(path),
            "No gaze recording. Set " + RecordingEnvironmentVariable + " to the frozen snapshot copy.");

        var samples = ReadSamples(path!);
        Check.That(samples).Not.IsEmpty();

        var r90 = Run(samples, fixationRadiusPx: 90);
        var r75 = Run(samples, fixationRadiusPx: 75);

        var report = string.Join(Environment.NewLine,
            "Wired replay (real GazeEngine + real auto-hide toolbar, linger 750 ms, ring 250):",
            r90.Describe("R90"),
            r75.Describe("R75"));
        var outPath = Environment.GetEnvironmentVariable(ReportEnvironmentVariable);
        if (!string.IsNullOrEmpty(outPath))
            File.WriteAllText(outPath, report);

        // A sanity floor, not a golden number: the range read-out lives in the report above.
        Check.That(r90.PopUpsPerMinute).IsStrictlyGreaterThan(0);
    }

    private static RunResult Run(IReadOnlyList<GazePoint> samples, double fixationRadiusPx)
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.FixationRadiusPixels = fixationRadiusPx;
        var provider = new TestFakes.FakeGazeProvider();
        using var engine = new GazeEngine(provider, new TestFakes.FakeInputSimulator(), settings);
        engine.ConfigureActivationOffers(Screen, scale: 2.0);
        var im = engine.InteractionManager;

        var visibility = new RecordingToolbarVisibility();
        var autoHide = new ToolbarAutoHideController(
            enabled: true, edge: "Top", screenW: Screen.Width, screenH: Screen.Height,
            toolbarRect: ToolbarRect, hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        autoHide.Attach(visibility);

        int popUps = 0;
        int completions = 0;
        long? pendingAccept = null;
        bool live = false;
        long onScreenUs = 0;
        long? previousTs = null;
        long? firstTs = null;
        long lastTs = 0;
        im.ActivationOfferShown += _ => { popUps++; live = true; };
        im.ActivationOfferClosed += () => live = false;
        im.ActivationOfferFired += (offer, _) => { completions++; pendingAccept = offer.Id; };

        // The startup publish: the toolbar is visible before the first sample.
        bool? lastVisible = null;
        if (lastVisible != visibility.Visible)
        {
            lastVisible = visibility.Visible;
            im.SetActivationChromeZones(ToolbarRect);
        }

        engine.Start();
        foreach (var sample in samples)
        {
            long ts = sample.TimestampMicroseconds;
            if (previousTs is { } previous && live)
                onScreenUs += ts - previous;
            previousTs = ts;
            firstTs ??= ts;
            lastTs = ts;

            // The real auto-hide decision for this sample; a zone publish only on its transitions,
            // as the coordinator does. Core re-validates the live offer on the publish.
            autoHide.Feed(sample, ts);
            if (lastVisible != visibility.Visible)
            {
                lastVisible = visibility.Visible;
                im.SetActivationChromeZones(
                    visibility.Visible ? new[] { ToolbarRect } : Array.Empty<PhysicalRect>());
            }

            provider.Raise(sample);

            if (pendingAccept is { } id)
            {
                im.RequestActivationOfferAccept(id);
                pendingAccept = null;
            }
        }

        double duration = (lastTs - (firstTs ?? 0)) / 60_000_000.0;
        return new RunResult(
            duration <= 0 ? 0 : onScreenUs / (double)(lastTs - (firstTs ?? 0)),
            duration <= 0 ? 0 : popUps / duration,
            completions,
            duration);
    }

    /// <summary>Parses the recorder's format locally: <c>#</c> header/trailer lines, then
    /// <c>x,y,timestampUs,isValid</c>.</summary>
    private static List<GazePoint> ReadSamples(string path)
    {
        var samples = new List<GazePoint>(20_000);
        foreach (var line in File.ReadLines(path))
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#')
                continue;
            var parts = span.ToString().Split(',', 4);
            if (parts.Length < 4)
                continue;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
            {
                samples.Add(new GazePoint(x, y, t, parts[3].Trim() == "1"));
            }
        }
        return samples;
    }

    /// <summary>The controller's output seam, recording the visibility the zones are published from.</summary>
    private sealed class RecordingToolbarVisibility : IToolbarVisibility
    {
        public bool Visible { get; private set; } = true;

        public void ShowToolbar() => Visible = true;
        public void HideToolbar(bool paused) => Visible = false;
        public void ForceShow() => Visible = true;
        public void SetPauseIndicator(bool visible) { }
        public void PositionIndicator(string edge) { }
        public void SetActive(bool active) { }
    }
}
