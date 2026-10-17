namespace EyeCtrl.LM.Tests;

using EyeCtrl.Core.Prediction;

/// <summary>
/// Headless, model-free gates for the generation orchestration: a predictor's ONNX session may
/// have only one Run in flight (the DirectML provider crashes otherwise), a superseded request
/// stops after its current step, and only the newest request's results surface. The real step
/// loop runs; the single step itself is replaced through <see cref="SmallLMPredictor.StepOverride"/>.
/// </summary>
public class SmallLMPredictorConcurrencyTests
{
    // The shared test vocab is character-level: w=7 and h=3 are single-token words.
    private const long WorldToken = 7;
    private const long HelloToken = 3;

    [Fact]
    public void Predict_OverlappingRequests_NeverRunTwoStepsAtOnce()
    {
        var tokenizer = CreateTokenizer(out var directory);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var maxGate = new object();
        var firstStep = 1;
        var active = 0;
        var maxActive = 0;
        SmallLMPredictor? predictor = null;

        try
        {
            predictor = SmallLMPredictor.CreateModelFree(tokenizer, (step, token) =>
            {
                var now = Interlocked.Increment(ref active);
                lock (maxGate) maxActive = Math.Max(maxActive, now);
                try
                {
                    if (Interlocked.Exchange(ref firstStep, 0) == 1)
                    {
                        // Hold the first request's step open while newer ones are queued.
                        entered.Set();
                        release.Wait(TimeSpan.FromSeconds(10));
                    }

                    return new SmallLMPredictor.StepOutput(WorldToken, 0.5);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

            predictor.Predict("first request");
            Check.That(entered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

            for (var i = 0; i < 5; i++)
                predictor.Predict($"request {i}");

            release.Set();

            Check.That(WaitForResults(predictor, TimeSpan.FromSeconds(10))).IsNotNull();
            Check.That(maxActive).IsEqualTo(1);
        }
        finally
        {
            release.Set();
            predictor?.Dispose();
            tokenizer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Predict_SupersededRequest_StopsAfterItsCurrentStep()
    {
        var tokenizer = CreateTokenizer(out var directory);
        var steps = new List<(int Request, int Step)>();
        SmallLMPredictor? predictor = null;
        var requestIndex = -1;

        try
        {
            predictor = SmallLMPredictor.CreateModelFree(tokenizer, (step, token) =>
            {
                if (step == 0) requestIndex++;
                var request = requestIndex;
                lock (steps) steps.Add((request, step));

                if (request == 0 && step == 0)
                    predictor!.Predict("second request");

                return request == 0
                    ? new SmallLMPredictor.StepOutput(WorldToken, 0.5)
                    : step < 2 ? new SmallLMPredictor.StepOutput(WorldToken, 0.5) : null;
            });

            predictor.Predict("first request");

            Check.That(WaitForResults(predictor, TimeSpan.FromSeconds(10))).IsNotNull();

            lock (steps)
            {
                Check.That(steps.Count(entry => entry.Request == 0)).IsEqualTo(1);
                // The third call is the one that returns null and ends the sequence.
                Check.That(steps.Where(entry => entry.Request == 1).Select(entry => entry.Step))
                    .ContainsExactly(0, 1, 2);
            }
        }
        finally
        {
            predictor?.Dispose();
            tokenizer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TryGetResults_AfterSupersession_ReturnsOnlyTheLatestRequestsResults()
    {
        var tokenizer = CreateTokenizer(out var directory);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        SmallLMPredictor? predictor = null;
        var requestIndex = -1;

        try
        {
            predictor = SmallLMPredictor.CreateModelFree(tokenizer, (step, token) =>
            {
                if (step == 0) requestIndex++;
                var request = requestIndex;

                if (request == 0)
                {
                    if (step == 0)
                    {
                        entered.Set();
                        release.Wait(TimeSpan.FromSeconds(10));
                    }

                    return new SmallLMPredictor.StepOutput(WorldToken, 0.5);
                }

                return step == 0
                    ? new SmallLMPredictor.StepOutput(HelloToken, 0.5)
                    : null;
            });

            predictor.Predict("first request");
            Check.That(entered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

            predictor.Predict("second request");

            // Nothing surfaces while a request is still in flight.
            Check.That(predictor.TryGetResults()).IsNull();

            release.Set();

            var results = WaitForResults(predictor, TimeSpan.FromSeconds(10));
            Check.That(results).IsNotNull();
            Check.That(results!).HasSize(1);
            Check.That(results![0].Word).IsEqualTo("h");
        }
        finally
        {
            release.Set();
            predictor?.Dispose();
            tokenizer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Dispose_DuringARun_WaitsForTheInFlightStep()
    {
        var tokenizer = CreateTokenizer(out var directory);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var predictor = SmallLMPredictor.CreateModelFree(tokenizer, (step, token) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new SmallLMPredictor.StepOutput(WorldToken, 0.5);
        });

        try
        {
            predictor.Predict("context");
            Check.That(entered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

            var disposal = Task.Run(predictor.Dispose);

            Check.That(disposal.Wait(TimeSpan.FromMilliseconds(250))).IsFalse();

            release.Set();
            Check.That(disposal.Wait(TimeSpan.FromSeconds(10))).IsTrue();
        }
        finally
        {
            release.Set();
            predictor.Dispose();
            tokenizer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static IReadOnlyList<PredictionResult>? WaitForResults(SmallLMPredictor predictor, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var results = predictor.TryGetResults();
            if (results != null) return results;
            Thread.Sleep(10);
        }

        return predictor.TryGetResults();
    }

    private static LMTokenizer CreateTokenizer(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), $"lm_concurrency_{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "tokenizer.json");
        TestTokenizerJson.Write(path);
        return new LMTokenizer(path);
    }
}
