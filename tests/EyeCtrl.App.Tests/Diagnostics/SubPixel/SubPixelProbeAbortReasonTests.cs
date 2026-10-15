namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;
using Xunit;

/// <summary>
/// N3 (fixup 03): every C7-initiated abort records its own <c>abortReason</c>, separately from C5's
/// <c>runEndReason</c>. One row per trigger; the file is the oracle.
/// </summary>
public class SubPixelProbeAbortReasonTests
{
    [Theory]
    [InlineData("show-deadline", "showDeadline")]
    [InlineData("session-cap", "sessionCap")]
    [InlineData("no-progress", "noProgress")]
    [InlineData("no-valid-gaze", "noValidGaze")]
    [InlineData("escape", "escape")]
    [InlineData("settings-requested", "settingsRequested")]
    [InlineData("force-show-toolbar", "forceShowToolbar")]
    [InlineData("paused", "paused")]
    [InlineData("start-step-failed", "startStepFailed")]
    [InlineData("show-failed", "showFailed")]
    [InlineData("menu-opened", "menuOpened")]
    [InlineData("window-closed", "windowClosed")]
    [InlineData("disposed", "disposed")]
    [InlineData("view-fault", "viewFault")]
    public void EveryC7InitiatedAbort_RecordsItsReason(string trigger, string expected)
    {
        using var world = new ProbeTestWorld(holdKeyboardClaim: true);

        if (trigger == "start-step-failed") world.Host.ThrowOnceAt = "HideRing";
        if (trigger == "show-failed") world.Host.ThrowOnceAt = "AssertToolbarInactive";
        if (trigger == "menu-opened")
        {
            world.Host.ReenterOnceAt("AssertToolbarInactive", () => world.Host.MenusVisible = true);
        }

        world.Coordinator.StartForTest();
        if (trigger != "show-deadline") world.Pump();

        switch (trigger)
        {
            case "show-deadline":
                world.Now += 5_001;
                world.Coordinator.Check(world.Coordinator.LiveSessionForTest!);
                break;
            case "session-cap":
                world.Now += 15 * 60_000 + 1;
                world.Coordinator.Check(world.Coordinator.LiveSessionForTest!);
                break;
            case "no-progress":
                world.Now += 90_001;
                world.Coordinator.Check(world.Coordinator.LiveSessionForTest!);
                break;
            case "no-valid-gaze":
                world.Now += 12_001;
                world.Coordinator.Check(world.Coordinator.LiveSessionForTest!);
                break;
            case "escape":
                ((TestFakes.FakeEscapeHatch)world.Context.EscapeHatch).Press();
                world.Pump();
                break;
            case "settings-requested":
                world.Messenger.Send(new SettingsRequestedMessage());
                break;
            case "force-show-toolbar":
                world.Coordinator.InterlockForceShowToolbar(() => { });
                break;
            case "paused":
                world.Messenger.Send(new StateChangedMessage(InteractionState.Paused));
                break;
            case "start-step-failed":
            case "show-failed":
            case "menu-opened":
                // The throw / menu re-check already aborted during the pumped start.
                break;
            case "window-closed":
                world.Host.Windows[0].FireClosed();
                break;
            case "view-fault":
                world.Host.Windows[0].FireViewFault();

                // F4: the fault must be POSTED, never restored inline. Inline, Restore would close
                // the window from inside a CompositionTarget.Rendering callback — the path fixup 05
                // F1 showed does not tolerate surprises. No Pump yet: nothing may have happened.
                Check.WithCustomMessage(
                        "The view fault restored inline; Restore ran inside the render callback")
                    .That(world.Modal.ProbeOpen).IsTrue();
                Check.WithCustomMessage("The window was closed inline by the view fault")
                    .That(world.Host.CountOf("WindowClosed")).IsEqualTo(0);
                Check.That(world.Coordinator.LiveSessionForTest).IsNotNull();

                world.Pump();
                Check.WithCustomMessage("The posted view-fault Restore never ran")
                    .That(world.Modal.ProbeOpen).IsFalse();
                break;
            case "disposed":
                world.Coordinator.Dispose();
                break;
        }

        var resultFiles = Directory.EnumerateFiles(world.ResultsDir, "subpixel-ab-*.json").ToList();
        Check.WithCustomMessage($"The {trigger} abort wrote no result file").That(resultFiles).HasSize(1);
        using var document = JsonDocument.Parse(File.ReadAllText(resultFiles[0]));
        Check.WithCustomMessage($"The {trigger} abort did not record its own reason")
            .That(document.RootElement.GetProperty("abortReason").GetString()).IsEqualTo(expected);
        Check.WithCustomMessage(
                "A C7 abort must not masquerade as the owner's End test: runEndReason stays absent")
            .That(document.RootElement.TryGetProperty("runEndReason", out _)).IsFalse();
    }
}
