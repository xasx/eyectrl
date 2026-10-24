namespace EyeCtrl.App.Tests.Services;

using System.Collections.Concurrent;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.Core.Settings;

/// <summary>
/// The build-in-flight contract behind the prediction loading hint (lm-loading-hint ticket):
/// the state is true while a background build runs, false the moment that build ends in ANY
/// way — swap, superseded, failed, cancelled, disposed — and an older, superseded build's end
/// must not clear a newer build's tenure. The UI marshal is faked with the manual queue of
/// <see cref="PredictionStackBackgroundTests"/> so tests control exactly when "the UI thread"
/// runs and can observe the notification order.
/// </summary>
public class PredictionStackBuildInFlightTests : IDisposable
{
    private readonly string _appDataDir;
    private readonly string _baseDir;
    // Concurrent, not Queue<Action>: the factory's marshal enqueues from POOL threads (a
    // background build may finish at any moment) while the test thread reads and dequeues —
    // a plain Queue<T> can corrupt or misread under that race (coordinator review of
    // 6f4adbf8: another flake source).
    private readonly ConcurrentQueue<Action> _uiQueue = new();

    public PredictionStackBuildInFlightTests()
    {
        _appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl-psf-bif-" + Guid.NewGuid().ToString("N"));
        _baseDir = Path.Combine(_appDataDir, "base");
        Directory.CreateDirectory(_appDataDir);
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_appDataDir, recursive: true); } catch (IOException) { }
    }

    private static EyeCtrlSettings Settings()
    {
        var s = new EyeCtrlSettings();
        s.Keyboard.PredictionEnabled = true;
        s.Keyboard.LanguagePack = "en";
        s.Keyboard.MLSuggestionsEnabled = false;
        return s;
    }

    private PredictionStackFactory NewBackgroundFactory() =>
        new(work => _uiQueue.Enqueue(work));

    /// <summary>Blocks until at least one UI callback has arrived, then drains the queue —
    /// including items enqueued while draining (the swap's end notification is raised from
    /// inside the marshal it follows).</summary>
    private void Pump()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_uiQueue.IsEmpty && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Check.WithCustomMessage("no UI callback arrived within 10s — the swap never posted")
            .That(_uiQueue.IsEmpty).IsFalse();
        for (var i = 0; i < 100 && _uiQueue.TryDequeue(out var run); i++)
            run();
    }

    /// <summary>Drains whatever is queued NOW, without waiting (for paths that marshal
    /// synchronously or are expected to post nothing).</summary>
    private void Drain()
    {
        for (var i = 0; i < 100 && _uiQueue.TryDequeue(out var run); i++)
            run();
    }

    [Fact]
    public void FirstBuild_IsInFlightUntilTheSwap_AndNotifiesOnTheUiThread()
    {
        using var factory = NewBackgroundFactory();
        var seen = new List<bool>();
        factory.BuildInFlightChanged += seen.Add;

        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);

        // The start notification is raised directly on Rebuild's calling (UI) thread — no
        // marshal hop, nothing to pump for it.
        Check.That(factory.BuildInFlight).IsTrue();
        Check.That(seen).ContainsExactly(new[] { true });

        Pump(); // the build finishes and the swap lands

        Check.WithCustomMessage(
                "the swap did not clear the in-flight state — the loading hint would stay up " +
                "over the suggestions it announced")
            .That(factory.BuildInFlight).IsFalse();
        Check.That(seen).ContainsExactly(true, false);
    }

    [Fact]
    public void FailedBuild_ClearsTheInFlightState_NoSwapArrives_AndNotifies()
    {
        // The predictor seam is the one deterministic failure point inside Build: an exception
        // there faults the background task, which is the exact path a broken model file takes
        // in production when the constructor itself throws. Review 01 F3: the END
        // NOTIFICATION is asserted directly (the window clears the hint from the event, not
        // from the state) — not incidentally via a generic pump timeout.
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        using var factory = new PredictionStackFactory(work => _uiQueue.Enqueue(work))
        {
            PredictorFactoryOverride = static (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var swaps = 0;
        factory.StackSwapped += (_, _) => swaps++;
        var seen = new List<bool>();
        factory.BuildInFlightChanged += seen.Add;

        var settings = Settings();
        settings.Keyboard.MLSuggestionsEnabled = true;
        settings.Keyboard.MLModelSize = "135M";
        factory.Rebuild(settings, _baseDir, _appDataDir, registry);
        Check.That(factory.BuildInFlight).IsTrue();

        // The end path ran: the state cleared. The CAS sits UPSTREAM of the notification in
        // the same pool-thread slice, so this also proves the fault continuation executed —
        // fast, no timeout guessing.
        var cleared = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (factory.BuildInFlight && DateTime.UtcNow < cleared)
            Thread.Sleep(10);
        Check.WithCustomMessage(
                "a failed build left the in-flight state stuck — the loading hint would never " +
                "clear (model problems must stay log-only, never on-screen)")
            .That(factory.BuildInFlight).IsFalse();

        // Deliver whatever the end posted (the enqueue rides the same slice as the CAS; the
        // short grace covers only cross-thread visibility).
        var delivered = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while ((!seen.Contains(false) || _uiQueue.Count > 0) && DateTime.UtcNow < delivered)
        {
            Drain();
            Thread.Sleep(10);
        }

        Check.WithCustomMessage(
                "the failed build's end cleared the state but never NOTIFIED — the hint would " +
                "be stuck on, and this test must fail on the notification, not on a pump " +
                "timeout (review 01 F3 / mutant N1)")
            .That(seen.Contains(false)).IsTrue();
        Check.That(swaps).IsEqualTo(0);
    }

    [Fact]
    public void AStaleEndDeliveredAfterANewerStart_ReportsTheCurrentState()
    {
        // Review 01 F1 / probe P1: build 1 FAULTS and its end post sits in the queue while
        // build 2 starts. A snapshot post would deliver a stale false AFTER build 2's start —
        // the window would believe nothing is in flight and the newer FIRST build would get
        // no hint for its whole build. The delivery must be level-triggered: it reads the
        // CURRENT state, so the worst it can send is a redundant true (the window
        // de-duplicates), never a lie.
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        using var factory = new PredictionStackFactory(work => _uiQueue.Enqueue(work))
        {
            PredictorFactoryOverride = static (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var seen = new List<bool>();
        factory.BuildInFlightChanged += seen.Add;

        var settings = Settings();
        settings.Keyboard.MLSuggestionsEnabled = true;
        settings.Keyboard.MLModelSize = "135M";

        factory.Rebuild(settings, _baseDir, _appDataDir, registry); // build 1: will fault
        var posted = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_uiQueue.IsEmpty && DateTime.UtcNow < posted)
            Thread.Sleep(10);
        // Build 1's end post is the ONLY item that can be queued here: its start was raised
        // directly, a faulted build raises no marshal, and build 2 does not exist yet.
        Check.WithCustomMessage("build 1's end never posted, or something unexpected is queued")
            .That(_uiQueue.Count).IsEqualTo(1);

        // Build 2 starts while build 1's stale end still sits in the queue. Its start
        // notification is raised directly, BEFORE this Rebuild returns. Build 2 may finish on
        // the pool at any moment — harmless: its marshal sits in the queue behind the stale
        // end and runs only when this test pumps.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(factory.BuildInFlight).IsTrue();

        // Deliver EXACTLY build 1's stale end, under build 2's tenure. Dequeueing one item
        // (not Drain) is the determinism fix: a Drain could also run build 2's marshal, whose
        // swap legitimately clears the state — the mid-test assertion below would then race
        // build 2's completion (the flake the UX team saw).
        Check.That(_uiQueue.TryDequeue(out var staleEnd)).IsTrue();
        staleEnd!();

        Check.WithCustomMessage(
                "a stale end notification lied about the current state (a stale end-delivery " +
                "false after a newer start) — the newer first build would show no hint " +
                "(review 01 F1)")
            .That(seen[^1]).IsTrue();
        Check.WithCustomMessage("build 2 is still the in-flight build — the state must hold")
            .That(factory.BuildInFlight).IsTrue();

        // Now let build 2's own end land: the swap marshals, the state clears, and the last
        // notification tells the truth about it.
        Pump();
        Check.That(factory.BuildInFlight).IsFalse();
        Check.WithCustomMessage("build 2's swap end must notify false")
            .That(seen[^1]).IsFalse();
    }

    [Fact]
    public void SupersededBuildsEnding_DoesNotClearTheNewerBuildsInFlightState()
    {
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump(); // a serving stack exists (the production shape when settings are re-applied)
        var seen = new List<bool>();
        factory.BuildInFlightChanged += seen.Add;

        // Build 1: the queue is empty (the start notification is raised directly), so the
        // next arrival is provably its marshal — its pool-thread generation check has passed.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_uiQueue.IsEmpty && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Check.WithCustomMessage("build 1's marshal never enqueued")
            .That(_uiQueue.Count).IsStrictlyGreaterThan(0);

        // Build 2 supersedes build 1 while build 1's marshal sits in the queue.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(factory.BuildInFlight).IsTrue();

        Check.That(_uiQueue.TryDequeue(out var staleMarshal)).IsTrue();
        staleMarshal!(); // build 1's stale marshal: its re-check discards the build
        // No Drain here (the flake discipline): build 2's marshal may already sit behind
        // this one, and running it would clear the state mid-test. The discarded build's
        // MarkBuildEnded CAS-fails and posts nothing, so there is nothing to clean up.
        Check.WithCustomMessage(
                "an older build's end cleared the state while build 2 is still running — a " +
                "plain bool cleared by whoever finishes first has exactly this bug")
            .That(factory.BuildInFlight).IsTrue();
        Check.WithCustomMessage("the superseded build's discard must notify nothing")
            .That(seen.Contains(false)).IsFalse();

        Pump(); // build 2 finishes and swaps in (the queue is empty, so Pump genuinely waits)
        Check.That(factory.BuildInFlight).IsFalse();
        // Review 01 F3's other arm: the supersede flow's end — the winner's swap — must
        // NOTIFY (the assertion, not the pump, carries the failure).
        Check.WithCustomMessage(
                "the winning swap's end never notified — subscribers must hear the transition")
            .That(seen[^1]).IsFalse();
    }

    [Fact]
    public void Dispose_ClearsTheInFlightStateImmediately()
    {
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(factory.BuildInFlight).IsTrue();

        factory.Dispose();

        Check.That(factory.BuildInFlight).IsFalse();
    }

    [Fact]
    public void PredictionDisabledEarlyOut_StartsNoBuild_LeavesTheStateClear()
    {
        using var factory = NewBackgroundFactory();
        var disabled = Settings();
        disabled.Keyboard.PredictionEnabled = false;

        factory.Rebuild(disabled, _baseDir, _appDataDir, modelRegistry: null);

        // Behaviour 5: prediction turned off swaps to null with no build in flight — no hint.
        Check.That(factory.BuildInFlight).IsFalse();
    }

    [Fact]
    public void SynchronousMode_NeverReportsABuildInFlight()
    {
        // No marshal → the historical inline build: the caller holds the result before any
        // subscriber could observe the state, so the hint machinery must stay quiet.
        using var factory = new PredictionStackFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(factory.BuildInFlight).IsFalse();
    }
}
