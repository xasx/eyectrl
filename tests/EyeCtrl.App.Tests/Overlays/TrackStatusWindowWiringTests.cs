namespace EyeCtrl.App.Tests.Overlays;

using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using NFluent;
using Xunit;

/// <summary>
/// Pins the real <see cref="TrackStatusWindow"/> handler to its two maps: the shown text must be
/// <see cref="TrackStatusWindow.QualityWord"/> and the foreground must be
/// <see cref="TrackStatusWindow.QualityBrush"/>. The map tests call those functions directly, so
/// without this a handler that bypasses either one passes the whole suite — and T06, which keys
/// the word, could leave the screen quietly English or every quality grey.
///
/// Constructs the window headlessly: no <c>Show()</c>, no input, and the WPF
/// <see cref="Application"/> is the shared token-resource singleton (<see cref="WpfApplicationGate"/>),
/// hence the shared "WpfApplication" collection.
/// </summary>
[Collection("WpfApplication")]
public class TrackStatusWindowWiringTests
{
    [StaFact]
    public void QualityChanges_ShowQualityWordAndQualityBrush()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var provider = new FakeTrackStatusProvider
        {
            Caps = GazeProviderCapabilities.EyePosition | GazeProviderCapabilities.HeadPose
        };
        using var vm = new TrackStatusViewModel(provider);
        var window = new TrackStatusWindow();
        try
        {
            window.Configure(vm);

            var qualityText = window.FindName("QualityText") as TextBlock;
            Check.WithCustomMessage("TrackStatusWindow no longer has a TextBlock named QualityText.")
                .That(qualityText).IsNotNull();

            var distanceText = window.FindName("DistanceText") as TextBlock;
            Check.WithCustomMessage("TrackStatusWindow no longer has a TextBlock named DistanceText.")
                .That(distanceText).IsNotNull();

            // T06 fixup-01 F1: the static labels' keyed English, pinned literally.
            var titleLabel = window.FindName("TitleLabel") as TextBlock;
            var distanceLabel = window.FindName("DistanceLabel") as TextBlock;
            var qualityLabel = window.FindName("QualityLabel") as TextBlock;
            Check.That(titleLabel!.Text).IsEqualTo("Track Status");
            Check.That(distanceLabel!.Text).IsEqualTo("Distance: ");
            Check.That(qualityLabel!.Text).IsEqualTo("Quality: ");

            void Poll(double distanceMm, bool valid, bool hasData = true)
            {
                provider.HasData = hasData;
                provider.Sample = new TrackStatusSample(
                    Left: new EyeSample(0.3, 0.4, 0.5, valid),
                    Right: new EyeSample(0.7, 0.4, 0.5, valid),
                    HeadDistanceMm: distanceMm, HeadDistanceValid: valid,
                    UserPresent: true, TimestampMicroseconds: 1000);
                vm.PollLatest();
            }

            void AssertShown(DistanceQuality expected)
            {
                Check.WithCustomMessage($"The view model did not reach {expected}.")
                    .That(vm.DistanceQuality).IsEqualTo(expected);
                Check.WithCustomMessage($"QualityText does not show QualityWord({expected}).")
                    .That(qualityText!.Text).IsEqualTo(TrackStatusWindow.QualityWord(StringTable.EmbeddedEnglish, expected));
                Check.WithCustomMessage($"QualityText.Foreground is not QualityBrush({expected}).")
                    .That(qualityText!.Foreground).IsSameReferenceAs(TrackStatusWindow.QualityBrush(expected));
            }

            Poll(600, true);
            AssertShown(DistanceQuality.Good);
            // The number and unit are one keyed template (T06): English literal on the expected
            // side, so a wrong key or a changed format specifier cannot pass.
            Check.WithCustomMessage("DistanceText does not show the keyed \"600 mm\" template.")
                .That(distanceText!.Text).IsEqualTo("600 mm");

            Poll(380, true);
            AssertShown(DistanceQuality.Fair);

            Poll(300, true);
            AssertShown(DistanceQuality.Poor);

            // The tracker drops out (e.g. disconnect): the reset path must reach the window too.
            Poll(0, false, hasData: false);
            AssertShown(DistanceQuality.Unknown);

            Poll(600, true);
            AssertShown(DistanceQuality.Good);

            // An invalid distance reading while the tracker still reports.
            Poll(600, false);
            AssertShown(DistanceQuality.Unknown);

            // The four enum names coincide with the English words today, so a handler that set
            // Text from DistanceQuality.ToString() would satisfy every assert above — and stay
            // silent until T06 keys the word. An out-of-range value reaches the fallback arm,
            // where the two differ ("42" versus "Unknown"), so this probe fails unless the
            // handler really asks QualityWord (and QualityBrush, whose fallback it also pins).
            vm.DistanceQuality = (DistanceQuality)42;
            AssertShown((DistanceQuality)42);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class FakeTrackStatusProvider : IGazeProvider
    {
        public GazeProviderCapabilities Caps { get; set; } = GazeProviderCapabilities.None;
        public TrackStatusSample Sample { get; set; }
        public bool HasData { get; set; } = true;
        public GazeProviderCapabilities Capabilities => Caps;
        public bool IsConnected => true;
#pragma warning disable CS0067
        public event Action<GazePoint>? GazePointReceived;
#pragma warning restore CS0067
        public bool TryGetLatestStatus(out TrackStatusSample sample)
        {
            sample = Sample;
            return HasData && Caps != GazeProviderCapabilities.None;
        }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
