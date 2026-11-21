namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.Logging;

/// <summary>
/// D4's wiring into the keyboard: the show/hide check logs the EVENT-DRIVEN verdict beside the one-shot
/// poll, reads the foreground process name, runs off the caller's thread, and — the pin that matters most —
/// <b>never changes the verdict</b>. The detector stays the sole authority.
/// </summary>
public class KeyboardSensitiveCheckWiringTests
{
    private sealed class Capture : ILogger<KeyboardViewModel>
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _lines.Add(formatter(state, exception));
        }

        public string[] Snapshot() { lock (_gate) return _lines.ToArray(); }
    }

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class FakePoll : IFocusedPasswordPoll
    {
        public Func<bool?> Read = () => null;
        public int Reads;

        public bool? ReadIsPassword()
        {
            Interlocked.Increment(ref Reads);
            return Read();
        }
    }

    private sealed class FakeProbe : IKeyboardContextProbe
    {
        public string? GetForegroundProcessName() => "WINWORD.exe";
        public string? ReadFocusedTextTail(int maxChars) => null;
    }

    private static KeyboardViewModel New(Capture log, ISensitiveContextDetector sensitive,
        IFocusedPasswordPoll poll) =>
        new(new EyeCtrlSettings(), new WeakReferenceMessenger(), logger: log, contextProbe: new FakeProbe(),
            sensitiveContext: sensitive, sensitivePoll: poll);

    [Fact]
    public async Task AShowCheck_LogsTheEventVerdictThePollAndTheForegroundProcess()
    {
        var log = new Capture();
        var vm = New(log, new FakeSensitive { IsSensitiveActive = true }, new FakePoll { Read = () => false });

        await vm.BeginSensitiveCheck("show").WaitAsync(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Check.That(log.Snapshot()).ContainsExactly(
            "Sensitive check at show: event=true poll=false proc=WINWORD.exe");
    }

    [Fact]
    public async Task ThePollNeverChangesTheVerdict_InEitherDirection()
    {
        // MUTANT PIN: feeding the poll's answer into the verdict must turn this RED.
        var sensitive = new FakeSensitive { IsSensitiveActive = true };
        var vm = New(new Capture(), sensitive, new FakePoll { Read = () => false });
        await vm.BeginSensitiveCheck("show").WaitAsync(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Check.That(vm.IsSensitiveContext).IsTrue();      // event said password, poll said not: event wins

        var notSensitive = new FakeSensitive { IsSensitiveActive = false };
        var vm2 = New(new Capture(), notSensitive, new FakePoll { Read = () => true });
        await vm2.BeginSensitiveCheck("hide").WaitAsync(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Check.That(vm2.IsSensitiveContext).IsFalse();    // event said not, poll said password: event wins
    }

    [Fact]
    public async Task TheCheck_DoesNotPollOnTheCallersThread()
    {
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var poll = new FakePoll
        {
            Read = () =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(60));
                return true;
            },
        };
        var vm = New(new Capture(), new FakeSensitive(), poll);

        try
        {
            var call = Task.Run<Task>(() => vm.BeginSensitiveCheck("show"));
            Check.That(started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).IsTrue();
            var check = await call.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // CAUSAL: the read cannot end while `release` is unset, so a Begin that already returned did not
            // wait for it, and the check's own task is still outstanding.
            Check.WithCustomMessage("BeginSensitiveCheck must return before the poll answers")
                .That(check.IsCompleted).IsFalse();
        }
        finally
        {
            release.Set();
        }
    }
}
