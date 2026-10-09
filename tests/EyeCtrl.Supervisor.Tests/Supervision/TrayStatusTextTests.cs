namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Globalization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// T06 fixup-01 F2: the tooltip line composes on a state change and re-resolves the table only at
/// its display moments. A same-state update — every heartbeat — must do no settings read at all.
/// The English pins use literals on the expected side by design.
/// </summary>
public class TrayStatusTextTests
{
    /// <summary>A two-line fixture whose prefix is the marker: swapping the table proves the
    /// refresh reads the NEW one, and the state phrases stay the real English.</summary>
    private static StringTable Fixture(string prefix) =>
        StringTable.Create(
            UiLanguage.English,
            CultureInfo.InvariantCulture,
            StringTableContent.ParseEnglish(
                $$"""
                {
                  "supervisor.tray.prefix": { "text": "{{prefix}}", "who": "caretaker", "where": "supervisor", "fit": "tray63", "args": [] },
                  "supervisor.state.idle": { "text": "idle", "who": "caretaker", "where": "supervisor", "fit": "tray63", "args": [] },
                  "supervisor.state.watching": { "text": "watching EyeCtrl", "who": "caretaker", "where": "supervisor", "fit": "tray63", "args": [] }
                }
                """));

    [Fact]
    public void Update_ComposesTheEnglishLine()
    {
        var status = new TrayStatusText(() => StringTable.EmbeddedEnglish);

        Check.That(status.Update(SupervisorState.IdleWatch)).IsEqualTo("EyeCtrl Supervisor: idle");
        Check.That(status.Update(SupervisorState.Supervising)).IsEqualTo("EyeCtrl Supervisor: watching EyeCtrl");
    }

    [Fact]
    public void ABurstOfSameStateUpdates_ResolvesTheTableOnce()
    {
        // 1200 heartbeats are an hour of Supervising (HeartbeatPublisher: one per ~3 s).
        var calls = 0;
        var status = new TrayStatusText(() => { calls++; return StringTable.EmbeddedEnglish; });

        for (var i = 0; i < 1200; i++)
            status.Update(SupervisorState.Supervising);

        Check.That(calls).IsEqualTo(1);
    }

    [Fact]
    public void ANewState_ResolvesAgain()
    {
        var calls = 0;
        var status = new TrayStatusText(() => { calls++; return StringTable.EmbeddedEnglish; });

        status.Update(SupervisorState.IdleWatch);
        status.Update(SupervisorState.IdleWatch);
        Check.That(calls).IsEqualTo(1);

        status.Update(SupervisorState.Supervising);
        Check.That(calls).IsEqualTo(2);
    }

    [Fact]
    public void Refresh_ShowsTheNewlyResolvedTable_WithoutAStateChange()
    {
        // The App saved a language change while the state stays Supervising: the next hover (or
        // menu open) must show the new language.
        var table = Fixture("Old: ");
        var status = new TrayStatusText(() => table);
        Check.That(status.Update(SupervisorState.IdleWatch)).IsEqualTo("Old: idle");

        table = Fixture("New: ");
        Check.That(status.Refresh()).IsEqualTo("New: idle");

        // The refreshed line is the cached one from now on, still without another resolve.
        table = Fixture("Unused: ");
        Check.That(status.Update(SupervisorState.IdleWatch)).IsEqualTo("New: idle");
    }

    [Fact]
    public void Refresh_BeforeAnyUpdate_ReturnsNull()
    {
        // The icon still shows its product-name default before the first state render; a hover
        // must not invent a state line.
        var status = new TrayStatusText(() => StringTable.EmbeddedEnglish);

        Check.That(status.Refresh()).IsNull();
    }
}
