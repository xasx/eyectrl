namespace EyeCtrl.Supervisor.Tests.Supervision;

using EyeCtrl.Supervisor.Supervision;

public class SupervisorStateTextTests
{
    private static readonly SupervisorState[] States = Enum.GetValues<SupervisorState>();

    [Fact]
    public void EveryState_MapsToADistinctNonEmptyPhrase()
    {
        var phrases = States.Select(SupervisorStateText.Describe).ToList();

        Check.That(States).HasSize(6);
        Check.That(phrases).ContainsOnlyElementsThatMatch(p => !string.IsNullOrWhiteSpace(p));
        Check.That(phrases.Distinct().Count()).IsEqualTo(States.Length);
    }

    [Fact]
    public void EveryStatesTrayText_FitsThe63CharShellCap()
    {
        // The shell truncates NotifyIcon.Text past 63 chars (SupervisorTrayIcon.SetStatusText), so the
        // "EyeCtrl Supervisor: " prefix counts toward the budget the phrases are written against.
        foreach (var state in States)
        {
            var text = SupervisorStateText.TrayPrefix + SupervisorStateText.Describe(state);
            Check.WithCustomMessage($"{state} tray text \"{text}\" is {text.Length} chars")
                .That(text.Length).IsLessOrEqualThan(63);
        }
    }
}
