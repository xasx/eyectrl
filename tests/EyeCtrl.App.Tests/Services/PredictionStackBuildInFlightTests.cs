namespace EyeCtrl.App.Tests.Services;

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
    private readonly Queue<Action> _uiQueue = new();

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
    /// including items enqueued while draining (the swap's end notification is posted from
    /// inside the marshal it follows).</summary>
    private void Pump()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_uiQueue.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Check.WithCustomMessage("no UI callback arrived within 10s — the swap never posted")
            .That(_uiQueue.Count).IsStrictlyGreaterThan(0);
        for (var i = 0; i < 100 && _uiQueue.Count > 0; i++)
            _uiQueue.Dequeue()();
    }

    /// <summary>Drains whatever is queued NOW, without waiting (for notifications that post
    /// synchronously on the caller's thread, like the build-start).</summary>
    private void Drain()
    {
        for (var i = 0; i < 100 && _uiQueue.Count > 0; i++)
            _uiQueue.Dequeue()();
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
    public void FailedBuild_ClearsTheInFlightState_NoSwapArrives()
    {
        // The predictor seam is the one deterministic failure point inside Build: an exception
        // there faults the background task, which is the exact path a broken model file takes
        // in production when the constructor itself throws.
        using var scope = ModelTestFakes.Create();
        var (registry, _) = scope.WithHashedDownloadedBundled();
        using var factory = new PredictionStackFactory(work => _uiQueue.Enqueue(work))
        {
            PredictorFactoryOverride = static (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var swaps = 0;
        factory.StackSwapped += (_, _) => swaps++;

        var settings = Settings();
        settings.Keyboard.MLSuggestionsEnabled = true;
        settings.Keyboard.MLModelSize = "135M";
        factory.Rebuild(settings, _baseDir, _appDataDir, registry);
        Check.That(factory.BuildInFlight).IsTrue();

        Pump();

        Check.WithCustomMessage(
                "a failed build left the in-flight state stuck — the loading hint would never " +
                "clear (model problems must stay log-only, never on-screen)")
            .That(factory.BuildInFlight).IsFalse();
        Check.That(swaps).IsEqualTo(0);
    }

    [Fact]
    public void SupersededBuildsEnding_DoesNotClearTheNewerBuildsInFlightState()
    {
        using var factory = NewBackgroundFactory();
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Pump(); // a serving stack exists (the production shape when settings are re-applied)

        // Build 1: the queue is empty (the start notification is raised directly), so the
        // next arrival is provably its marshal — its pool-thread generation check has passed.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_uiQueue.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Check.WithCustomMessage("build 1's marshal never enqueued")
            .That(_uiQueue.Count).IsStrictlyGreaterThan(0);

        // Build 2 supersedes build 1 while build 1's marshal sits in the queue.
        factory.Rebuild(Settings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(factory.BuildInFlight).IsTrue();

        _uiQueue.Dequeue()(); // build 1's stale marshal: its re-check discards the build
        Drain();             // plus any notification it posted, leaving the queue empty
        Check.WithCustomMessage(
                "an older build's end cleared the state while build 2 is still running — a " +
                "plain bool cleared by whoever finishes first has exactly this bug")
            .That(factory.BuildInFlight).IsTrue();

        Pump(); // build 2 finishes and swaps in (the queue is empty, so Pump genuinely waits)
        Check.That(factory.BuildInFlight).IsFalse();
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
