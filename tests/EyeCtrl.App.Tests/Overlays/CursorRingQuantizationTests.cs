namespace EyeCtrl.App.Tests.Overlays;

using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 7 (review A F3d / W1): instrument 2 must skip glide frames. The VM half is pinned in
/// <c>OverlayVmBindingCoordinatorTests</c>; this is the window half —
/// <see cref="CursorCircleWindow.MoveTo(PhysicalPoint, bool)"/> writes the position like
/// <c>UpdatePosition</c> but must not feed the probe.
///
/// <para>
/// <c>RecordQuantization</c> returns early when the window is unsourced, so the test gives it a real
/// (never shown) HWND with <see cref="WindowInteropHelper.EnsureHandle"/>: the read-back through
/// <c>GetWindowRect</c> then succeeds and the probe's <c>Count</c> is observable. The window is not
/// shown and the cursor is never touched, so this is safe for a gaze-only user.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class CursorRingQuantizationTests
{
    [StaFact]
    public void Instrument2_RecordsPositionWrites_ButSkipsGlideFrames()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new CursorCircleWindow();
        try
        {
            _ = new WindowInteropHelper(window).EnsureHandle();   // a real HWND, never shown
            var probe = new RingQuantizationProbe();
            window.AttachQuantizationProbe(probe, new DiagnosticsSettings { MeasureUiInstruments = true });
            window.UpdateAppearance(new CursorSettings { CircleSize = 250 });

            window.UpdatePosition(new PhysicalPoint(1000, 500));
            Check.That(probe.Count).IsEqualTo(1);

            // Mutation: drop the `!isGlideFrame &&` in ApplyPosition → this glide frame records and the
            // count becomes 2, red.
            window.MoveTo(new PhysicalPoint(1100, 520), glide: true);
            Check.That(probe.Count).IsEqualTo(1);

            // A non-glide MoveTo is an ordinary write and does record.
            window.MoveTo(new PhysicalPoint(1200, 540), glide: false);
            Check.That(probe.Count).IsEqualTo(2);
        }
        finally
        {
            window.Close();
        }
    }
}
