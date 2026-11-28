namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;

/// <summary>
/// Wave D D1: the stranding ledger's lines and daily totals, driven headlessly over a temp folder
/// (never the owner's profile) with an injected local clock.
/// </summary>
public class StrandingLedgerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "eyectrl-strand-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _lines = new();
    private DateTime _now = new(2026, 11, 28, 9, 0, 0);

    private StrandingLedger NewLedger() => new(
        StrandingLedger.SessionMarkerPath(_dir),
        StrandingLedger.RefusedLaunchesPath(_dir),
        _lines.Add,
        () => _now);

    private string MarkerPath => StrandingLedger.SessionMarkerPath(_dir);

    private string ZeroSummary(string reason, string day) =>
        $"STRAND-E1 day_summary reason={reason} day={day} paused_ms=0 pauses=0 left_gaze=0 "
        + "left_other=0 backstage_ms=0 backstage_closes=0 bs_disc=0 bs_mouse=0 bs_esc=0 "
        + "bs_altf4=0 bs_card=0 bs_handoff=0 bs_shutdown=0 bs_message=0 bs_other=0 refused=0";

    [Fact]
    public void StartSession_ReportsAnUncleanPreviousSession()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(MarkerPath, "2026-11-27T22:00:00.000");

        using var ledger = NewLedger();
        ledger.StartSession();

        Check.That(_lines).ContainsExactly(
            "STRAND-E1 session_prev end=unclean prev_start=2026-11-27T22:00:00.000");
        Check.That(File.ReadAllText(MarkerPath)).IsEqualTo("2026-11-28T09:00:00.000");
    }

    [Fact]
    public void StartSession_WithoutAMarker_WritesNoPreviousLine()
    {
        using var ledger = NewLedger();
        ledger.StartSession();

        Check.That(_lines).IsEmpty();
        Check.That(File.Exists(MarkerPath)).IsTrue();
    }

    [Fact]
    public void EndSession_DeletesTheMarker_AndWritesTheZeroSummary()
    {
        using var ledger = NewLedger();
        ledger.StartSession();
        ledger.EndSession();

        Check.That(File.Exists(MarkerPath)).IsFalse();
        Check.That(_lines).ContainsExactly(ZeroSummary("shutdown", "2026-11-28"));
    }

    [Fact]
    public void PauseExit_NamesItsSourceAndDuration()
    {
        using var ledger = NewLedger();
        ledger.OnPauseEntered(StrandingLedger.PauseSources.Toolbar);
        _now = _now.AddSeconds(42.5);
        ledger.OnPauseExited(StrandingLedger.PauseSources.Strip);

        Check.That(_lines).ContainsExactly(
            "STRAND-E1 pause_exit src=strip paused_ms=42500");
    }

    [Fact]
    public void PauseExit_ToolbarAndStrip_CountAsGaze_TrayCountsAsOther()
    {
        using var ledger = NewLedger();
        ledger.OnPauseEntered(StrandingLedger.PauseSources.Toolbar);
        _now = _now.AddSeconds(10);
        ledger.OnPauseExited(StrandingLedger.PauseSources.Toolbar);

        ledger.OnPauseEntered(StrandingLedger.PauseSources.Strip);
        _now = _now.AddSeconds(5);
        ledger.OnPauseExited(StrandingLedger.PauseSources.Strip);

        ledger.OnPauseEntered(StrandingLedger.PauseSources.Tray);
        _now = _now.AddSeconds(2);
        ledger.OnPauseExited(StrandingLedger.PauseSources.Tray);

        _now = _now.AddSeconds(1);
        ledger.EndSession();

        string summary = _lines[^1];
        Check.That(summary).Contains("paused_ms=17000");
        Check.That(summary).Contains("pauses=3");
        Check.That(summary).Contains("left_gaze=2");
        Check.That(summary).Contains("left_other=1");
    }

    [Fact]
    public void BackstageOpenClose_NamesTheSourceAndDuration()
    {
        using var ledger = NewLedger();
        ledger.OnBackstageOpened(StrandingLedger.BackstageSources.Toolbar);
        _now = _now.AddSeconds(15.2);
        ledger.OnBackstageClosed(StrandingLedger.BackstageSources.Esc);

        Check.That(_lines).ContainsExactly(
            "STRAND-E1 backstage_open src=toolbar",
            "STRAND-E1 backstage_close src=esc open_ms=15200");

        _now = _now.AddSeconds(1);
        ledger.EndSession();
        Check.That(_lines[^1]).Contains("backstage_ms=15200");
        Check.That(_lines[^1]).Contains("backstage_closes=1");
        Check.That(_lines[^1]).Contains("bs_esc=1");
    }

    [Fact]
    public void Summary_ASessionSpanningMidnight_ReportsEachDaySeparately()
    {
        _now = new DateTime(2026, 11, 27, 23, 40, 0);
        using var ledger = NewLedger();
        ledger.StartSession();

        // The owner pauses at 23:50 and resumes at 00:10 — one pause across two local days.
        _now = new DateTime(2026, 11, 27, 23, 50, 0);
        ledger.OnPauseEntered(StrandingLedger.PauseSources.Tray);

        // The midnight timer reports the outgoing day while the pause is still open.
        _now = new DateTime(2026, 11, 28, 0, 0, 0);
        ledger.WriteDailySummary("midnight");
        string firstDay = _lines[^1];
        Check.That(firstDay).Contains("reason=midnight day=2026-11-27");
        Check.That(firstDay).Contains("paused_ms=600000");
        Check.That(firstDay).Contains("pauses=1");

        _now = new DateTime(2026, 11, 28, 0, 10, 0);
        ledger.OnPauseExited(StrandingLedger.PauseSources.Tray);
        Check.That(_lines[^1]).Contains("paused_ms=1200000");

        _now = new DateTime(2026, 11, 28, 1, 0, 0);
        ledger.EndSession();
        string secondDay = _lines[^1];
        Check.That(secondDay).Contains("reason=shutdown day=2026-11-28");
        Check.That(secondDay).Contains("paused_ms=600000");
        Check.That(secondDay).Contains("left_other=1");
    }

    [Fact]
    public void Summary_CountsRefusedSecondLaunches_OnTheirOwnDay()
    {
        StrandingLedger.RecordRefusedSecondLaunch(_dir, new DateTime(2026, 11, 27, 23, 0, 0));
        StrandingLedger.RecordRefusedSecondLaunch(_dir, new DateTime(2026, 11, 28, 8, 0, 0), "restart");

        using var ledger = NewLedger();
        ledger.StartSession();
        ledger.EndSession();

        Check.That(_lines[^1]).Contains("refused=1");
        Check.That(File.ReadAllLines(StrandingLedger.RefusedLaunchesPath(_dir))).HasSize(2);
    }

    [Fact]
    public void NextMidnight_IsTheFollowingLocalMidnight()
    {
        Check.That(StrandingLedger.NextMidnight(new DateTime(2026, 11, 28, 23, 59, 0)))
            .IsEqualTo(new DateTime(2026, 11, 29, 0, 0, 0));
        Check.That(StrandingLedger.NextMidnight(new DateTime(2026, 11, 28, 0, 0, 0)))
            .IsEqualTo(new DateTime(2026, 11, 29, 0, 0, 0));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A leaked temp folder must not fail the suite.
        }
    }
}
