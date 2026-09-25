namespace EyeCtrl.App.Tests.Startup;

using EyeCtrl.App.Startup;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Startup provenance of the paused state. On 2026-09-24 the app ran 1 h 43 min in Paused from a
/// dev <c>--paused</c> launch and the log never said so; these pin the line that names the state
/// and which of the three sources caused it.
/// </summary>
public class PauseLaunchProvenanceTests
{
    [Theory]
    [InlineData(false, false, false, PauseLaunchProvenance.NeitherSource)]
    [InlineData(true, false, true, PauseLaunchProvenance.PausedArgumentSource)]
    [InlineData(false, true, true, PauseLaunchProvenance.StartPausedSettingSource)]
    // --paused wins: it is the per-launch override and must not be masked by the setting.
    [InlineData(true, true, true, PauseLaunchProvenance.PausedArgumentSource)]
    public void Source_And_StartsPaused(bool pausedArg, bool startPausedSetting,
        bool expectStartsPaused, string expectSource)
    {
        Check.That(PauseLaunchProvenance.StartsPaused(pausedArg, startPausedSetting))
            .IsEqualTo(expectStartsPaused);
        Check.That(PauseLaunchProvenance.Source(pausedArg, startPausedSetting))
            .IsEqualTo(expectSource);
    }

    [Fact]
    public void Describe_NamesThePausedStateAndItsSource_ForEachCase()
    {
        Check.That(PauseLaunchProvenance.Describe(true, false)).IsEqualTo(
            "Startup paused state: paused (source: --paused argument)");
        Check.That(PauseLaunchProvenance.Describe(false, true)).IsEqualTo(
            "Startup paused state: paused (source: general.startPaused)");
        Check.That(PauseLaunchProvenance.Describe(false, false)).IsEqualTo(
            "Startup paused state: not paused (source: neither)");
    }

    [Fact]
    public void Apply_LogsTheLine_AndPauses_ForAPausedLaunch()
    {
        var interaction = new InteractionManager();
        var lines = new List<string>();

        PauseLaunchProvenance.Apply(isPausedLaunch: true, startPausedSetting: false,
            interaction, lines.Add);

        Check.That(lines).HasSize(1);
        Check.That(lines[0]).Contains(PauseLaunchProvenance.PausedArgumentSource);
        Check.That(interaction.CurrentState).IsEqualTo(InteractionState.Paused);
    }

    [Fact]
    public void Apply_LogsTheSettingSource_AndPauses_ForStartPaused()
    {
        var interaction = new InteractionManager();
        var lines = new List<string>();

        PauseLaunchProvenance.Apply(isPausedLaunch: false, startPausedSetting: true,
            interaction, lines.Add);

        Check.That(lines).HasSize(1);
        Check.That(lines[0]).Contains(PauseLaunchProvenance.StartPausedSettingSource);
        Check.That(interaction.CurrentState).IsEqualTo(InteractionState.Paused);
    }

    [Fact]
    public void Apply_LogsNeither_AndLeavesFreeLook_WhenNothingAsksForPause()
    {
        var interaction = new InteractionManager();
        var lines = new List<string>();

        PauseLaunchProvenance.Apply(isPausedLaunch: false, startPausedSetting: false,
            interaction, lines.Add);

        Check.That(lines).HasSize(1);
        Check.That(lines[0]).Contains(PauseLaunchProvenance.NeitherSource);
        Check.That(lines[0]).Contains("not paused");
        Check.That(interaction.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }
}
