namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Core.Localization;
using EyeCtrl.Supervisor.Supervision;

public class SupervisorStateTextTests
{
    private static readonly SupervisorState[] States = Enum.GetValues<SupervisorState>();
    private static readonly StringTable Table = StringTable.EmbeddedEnglish;

    [Fact]
    public void EveryState_MapsToADistinctNonEmptyPhrase()
    {
        var phrases = States.Select(state => SupervisorStateText.Describe(Table, state)).ToList();

        Check.That(States).HasSize(6);
        Check.That(phrases).ContainsOnlyElementsThatMatch(p => !string.IsNullOrWhiteSpace(p));
        Check.That(phrases.Distinct().Count()).IsEqualTo(States.Length);
    }

    /// <summary>The English is written out per state (T06): resolving it with <c>T(key)</c> on
    /// both sides would let a wrong key pass as its own raw text.</summary>
    [Theory]
    [InlineData(SupervisorState.IdleWatch, "idle")]
    [InlineData(SupervisorState.Launching, "starting EyeCtrl")]
    [InlineData(SupervisorState.Supervising, "watching EyeCtrl")]
    [InlineData(SupervisorState.Recovering, "restarting EyeCtrl")]
    [InlineData(SupervisorState.CleanExitPending, "EyeCtrl is closing")]
    [InlineData(SupervisorState.FallenBack, "EyeCtrl stopped \u2014 backup program running")]
    public void EveryState_ResolvesItsEnglishPhrase(SupervisorState state, string expected)
    {
        Check.That(SupervisorStateText.Describe(Table, state)).IsEqualTo(expected);
    }

    [Fact]
    public void TheTrayPrefix_ResolvesTheEnglishPrefix()
    {
        Check.That(SupervisorStateText.TrayPrefix(Table)).IsEqualTo("EyeCtrl Supervisor: ");
    }

    [Fact]
    public void EveryStatesTrayText_FitsThe63CharShellCap()
    {
        // The shell truncates NotifyIcon.Text past 63 chars (SupervisorTrayIcon.SetStatusText), so the
        // prefix counts toward the budget the phrases are written against. Both are read from the
        // table now (T06), so the budget is checked against what the tray would actually show.
        foreach (var state in States)
        {
            var text = SupervisorStateText.TrayPrefix(Table) + SupervisorStateText.Describe(Table, state);
            Check.WithCustomMessage($"{state} tray text \"{text}\" is {text.Length} chars")
                .That(text.Length).IsLessOrEqualThan(63);
        }
    }
}
