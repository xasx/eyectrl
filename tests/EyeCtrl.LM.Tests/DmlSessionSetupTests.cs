namespace EyeCtrl.LM.Tests;

using EyeCtrl.LM;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

public class DmlSessionSetupTests
{
    private const string Adapter = "NVIDIA GeForce RTX 5090 Laptop GPU";

    [Fact]
    public void CreateSessionWithFallback_ChosenDeviceFails_FallsBackToTheDefaultThenTheCpu()
    {
        var appends = new List<string>();
        var created = new List<string>();
        var levels = new List<GraphOptimizationLevel>();
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => appends.Add("chosen"), Adapter, IsDefault: false),
            new(options => appends.Add("default"), null, IsDefault: true),
        };

        FakeSession CreateSession(string path, SessionOptions options)
        {
            levels.Add(options.GraphOptimizationLevel);
            var name = created.Count switch
            {
                0 => "chosen",
                1 => "default",
                _ => "cpu",
            };
            created.Add(name);
            if (name != "cpu") throw new InvalidOperationException($"{name} attempt failed");
            return new FakeSession(name);
        }

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, CreateSession, logger, "1.5B");

        Check.That(session.Name).IsEqualTo("cpu");
        Check.That(created).ContainsExactly("chosen", "default", "cpu");
        Check.That(appends).ContainsExactly("chosen", "default");
        Check.That(levels).Not.IsEmpty();
        Check.That(levels).ContainsOnlyElementsThatMatch(level => level == GraphOptimizationLevel.ORT_ENABLE_ALL);
        Check.That(logger.Entries).HasSize(2);
        Check.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
        Check.That(logger.Entries[0].Exception).IsNotNull();
        Check.That(logger.Entries[0].Message)
            .IsEqualTo($"DirectML on {Adapter} failed, falling back to the default adapter");
        Check.That(logger.Entries[1].Level).IsEqualTo(LogLevel.Warning);
        Check.That(logger.Entries[1].Exception).IsNotNull();
        Check.That(logger.Entries[1].Message).IsEqualTo("DirectML not available, falling back to CPU");
    }

    [Fact]
    public void CreateSessionWithFallback_ChosenDeviceSucceeds_LogsTheAdapter()
    {
        var created = new List<string>();
        var levels = new List<GraphOptimizationLevel>();
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => { }, Adapter, IsDefault: false),
            new(options => { }, null, IsDefault: true),
        };

        FakeSession CreateSession(string path, SessionOptions options)
        {
            levels.Add(options.GraphOptimizationLevel);
            created.Add("chosen");
            return new FakeSession("chosen");
        }

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, CreateSession, logger, "1.5B");

        Check.That(session.Name).IsEqualTo("chosen");
        Check.That(created).HasSize(1);
        Check.That(levels).ContainsOnlyElementsThatMatch(level => level == GraphOptimizationLevel.ORT_ENABLE_ALL);
        Check.That(logger.Entries).HasSize(1);
        Check.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information);
        Check.That(logger.Entries[0].Message)
            .IsEqualTo($"Using DirectML (GPU) for 1.5B on {Adapter}");
    }

    [Fact]
    public void CreateSessionWithFallback_DefaultAttemptSucceeds_LogsTheDefaultAdapter()
    {
        var levels = new List<GraphOptimizationLevel>();
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => { }, null, IsDefault: true),
        };

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, (path, options) =>
            {
                levels.Add(options.GraphOptimizationLevel);
                return new FakeSession("default");
            }, logger, "1.5B");

        Check.That(session.Name).IsEqualTo("default");
        Check.That(levels).ContainsOnlyElementsThatMatch(level => level == GraphOptimizationLevel.ORT_ENABLE_ALL);
        Check.That(logger.Entries).HasSize(1);
        Check.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information);
        Check.That(logger.Entries[0].Message)
            .IsEqualTo("Using DirectML (GPU) for 1.5B on the default adapter");
    }

    [Fact]
    public void CreateSessionWithFallback_ChosenAppendThrows_FallsBackToTheDefault()
    {
        var levels = new List<GraphOptimizationLevel>();
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => throw new InvalidOperationException("invalid adapter handle"), Adapter, IsDefault: false),
            new(options => { }, null, IsDefault: true),
        };

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, (path, options) =>
            {
                levels.Add(options.GraphOptimizationLevel);
                return new FakeSession("default");
            }, logger, "1.5B");

        Check.That(session.Name).IsEqualTo("default");
        Check.That(levels).ContainsOnlyElementsThatMatch(level => level == GraphOptimizationLevel.ORT_ENABLE_ALL);
        Check.That(logger.Entries).HasSize(2);
        Check.That(logger.Entries[0].Exception).IsNotNull();
        Check.That(logger.Entries[1].Message)
            .IsEqualTo("Using DirectML (GPU) for 1.5B on the default adapter");
    }

    [Fact]
    public void CreateSessionWithFallback_NoGpuAttempts_UsesTheCpuSilently()
    {
        var levels = new List<GraphOptimizationLevel>();
        var logger = new CapturingLogger();

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", [], (path, options) =>
            {
                levels.Add(options.GraphOptimizationLevel);
                return new FakeSession("cpu");
            }, logger, "1.5B");

        Check.That(session.Name).IsEqualTo("cpu");
        Check.That(levels).ContainsOnlyElementsThatMatch(level => level == GraphOptimizationLevel.ORT_ENABLE_ALL);
        Check.That(logger.Entries).IsEmpty();
    }

    [Fact]
    public void CreateSessionWithFallback_UnnamedChosenAdapter_LogsTheFallbackName()
    {
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => { }, null, IsDefault: false),
        };

        DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, (path, options) => new FakeSession("chosen"), logger, "1.5B");

        Check.That(logger.Entries).HasSize(1);
        Check.That(logger.Entries[0].Message)
            .IsEqualTo("Using DirectML (GPU) for 1.5B on the chosen adapter");
    }

    [Fact]
    public void CreateSessionWithFallback_UnnamedChosenAdapterFails_NamesItInTheWarning()
    {
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => throw new InvalidOperationException("no adapter"), null, IsDefault: false),
            new(options => { }, null, IsDefault: true),
        };

        DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, (path, options) => new FakeSession("default"), logger, "1.5B");

        Check.That(logger.Entries).HasSize(2);
        Check.That(logger.Entries[0].Message)
            .IsEqualTo("DirectML on the chosen adapter failed, falling back to the default adapter");
    }

    [Fact]
    public void SessionOptions_DefaultGraphOptimizationLevel_IsEnableAll()
    {
        // Pins why the explicit level in CreateSessionWithFallback is a guard rather than a
        // behaviour change: ORT's own default is already ORT_ENABLE_ALL.
        using var options = new SessionOptions();

        Check.That(options.GraphOptimizationLevel).IsEqualTo(GraphOptimizationLevel.ORT_ENABLE_ALL);
    }

    [Fact]
    public void BuildGpuAttempts_WithAChoice_TriesTheChosenDeviceThenTheDefault()
    {
        var chosen = new GpuChoice(_ => { }, Adapter);

        var attempts = DmlSessionSetup.BuildGpuAttempts(chosen, gpuPreference: null);

        Check.That(attempts).HasSize(2);
        Check.That(attempts[0].IsDefault).IsEqualTo(false);
        Check.That(attempts[0].AdapterName).IsEqualTo(Adapter);
        Check.That(attempts[1].IsDefault).IsEqualTo(true);
    }

    [Fact]
    public void BuildGpuAttempts_PowerSavingPreference_SkipsTheChosenDevice()
    {
        var chosen = new GpuChoice(_ => { }, Adapter);

        var attempts = DmlSessionSetup.BuildGpuAttempts(chosen, WindowsGpuPreference.PowerSaving);

        Check.That(attempts).HasSize(1);
        Check.That(attempts[0].IsDefault).IsEqualTo(true);
    }

    [Fact]
    public void BuildGpuAttempts_HighPerformancePreference_KeepsTheChosenDevice()
    {
        var chosen = new GpuChoice(_ => { }, Adapter);

        var attempts = DmlSessionSetup.BuildGpuAttempts(chosen, gpuPreference: 2);

        Check.That(attempts).HasSize(2);
        Check.That(attempts[0].IsDefault).IsEqualTo(false);
        Check.That(attempts[1].IsDefault).IsEqualTo(true);
    }

    [Fact]
    public void BuildGpuAttempts_WithoutAChoice_TriesOnlyTheDefault()
    {
        var attempts = DmlSessionSetup.BuildGpuAttempts(null, gpuPreference: null);

        Check.That(attempts).HasSize(1);
        Check.That(attempts[0].IsDefault).IsEqualTo(true);
    }

    private sealed class FakeSession(string name)
    {
        public string Name { get; } = name;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
