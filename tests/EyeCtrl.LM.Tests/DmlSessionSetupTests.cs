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
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => appends.Add("chosen"), Adapter, IsDefault: false),
            new(options => appends.Add("default"), null, IsDefault: true),
        };

        FakeSession CreateSession(string path, SessionOptions options)
        {
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
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => { }, Adapter, IsDefault: false),
            new(options => { }, null, IsDefault: true),
        };

        FakeSession CreateSession(string path, SessionOptions options)
        {
            created.Add("chosen");
            return new FakeSession("chosen");
        }

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, CreateSession, logger, "1.5B");

        Check.That(session.Name).IsEqualTo("chosen");
        Check.That(created).HasSize(1);
        Check.That(logger.Entries).HasSize(1);
        Check.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information);
        Check.That(logger.Entries[0].Message)
            .IsEqualTo($"Using DirectML (GPU) for 1.5B on {Adapter}");
    }

    [Fact]
    public void CreateSessionWithFallback_DefaultAttemptSucceeds_LogsTheDefaultAdapter()
    {
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => { }, null, IsDefault: true),
        };

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, (path, options) => new FakeSession("default"), logger, "1.5B");

        Check.That(session.Name).IsEqualTo("default");
        Check.That(logger.Entries).HasSize(1);
        Check.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information);
        Check.That(logger.Entries[0].Message)
            .IsEqualTo("Using DirectML (GPU) for 1.5B on the default adapter");
    }

    [Fact]
    public void CreateSessionWithFallback_ChosenAppendThrows_FallsBackToTheDefault()
    {
        var logger = new CapturingLogger();
        var attempts = new GpuAttempt[]
        {
            new(options => throw new InvalidOperationException("invalid adapter handle"), Adapter, IsDefault: false),
            new(options => { }, null, IsDefault: true),
        };

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", attempts, (path, options) => new FakeSession("default"), logger, "1.5B");

        Check.That(session.Name).IsEqualTo("default");
        Check.That(logger.Entries).HasSize(2);
        Check.That(logger.Entries[0].Exception).IsNotNull();
        Check.That(logger.Entries[1].Message)
            .IsEqualTo("Using DirectML (GPU) for 1.5B on the default adapter");
    }

    [Fact]
    public void CreateSessionWithFallback_NoGpuAttempts_UsesTheCpuSilently()
    {
        var logger = new CapturingLogger();

        var session = DmlSessionSetup.CreateSessionWithFallback(
            "model.onnx", [], (path, options) => new FakeSession("cpu"), logger, "1.5B");

        Check.That(session.Name).IsEqualTo("cpu");
        Check.That(logger.Entries).IsEmpty();
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
