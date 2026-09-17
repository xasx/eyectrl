namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.IO;
using NFluent;

/// <summary>
/// Source-scan pin: the keyboard's key-grid gate must build its physical-pixel rectangle from
/// BOTH corners through <c>UIElement.PointToScreen</c>, never from a DIP extent added to a
/// physical origin.
///
/// <para>
/// The defect this pins away: <c>IsGazeInsideKeyArea</c> took one <c>PointToScreen</c> corner and
/// added <c>KeyRows.ActualWidth</c>/<c>ActualHeight</c> — DIP extents — to it. At the user's 200%
/// scale the accepted rectangle was the key grid's top-left quadrant, so 30 of the 40 shipped
/// qwerty keys never accumulated dwell (every sub-layout switch and the emoji switch included, all
/// of them in the bottom row). The behavioural proof lives in
/// <see cref="KeyboardGateProbeTests"/>; this pin is the cheap spelling guard that makes the next
/// one-corner-plus-extent expression fail loudly before it reaches a user who cannot bisect it.
/// </para>
///
/// <para>
/// Why the pins are shaped this way. Counting two <c>PointToScreen(</c> calls was the first
/// attempt, on the reasoning that with two physical corners there is no extent left to add —
/// and that reasoning is wrong, measured: a rect rebuilt as
/// <c>new PhysicalRect(topLeft.X, topLeft.Y, ActualWidth, ActualHeight)</c> beside the declared
/// (unused) second corner keeps the count at two and stayed green. The argument pin closes it by
/// reading the <c>PhysicalRect</c> call's own arguments and rejecting the element extents there —
/// the one place the mix can re-enter without disturbing the corner derivation. It is
/// deliberately not a pin on one spelling of the subtraction: honest extraction of width and
/// height into locals leaves the arguments free of extents and stays green. DpiHelper is
/// separately banned because it does not drive <c>PointToScreen</c> (the measured
/// <c>ActualWidth * DpiHelper.ScaleX</c> variant stayed at 10/30 live keys at scale 2.0).
/// </para>
///
/// <para>
/// Modelled on <c>TaskSwitcherDwellPositionTests</c>' source pins, and like them it pins
/// spelling, not behaviour: it is a guard against re-introduction, not evidence the gate works.
/// <see cref="KeyboardGateProbeTests"/> remains the evidence.
/// </para>
/// </summary>
public class KeyboardGateCensusTests
{
    [Fact]
    public void KeyGridScreenRect_UsesBothPointToScreenCorners_AndPhysicalRectContains()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "KeyboardWindow.xaml.cs"));
        var body = ExtractMethod(source, "private bool TryGetKeyRowsScreenRect(out PhysicalRect rect)");

        Check.WithCustomMessage(
                "The key-grid gate must derive BOTH corners through PointToScreen. One corner plus "
                + "an ActualWidth/ActualHeight extent is the mixed-unit rectangle that left the "
                + "grid's top-left quadrant accepted at 200% scale (30 of 40 keys dead). The DIP "
                + "extents belong only as arguments to the second PointToScreen call.")
            .That(CountOccurrences(body, "PointToScreen(")).IsEqualTo(2);
        Check.WithCustomMessage(
                "The gate must return a PhysicalRect — the typed return is the contract that the "
                + "rectangle is in physical pixels, the same contract OverlayPositioning's "
                + "TryGetScreenCenter carries.")
            .That(body.Contains("new PhysicalRect(")).IsTrue();
        var rectArguments = ExtractCallArguments(body, "new PhysicalRect(");
        Check.WithCustomMessage(
                "The PhysicalRect must be built from the two corner POINTS, never from "
                + "ActualWidth/ActualHeight. Two PointToScreen calls plus a rect built from the "
                + "element extents is the DIP-extents revert with the second corner left declared "
                + "(the count check alone stayed green on exactly that shape), so the extents must "
                + "be kept out of the rect construction itself.")
            .That(rectArguments.Contains("ActualWidth") || rectArguments.Contains("ActualHeight"))
            .IsFalse();
        Check.WithCustomMessage(
                "The gate must not touch DpiHelper: it does not drive PointToScreen, and the "
                + "measured ActualWidth * DpiHelper.ScaleX variant was indistinguishable from the "
                + "bug at scale 2.0 (10/30 keys). PointToScreen composes the visual tree's own "
                + "transform; nothing manual belongs on top of its result.")
            .That(body.Contains("DpiHelper")).IsFalse();

        var updateGaze = ExtractMethod(source, "public void UpdateGaze(");
        Check.WithCustomMessage(
                "The gate result must be consumed through PhysicalRect.Contains — comparing raw "
                + "bounds arithmetic at the call site is how the DIP/physical mix gets back in.")
            .That(updateGaze.Contains(".Contains(screenPoint.X, screenPoint.Y)")).IsTrue();
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.That(end).IsStrictlyGreaterThan(-1);
        return source[start..end];
    }

    /// <summary>
    /// The text between the first <paramref name="call"/>'s opening parenthesis and its matching
    /// close, so a pin can inspect a call's arguments without pinning the surrounding statement.
    /// </summary>
    private static string ExtractCallArguments(string text, string call)
    {
        var open = text.IndexOf(call, StringComparison.Ordinal);
        Check.That(open).IsStrictlyGreaterThan(-1);
        var start = open + call.Length;
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text[start..i];
        }
        throw new InvalidOperationException($"Unbalanced parentheses after '{call}'.");
    }
}
