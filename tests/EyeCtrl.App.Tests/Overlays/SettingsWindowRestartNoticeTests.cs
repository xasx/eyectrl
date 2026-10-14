namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The Settings window registers on the app-lifetime messenger for the two restart notices (holistic
/// review F5). The messenger holds a recipient weakly but until it is collected, so a closed window
/// kept receiving them and showed a modal on a window nobody can see. It now unregisters in
/// <c>Closed</c>, and the App-level <c>RestartNoticeHost</c> shows the notices instead.
/// </summary>
[Collection("WpfApplication")]
public class SettingsWindowRestartNoticeTests
{
    [StaFact]
    public void AClosedSettingsWindow_NoLongerReceivesTheRestartNotices()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var cleanups = new List<Action>();
        try
        {
            var messenger = new WeakReferenceMessenger();
            var window = OverlayClientOriginTests.BuildSettingsWindow(cleanups, messenger);

            Check.WithCustomMessage("an open Settings window must receive the in-progress notice")
                .That(messenger.IsRegistered<RestartInProgressMessage>(window)).IsTrue();
            Check.WithCustomMessage("an open Settings window must receive the refusal notice")
                .That(messenger.IsRegistered<RestartUnavailableMessage>(window)).IsTrue();

            window.Close();

            Check.WithCustomMessage("a closed Settings window still receives RestartInProgressMessage")
                .That(messenger.IsRegistered<RestartInProgressMessage>(window)).IsFalse();
            Check.WithCustomMessage("a closed Settings window still receives RestartUnavailableMessage")
                .That(messenger.IsRegistered<RestartUnavailableMessage>(window)).IsFalse();

            // And a notice sent now reaches nobody, without throwing.
            messenger.Send(new RestartInProgressMessage());
            messenger.Send(new RestartUnavailableMessage("anything"));
        }
        finally
        {
            foreach (var cleanup in cleanups)
            {
                try { cleanup(); } catch { /* best effort */ }
            }
        }
    }
}
